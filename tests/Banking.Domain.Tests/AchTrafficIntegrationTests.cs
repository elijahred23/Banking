using System.Collections.Concurrent;
using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class AchTrafficIntegrationTests
{
    [Fact, Trait("Category", "ExternalIntegration")]
    public async Task Generated_ach_runs_round_trip_through_nacha_operator_and_balanced_accounting()
    {
        if (Environment.GetEnvironmentVariable("BANKING_RUN_EXTERNAL_INTEGRATION") != "1") return;
        var name = $"BankingAchTrafficTest_{Guid.NewGuid():N}";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = $"Server=localhost,11433;Database={name};User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True",
            ["RabbitMq:HostName"] = "localhost", ["RabbitMq:UserName"] = "taskwrapper",
            ["RabbitMq:Password"] = "taskwrapper123", ["Ach:BatchCutoffSeconds"] = "1"
        }).Build();
        var collection = new ServiceCollection().AddLogging();
        collection.AddSingleton<IConfiguration>(configuration);
        await using var services = collection.AddBankingInfrastructure(configuration).BuildServiceProvider();
        var factory = services.GetRequiredService<IDbContextFactory<BankingDbContext>>();
        var bus = new IsolatedBus(services.GetRequiredService<IMessageBus>(), $"ach-test.{Guid.NewGuid():N}.");
        var workers = new List<IHostedService>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await services.InitializeDatabaseAsync(seed: true);
            await using (var db = factory.CreateDbContext())
            {
                var clock = await db.SimulationStates.SingleAsync(x => x.Name == "default");
                clock.CurrentUtc = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
                await db.SaveChangesAsync();
            }
            // Repeated startup must preserve the expanded population and all opening balances.
            await services.InitializeDatabaseAsync(seed: true);
            // SQL repeatable seed and startup catalog must agree, including on existing databases.
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "database/seeds/002_traffic_population.sql"))) root = root.Parent;
            Assert.NotNull(root);
            var sqlSeed = await File.ReadAllTextAsync(Path.Combine(root.FullName, "database/seeds/002_traffic_population.sql"));
            await using (var db = factory.CreateDbContext())
            {
                var savings = await db.Accounts.SingleAsync(x => x.AccountNumber == TrafficPopulation.AccountNumber("101000019", 12));
                savings.Balance = 123.45m;
                await db.SaveChangesAsync();
                await db.Database.ExecuteSqlRawAsync(sqlSeed);
                await db.Database.ExecuteSqlRawAsync(sqlSeed);
                Assert.Equal(82, await db.Accounts.CountAsync());
                Assert.Equal(50, await db.Customers.CountAsync());
                await db.Entry(savings).ReloadAsync();
                Assert.Equal(123.45m, savings.Balance);
            }
            workers.Add(new Banking.AchService.Worker(bus, factory, new NachaFileWriter(), configuration,
                services.GetRequiredService<SimulationStateService>(), NullLogger<Banking.AchService.Worker>.Instance));
            workers.Add(new Banking.FedAchSimulator.Worker(bus, factory, new NachaFileParser(),
                NullLogger<Banking.FedAchSimulator.Worker>.Instance));
            workers.Add(new Banking.MessageManager.Worker(bus, factory, NullLogger<Banking.MessageManager.Worker>.Instance));
            workers.Add(new OutboxPublisher(factory, bus, NullLogger<OutboxPublisher>.Instance));
            foreach (var worker in workers) await worker.StartAsync(timeout.Token);
            foreach (var mode in new[] { AchTrafficMode.Payroll, AchTrafficMode.Suppliers, AchTrafficMode.Utilities })
            {
                List<AchEntry> generated;
                Dictionary<Guid, decimal> balances;
                Dictionary<Guid, decimal> cash;
                await using (var db = factory.CreateDbContext())
                {
                    var bank = await db.Banks.SingleAsync(x => x.RoutingNumber == "101000019");
                    var number = TrafficPopulation.AccountNumber(bank.RoutingNumber,
                        mode == AchTrafficMode.Payroll ? 1 : mode == AchTrafficMode.Suppliers ? 2 : 3);
                    var company = await db.Accounts.Include(x => x.Customer).ThenInclude(x => x.Bank)
                        .SingleAsync(x => x.AccountNumber == number);
                    var candidates = await db.Accounts.Include(x => x.Customer).ThenInclude(x => x.Bank)
                        .Where(x => x.Customer.BankId != bank.Id).ToListAsync();
                    var receivers = candidates.Where(x => mode == AchTrafficMode.Suppliers
                        ? TrafficPopulation.IsSupplier(x) : TrafficPopulation.IsPersonChecking(x)).Take(4).ToList();
                    generated = AchTraffic.CreateRun(company, receivers, mode, new DateOnly(2026, 10, 26),
                        DateTimeOffset.UtcNow, new Random(42)).ToList();
                    Assert.NotEmpty(generated);
                    balances = await db.Accounts.ToDictionaryAsync(x => x.Id, x => x.Balance);
                    cash = await db.Banks.ToDictionaryAsync(x => x.Id, x => x.MasterAccountBalance);
                    db.AchEntries.AddRange(generated);
                    await db.SaveChangesAsync();
                    foreach (var entry in generated)
                    {
                        AchTraffic.Approve(entry, DateTimeOffset.UtcNow);
                        db.OutboxMessages.Add(Outbox.Message(Queues.AchEntryCreated, new AchEntryCreated(entry.Id), bank.Id));
                    }
                    await db.SaveChangesAsync();
                }
                var ids = generated.Select(x => x.Id).ToArray();
                var posted = false;
                while (!posted)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    await using var db = factory.CreateDbContext();
                    posted = await db.AchEntries.CountAsync(x => ids.Contains(x.Id) && x.Status == AchEntryStatus.Posted) == ids.Length;
                    if (!posted) await Task.Delay(100, timeout.Token);
                }
                await using (var db = factory.CreateDbContext())
                {
                    var entries = await db.AchEntries.Include(x => x.Batch).ThenInclude(x => x!.File)
                        .Where(x => ids.Contains(x.Id)).ToListAsync();
                    foreach (var entry in entries)
                    {
                        Assert.True(new NachaFileParser().Validate(entry.Batch!.File!.RawNachaPayload).IsValid);
                        Assert.Equal("Settled", entry.Batch.File.Status);
                        Assert.NotEqual(entry.CreatedBy, entry.ApprovedBy);
                        var receiver = await db.Accounts.Include(x => x.Customer).SingleAsync(x =>
                            x.Customer.BankId == entry.ReceivingBankId && x.AccountNumber == entry.ReceivingAccountNumber);
                        var sign = AchRules.IsCredit(entry.TransactionCode) ? 1m : -1m;
                        Assert.Equal(balances[receiver.Id] + sign * entry.Amount, receiver.Balance);
                        var lines = await db.AchLedgerEntries.Where(x => x.AchEntryId == entry.Id).ToListAsync();
                        Assert.Equal(4, lines.Count);
                        Assert.True(JournalControl.IsBalanced(lines.Select(x => new JournalLine(x.Debit, x.Credit))));
                    }
                    var expectedCash = new Dictionary<Guid, decimal>(cash);
                    foreach (var entry in generated)
                    {
                        var sign = AchRules.IsCredit(entry.TransactionCode) ? 1m : -1m;
                        expectedCash[entry.OriginatingBankId] -= sign * entry.Amount;
                        expectedCash[entry.ReceivingBankId!.Value] += sign * entry.Amount;
                    }
                    foreach (var bank in await db.Banks.ToListAsync())
                        Assert.Equal(expectedCash[bank.Id], bank.MasterAccountBalance);
                    var origin = await db.Accounts.SingleAsync(x => x.Id == generated[0].OriginatingAccountId);
                    var direction = mode == AchTrafficMode.Utilities ? 1m : -1m;
                    Assert.Equal(balances[origin.Id] + direction * generated.Sum(x => x.Amount), origin.Balance);
                    Assert.Equal(0m, origin.HeldBalance);
                    Assert.Equal(balances.Values.Sum(), await db.Accounts.SumAsync(x => x.Balance));
                    Assert.Equal(cash.Values.Sum(), await db.Banks.SumAsync(x => x.MasterAccountBalance));
                    // A fresh transport delivery of an already settled result must not post twice.
                    foreach (var fileGroup in entries.GroupBy(x => x.Batch!.AchFileId!.Value))
                        await bus.PublishAsync(Queues.AchInbound, new AchOperatorResult(fileGroup.Key, true,
                            "Duplicate final result", fileGroup.Select(x => x.Id).ToArray()), timeout.Token,
                            $"duplicate-{fileGroup.Key:N}");
                }
                var duplicateIds = new List<string>();
                await using (var db = factory.CreateDbContext())
                    duplicateIds = await db.AchEntries.Where(x => ids.Contains(x.Id))
                        .Select(x => x.Batch!.AchFileId!.Value).Distinct().Select(x => "duplicate-" + x.ToString("N")).ToListAsync();
                var acknowledged = false;
                while (!acknowledged)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    await using var db = factory.CreateDbContext();
                    acknowledged = await db.ConsumerInboxMessages.CountAsync(x => duplicateIds.Contains(x.MessageId)
                        && x.CompletedDate != null) == duplicateIds.Count;
                    if (!acknowledged) await Task.Delay(100, timeout.Token);
                }
                await using (var db = factory.CreateDbContext())
                {
                    Assert.Equal(generated.Count * 4, await db.AchLedgerEntries.CountAsync(x => ids.Contains(x.AchEntryId)));
                    Assert.Equal(balances.Values.Sum(), await db.Accounts.SumAsync(x => x.Balance));
                }
            }
        }
        finally
        {
            foreach (var worker in workers.AsEnumerable().Reverse())
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await worker.StopAsync(stop.Token); } catch (OperationCanceledException) { }
                (worker as IDisposable)?.Dispose();
            }
            await bus.CleanupAsync();
            await using var db = factory.CreateDbContext();
            await db.Database.EnsureDeletedAsync(); // Only the uniquely named test database.
        }
    }

    private sealed class IsolatedBus(IMessageBus inner, string prefix) : IMessageBus
    {
        private readonly ConcurrentDictionary<string, byte> queues = new();
        private string Queue(string queue) { queues.TryAdd(prefix + queue, 0); return prefix + queue; }
        public Task PublishAsync<T>(string queue, T message, CancellationToken cancellationToken = default, string? messageId = null) =>
            inner.PublishAsync(Queue(queue), message, cancellationToken, messageId);
        public Task ConsumeAsync<T>(string queue, Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken) =>
            inner.ConsumeAsync(Queue(queue), handler, cancellationToken);
        public async Task CleanupAsync()
        {
            var connectionFactory = new ConnectionFactory { HostName = "localhost", UserName = "taskwrapper", Password = "taskwrapper123" };
            await using var connection = await connectionFactory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            foreach (var queue in queues.Keys) await channel.QueueDeleteAsync(queue, false, false);
        }
    }
}
