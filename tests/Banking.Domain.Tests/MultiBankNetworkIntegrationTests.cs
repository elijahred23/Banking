using System.Text.Json;
using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class MultiBankNetworkIntegrationTests
{
    [Fact, Trait("Category", "ExternalIntegration")]
    public async Task Five_banks_settle_deliver_and_post_independently_with_durable_recovery()
    {
        if (Environment.GetEnvironmentVariable("BANKING_RUN_EXTERNAL_INTEGRATION") != "1") return;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:DefaultConnection"] = $"Server=localhost,11433;Database=BankingNetworkTest_{Guid.NewGuid():N};User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True"
        }).Build();
        await using var services = new ServiceCollection().AddLogging().AddBankingInfrastructure(config).BuildServiceProvider();
        var factory = services.GetRequiredService<IDbContextFactory<BankingDbContext>>();
        try
        {
            await services.InitializeDatabaseAsync();
            var banks = Enumerable.Range(1, 5).Select(i => new Bank {
                Name = $"Synthetic bank {i}", RoutingNumber = $"99000000{i}", FedParticipantId = $"LABBANK{i}",
                Bic = $"LAB{i}US44XXX", TownName = "Tulsa", CountryCode = "US", MasterAccountBalance = 100_000m,
                Customers = [new Customer { Name = $"Synthetic customer {i}", Accounts = [new Account {
                    AccountNumber = $"TEST{i}", Balance = 150_000m }] }]
            }).ToArray();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Banks.AddRange(banks);
                await db.SaveChangesAsync();
            }
            var iso = new IsoMessageService();
            var settlementService = new FedwireSettlementService(factory, iso);
            var posting = services.GetRequiredService<IncomingWirePostingService>();
            var bus = new HandlerBus();
            using var worker = new Banking.MessageManager.Worker(bus, factory,
                NullLogger<Banking.MessageManager.Worker>.Instance);
            await worker.StartAsync(default);
            foreach (var receiver in banks.Skip(1))
            {
                var account = banks[0].Customers[0].Accounts[0];
                var beneficiary = receiver.Customers[0].Accounts[0];
                var wire = new WireTransfer { BankId = banks[0].Id, SenderBankId = banks[0].Id,
                    ReceiverBankId = receiver.Id, FromAccountId = account.Id,
                    Amount = 25_000, Direction = WireDirection.Outgoing, Status = WireStatus.SentToFed,
                    Rail = PaymentRail.Fedwire, SenderName = "Synthetic sender", ReceiverName = "Synthetic receiver",
                    BeneficiaryAccountNumber = beneficiary.AccountNumber };
                await using (var db = await factory.CreateDbContextAsync())
                {
                    var source = await db.Accounts.SingleAsync(x => x.Id == account.Id);
                    source.HeldBalance += wire.Amount;
                    db.WireTransfers.Add(wire);
                    await db.SaveChangesAsync();
                }
                var envelope = new FedEnvelope(FedMessageKind.Payment, wire.Id, wire.CorrelationId,
                    banks[0].Id, receiver.Id, wire.Amount,
                    iso.CreatePacs008(wire, banks[0], receiver, account.AccountNumber, beneficiary.AccountNumber));
                var simultaneous = await Task.WhenAll(settlementService.SettleAsync(envelope, default),
                    settlementService.SettleAsync(envelope, default));
                var settlement = simultaneous[0];
                Assert.Equal(settlement.Id, simultaneous[1].Id);
                Assert.Equal("ACSC", settlement.StatusCode);
                Assert.Equal(settlement.Id, (await settlementService.SettleAsync(envelope, default)).Id);
                // Recover only from the durable outbox: no live simulator response is needed.
                List<FedEnvelope> messages;
                await using (var db = await factory.CreateDbContextAsync())
                {
                    messages = (await db.OutboxMessages.Where(x => x.Queue == Queues.FedInbound).ToListAsync())
                        .Select(x => JsonSerializer.Deserialize<FedEnvelope>(x.Payload)!)
                        .Where(x => x.CorrelationId == wire.CorrelationId).ToList();
                    Assert.Equal(2, messages.Count);
                    Assert.Equal(150_000m, (await db.Accounts.SingleAsync(x => x.Id == beneficiary.Id)).Balance);
                }
                var beforeDelivery = await services.GetRequiredService<BankNetworkReconciliationService>().BuildAsync(receiver.Id, default);
                Assert.Contains(beforeDelivery, x => x.CorrelationId == wire.CorrelationId && x.Delivery == "Awaiting delivery");
                foreach (var message in messages)
                {
                    await bus.Receive(message);
                    await bus.Receive(message);
                }
                Guid incomingId;
                await using (var db = await factory.CreateDbContextAsync())
                {
                    var incoming = await db.WireTransfers.SingleAsync(x => x.BankId == receiver.Id && x.CorrelationId == wire.CorrelationId);
                    incomingId = incoming.Id;
                    Assert.Equal(WireStatus.Received, incoming.Status);
                    Assert.Equal(150_000m, (await db.Accounts.SingleAsync(x => x.Id == beneficiary.Id)).Balance);
                }
                Assert.NotNull(await posting.PostAsync(banks[0].Id, incomingId, "wrong-bank", default));
                var postings = await Task.WhenAll(
                    posting.PostAsync(receiver.Id, incomingId, "receiver-operations", default),
                    posting.PostAsync(receiver.Id, incomingId, "receiver-operations", default));
                Assert.All(postings, result => Assert.Null(result));
                await using (var db = await factory.CreateDbContextAsync())
                {
                    Assert.Equal(175_000m, (await db.Accounts.SingleAsync(x => x.Id == beneficiary.Id)).Balance);
                    Assert.Equal(2, await db.LedgerEntries.CountAsync(x => x.WireTransferId == incomingId));
                }
                var reconciled = await services.GetRequiredService<BankNetworkReconciliationService>().BuildAsync(receiver.Id, default);
                Assert.Single(reconciled);
                Assert.Equal("Balanced", reconciled[0].Accounting);
            }
            var insufficient = new WireTransfer { BankId = banks[0].Id, SenderBankId = banks[0].Id,
                ReceiverBankId = banks[1].Id, FromAccountId = banks[0].Customers[0].Accounts[0].Id,
                Amount = 25_000, Direction = WireDirection.Outgoing, Status = WireStatus.SentToFed,
                SenderName = "Synthetic sender", ReceiverName = "Synthetic receiver", BeneficiaryAccountNumber = "TEST2" };
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.WireTransfers.Add(insufficient);
                var account = await db.Accounts.SingleAsync(x => x.Id == insufficient.FromAccountId);
                account.HeldBalance = insufficient.Amount;
                await db.SaveChangesAsync();
            }
            var rejectedEnvelope = new FedEnvelope(FedMessageKind.Payment, insufficient.Id, insufficient.CorrelationId,
                banks[0].Id, banks[1].Id, insufficient.Amount,
                iso.CreatePacs008(insufficient, banks[0], banks[1], "TEST1", "TEST2"));
            Assert.Equal("RJCT", (await settlementService.SettleAsync(rejectedEnvelope, default)).StatusCode);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var outcomes = (await db.OutboxMessages.Where(x => x.Queue == Queues.FedInbound).ToListAsync())
                    .Select(x => JsonSerializer.Deserialize<FedEnvelope>(x.Payload)!)
                    .Where(x => x.CorrelationId == insufficient.CorrelationId).ToList();
                Assert.Single(outcomes);
                Assert.Equal(FedMessageKind.Status, outcomes[0].Kind);
                await bus.Receive(outcomes[0]);
            }
            await using var verify = await factory.CreateDbContextAsync();
            Assert.Equal(5, await verify.FedSettlements.CountAsync());
            Assert.Equal(4, await verify.WireTransfers.CountAsync(x => x.Direction == WireDirection.Incoming));
            Assert.Equal(WireStatus.Rejected, (await verify.WireTransfers.SingleAsync(x => x.Id == insufficient.Id)).Status);
            Assert.Equal(0m, (await verify.Accounts.SingleAsync(x => x.Id == insufficient.FromAccountId)).HeldBalance);
            Assert.Equal(500_000m, await verify.Banks.SumAsync(x => x.MasterAccountBalance));
            Assert.Equal(0m, (await verify.Banks.SingleAsync(x => x.Id == banks[0].Id)).MasterAccountBalance);
            Assert.Equal(50_000m, (await verify.Accounts.SingleAsync(x => x.Id == banks[0].Customers[0].Accounts[0].Id)).Balance);
            var unknown = new WireTransfer { BankId = banks[1].Id, SenderBankId = banks[1].Id,
                ReceiverBankId = banks[0].Id, FromAccountId = banks[1].Customers[0].Accounts[0].Id,
                Amount = 25_000, Direction = WireDirection.Outgoing, Status = WireStatus.SentToFed,
                SenderName = "Synthetic sender", ReceiverName = "Unknown receiver", BeneficiaryAccountNumber = "UNKNOWN" };
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.WireTransfers.Add(unknown);
                var source = await db.Accounts.SingleAsync(x => x.Id == unknown.FromAccountId);
                source.HeldBalance = unknown.Amount;
                await db.SaveChangesAsync();
            }
            var unknownEnvelope = new FedEnvelope(FedMessageKind.Payment, unknown.Id, unknown.CorrelationId,
                banks[1].Id, banks[0].Id, unknown.Amount, iso.CreatePacs008(unknown, banks[1], banks[0], "TEST2", "UNKNOWN"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => settlementService.SettleAsync(
                unknownEnvelope with { Amount = 1 }, default));
            Assert.Equal("ACSC", (await settlementService.SettleAsync(unknownEnvelope, default)).StatusCode);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var outcomes = (await db.OutboxMessages.Where(x => x.Queue == Queues.FedInbound).ToListAsync())
                    .Select(x => JsonSerializer.Deserialize<FedEnvelope>(x.Payload)!)
                    .Where(x => x.CorrelationId == unknown.CorrelationId);
                foreach (var outcome in outcomes) await bus.Receive(outcome);
            }
            await using (var db = await factory.CreateDbContextAsync())
            {
                var incoming = await db.WireTransfers.SingleAsync(x => x.BankId == banks[0].Id
                    && x.Direction == WireDirection.Incoming && x.CorrelationId == unknown.CorrelationId);
                Assert.NotNull(await posting.PostAsync(banks[0].Id, incoming.Id, "operations", default));
                Assert.Equal(WireStatus.Received, (await db.WireTransfers.AsNoTracking().SingleAsync(x => x.Id == incoming.Id)).Status);
                Assert.True(await db.WireEvents.AnyAsync(x => x.WireTransferId == incoming.Id && x.EventType == "BeneficiaryException"));
                Assert.False(await db.LedgerEntries.AnyAsync(x => x.WireTransferId == incoming.Id));
                Assert.Equal(500_000m, await db.Banks.SumAsync(x => x.MasterAccountBalance));
            }

        }
        finally
        {
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.EnsureDeletedAsync();
        }
    }

    private sealed class HandlerBus : IMessageBus
    {
        private Func<FedEnvelope, CancellationToken, Task> handler = null!;
        public Task Receive(FedEnvelope message) => handler(message, default);
        public Task ConsumeAsync<T>(string queue, Func<T, CancellationToken, Task> action, CancellationToken token)
        {
            if (queue == Queues.FedInbound) handler = (message, ct) => action((T)(object)message, ct);
            return Task.CompletedTask;
        }
        public Task PublishAsync<T>(string queue, T message, CancellationToken cancellationToken = default, string? messageId = null) => Task.CompletedTask;
    }
}
