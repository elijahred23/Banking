using System.Collections.Concurrent;
using System.Text.Json;
using Banking.Domain;
using Banking.FedwireSimulator;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class FedFundsIntegrationTests
{
    [Fact, Trait("Category", "ExternalIntegration")]
    public async Task Overnight_trade_recovers_after_settlement_accrues_once_and_repays_over_sql_and_rabbit()
    {
        if (!Enabled) return;
        await using var lab = await Lab.CreateAsync();
        var tradeId = await lab.ApprovedTrade(750_000, 4.50m);
        await lab.PumpUntil(async db => await db.WireTransfers.AnyAsync(x => x.Status == WireStatus.SentToFed));
        FedEnvelope instruction;
        await using (var db = await lab.Factory.CreateDbContextAsync())
        {
            var obligation = await db.FedFundsPaymentObligations.SingleAsync(x => x.TradeId == tradeId);
            var wire = await db.WireTransfers.Include(x => x.IsoMessages).SingleAsync(x => x.Id == obligation.WireTransferId);
            var xml = wire.IsoMessages.Single(x => x.MessageType == "pacs.009").XmlPayload;
            instruction = new(FedMessageKind.Payment, wire.Id, wire.CorrelationId, wire.SenderBankId, wire.ReceiverBankId, wire.Amount, xml);
        }
        // Simulate the operator crashing after committing settlement, before publishing any response.
        var settlementEngine = new FedwireSettlementService(lab.Factory, new IsoMessageService());
        var first = await settlementEngine.SettleAsync(instruction, default);
        Assert.Equal("ACSC", first.StatusCode);
        await lab.Treasury.ProcessAsync(default);
        await using (var db = await lab.Factory.CreateDbContextAsync())
        {
            Assert.Equal(FedFundsTradeStatus.FundingPending, (await db.FedFundsTrades.FindAsync(tradeId))!.Status);
            Assert.False(await db.LedgerEntries.AnyAsync(x => x.AccountCode == "FF_SOLD"));
        }
        await lab.PumpUntil(db => db.FedFundsTrades.AnyAsync(x => x.Id == tradeId && x.Status == FedFundsTradeStatus.Active));
        await lab.StartNetwork(); // Original queued instruction now replays; it must not settle twice.
        Assert.Equal(first.Id, (await settlementEngine.SettleAsync(instruction, default)).Id);
        await lab.Bus.PublishAsync(Queues.FedInbound, instruction with { Kind = FedMessageKind.Status,
            StatusCode = "ACSC", Imad = first.Imad, Omad = first.Omad,
            XmlPayload = new IsoMessageService().CreatePacs002(instruction.CorrelationId, "ACSC", "Duplicate final status", first.Imad, "pacs.009.001.08") });
        await lab.Services.GetRequiredService<SimulationStateService>().AdvanceAsync(TimeSpan.FromDays(1));
        await lab.PumpUntil(db => db.FedFundsTrades.AnyAsync(x => x.Id == tradeId && x.Status == FedFundsTradeStatus.Repaid));
        await lab.Treasury.ProcessAsync(default);
        await lab.Treasury.ProcessAsync(default);
        await using (var db = await lab.Factory.CreateDbContextAsync())
        {
            Assert.Single(await db.FedFundsAccruals.Where(x => x.TradeId == tradeId).ToListAsync());
            Assert.Equal(93.75m, (await db.FedFundsAccruals.SingleAsync()).Amount);
            Assert.Equal(3, await db.FedSettlements.CountAsync());
            Assert.Equal(3, await db.FedFundsPaymentObligations.CountAsync(x => x.Status == FedFundsObligationStatus.Settled));
            Assert.Equal(0m, (await db.FedFundsCreditLines.SingleAsync()).ReservedAmount);
            Assert.Equal(5_000_093.75m, (await db.Banks.FindAsync(lab.Lender.Id))!.MasterAccountBalance);
            Assert.Equal(4_999_906.25m, (await db.Banks.FindAsync(lab.Borrower.Id))!.MasterAccountBalance);
            Assert.Equal(25_000_000m, await db.Banks.SumAsync(x => x.MasterAccountBalance));
            Assert.False(await db.DeadLetterMessages.AnyAsync());
            var journals = await db.LedgerEntries.GroupBy(x => x.JournalId).Select(x => new { Debit = x.Sum(y => y.Debit), Credit = x.Sum(y => y.Credit) }).ToListAsync();
            Assert.All(journals, x => Assert.Equal(x.Debit, x.Credit));
        }
        var reports = lab.Services.GetRequiredService<FedFundsReadService>();
        Assert.True(Assert.Single(await reports.ReconcileAsync(lab.Lender.Id, default)).IsClear);
        Assert.True(Assert.Single(await reports.ReconcileAsync(lab.Borrower.Id, default)).IsClear);
        Assert.All(await new BankNetworkReconciliationService(lab.Factory).BuildAsync(lab.Borrower.Id, default), x => Assert.Equal("Balanced", x.Accounting));
        Assert.Empty(await reports.PositionsAsync(lab.Third.Id, default));
        var controller = new Banking.Web.Controllers.FedFundsController(lab.Factory, lab.Treasury, reports,
            Microsoft.Extensions.Options.Options.Create(new FedFundsOptions()))
        {
            ControllerContext = new() { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
                    new System.Security.Claims.Claim("bank_id", lab.Third.Id.ToString()),
                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "third-bank") ], "test")) } }
        };
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await controller.Details(tradeId, default));
        Assert.IsType<Microsoft.AspNetCore.Mvc.NotFoundResult>(await controller.Approve(tradeId, true, default));

        await Assert.ThrowsAsync<InvalidOperationException>(() => lab.Treasury.AcceptTradeAsync(new(lab.Third.Id, "intruder"), tradeId, true, default));
    }

    [Fact, Trait("Category", "ExternalIntegration")]
    public async Task Competing_approvals_cannot_exceed_credit_and_funding_failure_retains_reserved_exposure()
    {
        if (!Enabled) return;
        await using var lab = await Lab.CreateAsync();
        await lab.Treasury.SetCreditLineAsync(new(lab.Lender.Id, "credit-approver"), lab.Borrower.Id, 1_000_000, true, default);
        var first = await lab.Proposal(750_000);
        var second = await lab.Proposal(750_000);
        foreach (var id in new[] { first, second })
        {
            await lab.Treasury.AcceptTradeAsync(new(lab.Lender.Id, "lender-maker"), id, true, default);
            await lab.Treasury.ApproveTradeAsync(new(lab.Borrower.Id, "borrower-approver"), id, true, default);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => lab.Treasury.ApproveTradeAsync(new(lab.Lender.Id, "lender-maker"), first, true, default));
        var outcomes = await Task.WhenAll(new[] { first, second }.Select(async id => {
            try { await lab.Treasury.ApproveTradeAsync(new(lab.Lender.Id, "lender-approver"), id, true, default); return true; }
            catch (InvalidOperationException) { return false; }
        }));
        Assert.Single(outcomes, x => x);
        await using (var db = await lab.Factory.CreateDbContextAsync())
        {
            var lender = await db.Banks.FindAsync(lab.Lender.Id);
            lender!.MasterAccountBalance = 1;
            await db.SaveChangesAsync();
        }
        await lab.Treasury.ProcessAsync(default);
        await using (var db = await lab.Factory.CreateDbContextAsync())
        {
            var failed = await db.FedFundsTrades.SingleAsync(x => x.Status == FedFundsTradeStatus.FundingException);
            Assert.Empty(await db.FedSettlements.ToListAsync());
            Assert.Empty(await db.LedgerEntries.ToListAsync());
            Assert.Equal(750_000m, (await db.FedFundsCreditLines.SingleAsync()).ReservedAmount);
            await lab.Treasury.CancelAsync(new(lab.Lender.Id, "lender-approver"), failed.Id, default);
        }
        await using var verify = await lab.Factory.CreateDbContextAsync();
        Assert.Equal(0m, (await verify.FedFundsCreditLines.SingleAsync()).ReservedAmount);
    }

    [Fact, Trait("Category", "ExternalIntegration")]
    public async Task Insufficient_repayment_is_overdue_with_liability_until_explicit_recovery()
    {
        if (!Enabled) return;
        await using var lab = await Lab.CreateAsync();
        await lab.StartNetwork();
        var id = await lab.ApprovedTrade(750_000, 4.50m);
        await lab.PumpUntil(db => db.FedFundsTrades.AnyAsync(x => x.Id == id && x.Status == FedFundsTradeStatus.Active));
        await using (var db = await lab.Factory.CreateDbContextAsync())
        {
            (await db.Banks.FindAsync(lab.Borrower.Id))!.MasterAccountBalance = 0;
            await db.SaveChangesAsync();
        }
        await lab.Services.GetRequiredService<SimulationStateService>().AdvanceAsync(TimeSpan.FromDays(1));
        await lab.Treasury.ProcessAsync(default);
        await lab.Treasury.ProcessAsync(default);
        await using (var db = await lab.Factory.CreateDbContextAsync())
        {
            Assert.Equal(FedFundsTradeStatus.Overdue, (await db.FedFundsTrades.FindAsync(id))!.Status);
            Assert.Equal(2, await db.FedFundsPaymentObligations.CountAsync(x => x.Status == FedFundsObligationStatus.Failed));
            Assert.Equal(750_000m, (await db.FedFundsCreditLines.SingleAsync()).ReservedAmount);
            Assert.Single(await db.FedSettlements.ToListAsync());
            Assert.Equal("ACSC", (await db.FedSettlements.SingleAsync()).StatusCode);
            (await db.Banks.FindAsync(lab.Borrower.Id))!.MasterAccountBalance = 1_000_000;
            await db.SaveChangesAsync();
        }
        var outstanding = Assert.Single(await lab.Services.GetRequiredService<FedFundsReadService>().ReconcileAsync(lab.Borrower.Id, default));
        Assert.Equal(750_000m, outstanding.JournalPrincipal);
        Assert.Equal(93.75m, outstanding.JournalInterest);
        await lab.Treasury.RetryRepaymentAsync(new(lab.Borrower.Id, "operations"), id, default);
        await lab.PumpUntil(db => db.FedFundsTrades.AnyAsync(x => x.Id == id && x.Status == FedFundsTradeStatus.Repaid));
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("BANKING_RUN_EXTERNAL_INTEGRATION") == "1";

    private sealed class Lab : IAsyncDisposable
    {
        public required ServiceProvider Services { get; init; }
        public required IDbContextFactory<BankingDbContext> Factory { get; init; }
        public required ScopedBus Bus { get; init; }
        public FedFundsTradeService Treasury => Services.GetRequiredService<FedFundsTradeService>();
        public required Bank Lender { get; init; }
        public required Bank Borrower { get; init; }
        public required Bank Third { get; init; }
        private readonly List<BackgroundService> workers = [];
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromMinutes(2));
        private OutboxDispatcher dispatcher = null!;
        public static async Task<Lab> CreateAsync()
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["ConnectionStrings:DefaultConnection"] = $"Server=localhost,11433;Database=BankingFedFundsTest_{Guid.NewGuid():N};User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True",
                ["RabbitMq:HostName"] = "localhost", ["RabbitMq:UserName"] = "taskwrapper", ["RabbitMq:Password"] = "taskwrapper123"
            }).Build();
            var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config).AddBankingInfrastructure(config).BuildServiceProvider();
            await services.InitializeDatabaseAsync();
            var factory = services.GetRequiredService<IDbContextFactory<BankingDbContext>>();
            var banks = Enumerable.Range(1, 5).Select(i => new Bank { Name = $"Synthetic Treasury bank {i}",
                RoutingNumber = $"99000000{i}", FedParticipantId = $"TREASURY{i}", Bic = $"TST{i}US44XXX",
                TownName = "Synthetic city", CountryCode = "US", MasterAccountBalance = 5_000_000 }).ToArray();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Banks.AddRange(banks);
                (await db.SimulationStates.SingleAsync()).CurrentUtc = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
                await db.SaveChangesAsync();
            }
            var bus = new ScopedBus(services.GetRequiredService<IMessageBus>());
            var lab = new Lab { Services = services, Factory = factory, Bus = bus, Borrower = banks[0], Lender = banks[1], Third = banks[2] };
            lab.dispatcher = new(factory, bus, NullLogger<OutboxDispatcher>.Instance);
            await lab.Start(new Banking.WireService.Worker(bus, factory, new IsoMessageService(), new CbprPlusMessageService(new IsoMessageService()),
                services.GetRequiredService<IPaymentRouteResolver>(), new WireComplianceService(), NullLogger<Banking.WireService.Worker>.Instance));
            await lab.Start(new Banking.MessageManager.Worker(bus, factory, NullLogger<Banking.MessageManager.Worker>.Instance));
            return lab;
        }
        public async Task StartNetwork() => await Start(new Banking.FedwireSimulator.Worker(Bus,
            new FedwirePaymentProcessor(new FedwireSettlementService(Factory, new IsoMessageService()),
                new FedwireResponsePublisher(Bus, new IsoMessageService()), NullLogger<FedwirePaymentProcessor>.Instance)));
        private async Task Start(BackgroundService worker) { workers.Add(worker); await worker.StartAsync(lifetime.Token); }
        public Task<Guid> Proposal(decimal principal) => Treasury.ProposeTradeAsync(new(Borrower.Id, "borrower-maker"),
            new(Lender.Id, true, principal, 4.50m, new(2026, 11, 12)), default);
        public async Task<Guid> ApprovedTrade(decimal principal, decimal rate)
        {
            await Treasury.SetCreditLineAsync(new(Lender.Id, "credit-approver"), Borrower.Id, 2_000_000, true, default);
            var id = await Treasury.ProposeTradeAsync(new(Borrower.Id, "borrower-maker"), new(Lender.Id, true, principal, rate, new(2026, 11, 12)), default);
            await Treasury.AcceptTradeAsync(new(Lender.Id, "lender-maker"), id, true, default);
            await Treasury.ApproveTradeAsync(new(Borrower.Id, "borrower-approver"), id, true, default);
            await Treasury.ApproveTradeAsync(new(Lender.Id, "lender-approver"), id, true, default);
            return id;
        }
        public async Task PumpUntil(Func<BankingDbContext, Task<bool>> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            while (!timeout.IsCancellationRequested)
            {
                await Treasury.ProcessAsync(timeout.Token);
                await dispatcher.PublishBatchAsync(timeout.Token);
                await using var db = await Factory.CreateDbContextAsync(timeout.Token);
                if (await ready(db)) return;
                var errors = await db.DeadLetterMessages.Select(x => x.Error).ToListAsync(timeout.Token);
                Assert.Empty(errors);
                await Task.Delay(75, timeout.Token);
            }
            throw new TimeoutException("Treasury pipeline did not reach expected durable state.");
        }
        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            foreach (var worker in workers)
            {
                try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
                worker.Dispose();
            }
            var connectionFactory = new ConnectionFactory { HostName = "localhost", UserName = "taskwrapper", Password = "taskwrapper123" };
            await using var connection = await connectionFactory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            foreach (var queue in Bus.Queues) await channel.QueueDeleteAsync(queue);
            await using var db = await Factory.CreateDbContextAsync();
            await db.Database.EnsureDeletedAsync();
            await Services.DisposeAsync();
            lifetime.Dispose();
        }
    }
    private sealed class ScopedBus(IMessageBus inner) : IMessageBus
    {
        private readonly string prefix = $"fedfunds.test.{Guid.NewGuid():N}.";
        private readonly ConcurrentDictionary<string, byte> queues = new();
        public IEnumerable<string> Queues => queues.Keys;
        private string Name(string queue) { var name = prefix + queue; queues.TryAdd(name, 0); return name; }
        public Task PublishAsync<T>(string queue, T message, CancellationToken cancellationToken = default, string? messageId = null) =>
            inner.PublishAsync(Name(queue), message, cancellationToken, messageId);
        public Task ConsumeAsync<T>(string queue, Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken) =>
            inner.ConsumeAsync(Name(queue), handler, cancellationToken);
    }
}
