using System.Collections.Concurrent;
using System.Security.Claims;
using Banking.Domain;
using Banking.FedwireSimulator;
using Banking.Infrastructure;
using Banking.Web;
using Banking.Web.Controllers;
using Banking.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class StatementIntegrationTests
{
    [Fact, Trait("Category", "ExternalIntegration")]
    public async Task Correspondent_fedwire_postings_generate_deliver_and_reconcile_the_650k_daily_statement()
    {
        if (!Enabled) return;
        await using var lab = await Lab.CreateAsync(true);
        var accountId = await lab.Postings.OpenAsync(lab.Issuer.Id, lab.Respondent.Id, "LAB0042", 500_000, default);
        await lab.Schedules.SaveAsync(lab.Issuer.Id, accountId, "America/Chicago", new(18, 0), [], true, default);
        var outgoing = await lab.Postings.RequestWireAsync(lab.Respondent.Id, accountId, lab.Other.Id, 100_000, "maker@respondent", default);
        await lab.Wires(lab.Issuer.Id, "approver@issuer").DecideApproval(outgoing, true, null, default);
        var create = Assert.IsType<RedirectToActionResult>(await lab.Wires(lab.Other.Id, "maker@other").Create(
            new CreateWireViewModel { ReceiverBankId = lab.Issuer.Id, TransferType = WireTransferType.FinancialInstitutionCreditTransfer,
                Rail = PaymentRail.Fedwire, Amount = 250_000, CustomerReference = "INCOMING-CORRESPONDENT" }, default));
        var incomingSource = Assert.IsType<Guid>(create.RouteValues!["id"]);
        await lab.Wires(lab.Other.Id, "approver@other").DecideApproval(incomingSource, true, null, default);
        await lab.PumpUntil(db => db.WireTransfers.AnyAsync(x => x.CorrelationId == db.WireTransfers.Where(w => w.Id == incomingSource).Select(w => w.CorrelationId).First()
            && x.BankId == lab.Issuer.Id && x.Direction == WireDirection.Incoming && x.Status == WireStatus.Completed));
        await using (var db = await lab.Factory.CreateDbContextAsync())
        {
            var correlation = (await db.WireTransfers.FindAsync(incomingSource))!.CorrelationId;
            var incoming = await db.WireTransfers.SingleAsync(x => x.CorrelationId == correlation && x.BankId == lab.Issuer.Id && x.Direction == WireDirection.Incoming);
            await lab.Postings.AssignIncomingAsync(lab.Issuer.Id, accountId, incoming.Id, default);
        }
        await lab.PumpUntil(db => db.CorrespondentAccounts.AnyAsync(x => x.Id == accountId && x.IssuerBalance == 650_000 && x.RespondentBalance == 650_000));
        await lab.Services.GetRequiredService<SimulationStateService>().AdvanceAsync(TimeSpan.FromHours(10)); // 08:00 -> 18:00 Central
        await lab.PumpUntil(db => db.AccountStatements.AnyAsync(x => x.AccountId == accountId && x.Kind == StatementKind.Daily
            && db.StatementDeliveries.Any(d => d.StatementId == x.Id && d.Status == StatementDeliveryStatus.Delivered)
            && db.StatementReconciliations.Any(r => r.StatementId == x.Id && r.Status == StatementReconciliationStatus.Reconciled)));
        Guid id;
        await using (var db = await lab.Factory.CreateDbContextAsync())
        {
            var statement = await db.AccountStatements.Include(x => x.Entries).SingleAsync(x => x.Kind == StatementKind.Daily);
            id = statement.Id;
            Assert.Equal(500_000m, statement.OpeningBalance); Assert.Equal(650_000m, statement.ClosingBalance);
            Assert.Equal(100_000m, statement.TotalDebits); Assert.Equal(250_000m, statement.TotalCredits);
            Assert.Equal(2, statement.Entries.Count); Assert.Equal(0m, statement.GenerationDifference);
            CamtStatementBuilder.Validate(statement.XmlPayload, StatementKind.Daily);
            Assert.Equal(2, await db.FedSettlements.CountAsync());
            Assert.Equal(0m, (await db.CorrespondentAccounts.FindAsync(accountId))!.HeldBalance);
            Assert.Equal(4, await db.CorrespondentLedgerEntries.CountAsync(x => x.BankId == lab.Respondent.Id));
            Assert.Equal(20_000_000m, await db.Banks.SumAsync(x => x.MasterAccountBalance));
            Assert.False(await db.DeadLetterMessages.AnyAsync());
        }
        await Task.WhenAll(lab.Schedules.ProcessDueAsync(default), lab.Schedules.ProcessDueAsync(default));
        await using (var db = await lab.Factory.CreateDbContextAsync())
            Assert.Equal(1, await db.AccountStatements.CountAsync(x => x.Kind == StatementKind.Daily));
        Assert.IsType<FileContentResult>(await lab.Statements(lab.Respondent.Id).Download(id, "xml", default));
        var pdf = Assert.IsType<FileContentResult>(await lab.Statements(lab.Respondent.Id).Download(id, "pdf", default));
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf.FileContents));
        Assert.IsType<NotFoundResult>(await lab.Statements(lab.Unrelated.Id).Details(id, default));
        Assert.IsType<NotFoundResult>(await lab.Statements(lab.Unrelated.Id).Download(id, "pdf", default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => lab.Generation.GenerateAsync(lab.Unrelated.Id, accountId,
            StatementKind.Intraday, new(2026, 11, 10, 14, 0, 0, TimeSpan.Zero), new(2026, 11, 11, 0, 0, 0, TimeSpan.Zero), null, default));
        var journals = await lab.Services.GetRequiredService<UnifiedJournalService>().ReadAsync(lab.Respondent.Id);
        UnifiedAccountingPolicy.Validate(journals);
        Assert.Contains(journals, x => x.Rail == PaymentRail.CorrespondentBook);
    }

    [Fact, Trait("Category", "ExternalIntegration")]
    public async Task Delayed_mirror_is_a_timing_item_snapshots_are_immutable_and_corrections_do_not_overwrite_history()
    {
        if (!Enabled) return;
        await using var lab = await Lab.CreateAsync(false);
        var account = await lab.Postings.OpenAsync(lab.Issuer.Id, lab.Respondent.Id, "LAB0042", 500_000, default);
        await lab.Postings.AssessFeeAsync(lab.Issuer.Id, account, 25, "Service fee", default);
        var clock = lab.Services.GetRequiredService<SimulationStateService>();
        await clock.AdvanceAsync(TimeSpan.FromHours(1));
        var start = new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(1);
        var generated = await Task.WhenAll(lab.Generation.GenerateAsync(lab.Issuer.Id, account, StatementKind.Daily, start, end, null, default),
            lab.Generation.GenerateAsync(lab.Issuer.Id, account, StatementKind.Daily, start, end, null, default));
        Assert.Equal(generated[0], generated[1]); var id = generated[0];
        Assert.IsType<NotFoundResult>(await lab.Statements(lab.Respondent.Id).Download(id, "xml", default));
        await lab.Reconciliation.DeliverAsync(new(id, lab.Respondent.Id), default);
        await lab.Reconciliation.DeliverAsync(new(id, lab.Respondent.Id), default);
        await lab.Reconciliation.ReconcileAsync(lab.Respondent.Id, id, default);
        Guid posting;
        await using (var db = await lab.Factory.CreateDbContextAsync())
        {
            Assert.Equal(StatementReconciliationStatus.Exception, (await db.StatementReconciliations.SingleAsync()).Status);
            posting = (await db.CorrespondentLedgerEntries.SingleAsync(x => x.BankId == lab.Issuer.Id && x.IsAccountLine)).PostingId;
            Assert.Single(await db.OperationsCases.ToListAsync());
            Assert.Equal(1, (await db.StatementDeliveries.SingleAsync()).AttemptCount);
        }
        await clock.AdvanceAsync(TimeSpan.FromHours(1));
        await lab.Postings.MirrorAsync(new(account, posting), default);
        await lab.Postings.MirrorAsync(new(account, posting), default);
        await lab.Generation.NotifyAsync(new(account, posting), default);
        await lab.Generation.NotifyAsync(new(account, posting), default);
        await lab.Reconciliation.ReconcileAsync(lab.Respondent.Id, id, default);
        await using (var db = await lab.Factory.CreateDbContextAsync())
        {
            var result = await db.StatementReconciliations.OrderByDescending(x => x.CreatedAt).FirstAsync();
            Assert.Equal(StatementReconciliationStatus.TimingDifference, result.Status);
            Assert.Equal(25m, result.Difference);
            Assert.Equal(OperationsCaseStatus.Resolved, (await db.OperationsCases.SingleAsync()).Status);
            Assert.Equal(2, await db.CorrespondentLedgerEntries.CountAsync(x => x.BankId == lab.Respondent.Id));
            Assert.Equal(1, await db.AccountStatements.CountAsync(x => x.Kind == StatementKind.Notification));
            var snapshot = await db.AccountStatements.SingleAsync(x => x.Id == id);
            snapshot.ClosingBalance = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        var correction = await lab.Generation.GenerateAsync(lab.Issuer.Id, account, StatementKind.Daily, start, end, id, default);
        await using (var db = await lab.Factory.CreateDbContextAsync())
        {
            var prior = await db.AccountStatements.SingleAsync(x => x.Id == id);
            var revised = await db.AccountStatements.SingleAsync(x => x.Id == correction);
            Assert.Equal(499_975m, prior.ClosingBalance); Assert.Equal(id, revised.PreviousStatementId); Assert.Equal(2, revised.Version);
            (await db.CorrespondentAccounts.FindAsync(account))!.IssuerBalance += 1;
            await db.SaveChangesAsync();
        }
        var held = await lab.Generation.GenerateAsync(lab.Issuer.Id, account, StatementKind.Daily, start, end, correction, default);
        await using var verify = await lab.Factory.CreateDbContextAsync();
        Assert.Equal(1m, (await verify.AccountStatements.FindAsync(held))!.GenerationDifference);
        Assert.False(await verify.StatementDeliveries.AnyAsync(x => x.StatementId == held));
        Assert.IsType<NotFoundResult>(await lab.Statements(lab.Respondent.Id).Details(held, default));
    }
    [Fact, Trait("Category", "ExternalIntegration")]
    public async Task Intraday_cutoff_includes_current_clock_postings_and_refresh_preserves_prior_snapshot()
    {
        if (!Enabled) return;
        await using var lab = await Lab.CreateAsync(false);
        var account = await lab.Postings.OpenAsync(lab.Issuer.Id, lab.Respondent.Id, "LAB0042", 500_000, default);
        var cutoff = new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero);
        await lab.Postings.AssessFeeAsync(lab.Issuer.Id, account, 25, "First fee", default);
        var first = await lab.Generation.GenerateAsync(lab.Respondent.Id, account, StatementKind.Intraday, cutoff, cutoff, null, default);
        await lab.Postings.AssessFeeAsync(lab.Issuer.Id, account, 10, "Second fee", default);
        var refreshed = await lab.Generation.GenerateAsync(lab.Respondent.Id, account, StatementKind.Intraday, cutoff, cutoff, null, default);
        Assert.NotEqual(first, refreshed);
        Assert.Equal(refreshed, await lab.Generation.GenerateAsync(lab.Respondent.Id, account, StatementKind.Intraday, cutoff, cutoff, null, default));
        await using var db = await lab.Factory.CreateDbContextAsync();
        Assert.Equal(499_975m, (await db.AccountStatements.FindAsync(first))!.ClosingBalance);
        Assert.Equal(499_965m, (await db.AccountStatements.FindAsync(refreshed))!.ClosingBalance);
        Assert.Equal(2, (await db.AccountStatements.FindAsync(refreshed))!.Version);
    }

    [Fact, Trait("Category", "ExternalIntegration")]
    public async Task Rejected_correspondent_transfer_releases_reservation_without_posting_or_statement_activity()
    {
        if (!Enabled) return;
        await using var lab = await Lab.CreateAsync(false);
        var accountId = await lab.Postings.OpenAsync(lab.Issuer.Id, lab.Respondent.Id, "LAB0042", 500_000, default);
        var wire = await lab.Postings.RequestWireAsync(lab.Respondent.Id, accountId, lab.Other.Id, 100_000, "respondent-maker", default);
        await lab.Wires(lab.Issuer.Id, "issuer-approver").DecideApproval(wire, false, "Declined", default);
        await lab.Postings.ProcessPaymentsAsync(default);
        await lab.Postings.ProcessPaymentsAsync(default);
        await using var db = await lab.Factory.CreateDbContextAsync();
        var account = await db.CorrespondentAccounts.FindAsync(accountId);
        Assert.Equal(0m, account!.HeldBalance);
        Assert.Equal(500_000m, account.IssuerBalance);
        Assert.Empty(await db.CorrespondentLedgerEntries.ToListAsync());
        Assert.Equal(CorrespondentPaymentStatus.Rejected, (await db.CorrespondentPaymentLinks.SingleAsync()).Status);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => lab.Postings.RequestWireAsync(lab.Unrelated.Id, accountId,
            lab.Other.Id, 100, "other-bank", default));
    }

    [Fact, Trait("Category", "ExternalIntegration")]
    public async Task Approved_book_orders_and_concurrent_funding_use_existing_statements_without_moving_reserves()
    {
        if (!Enabled) return;
        await using var lab = await Lab.CreateAsync(false);
        var service = lab.Services.GetRequiredService<CorrespondentPlatformService>();
        var source = await lab.Postings.OpenAsync(lab.Issuer.Id, lab.Respondent.Id, "SOURCE", 500_000, default);
        var destination = await lab.Postings.OpenAsync(lab.Issuer.Id, lab.Other.Id, "DEST", 0, default);
        foreach (var (account, respondent) in new[] { (source, lab.Respondent.Id), (destination, lab.Other.Id) })
        {
            var agreement = await service.ProposeAgreementAsync(lab.Issuer.Id, account, 500_000, "agreement-maker", default);
            await Assert.ThrowsAsync<ArgumentException>(() => service.ApproveAgreementAsync(lab.Issuer.Id, agreement, "agreement-maker", false, default));
            await service.ApproveAgreementAsync(lab.Issuer.Id, agreement, "issuer-approver", false, default);
            await service.ApproveAgreementAsync(respondent, agreement, "respondent-approver", false, default);
        }
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RequestBookAsync(lab.Unrelated.Id, source, destination, 100_000, "intruder", default));
        var order = await lab.Postings.RequestWireAsync(lab.Respondent.Id, source, lab.Other.Id, 100_000, "payment-maker", default);
        var reserved = Assert.Single(await service.PositionsAsync(lab.Respondent.Id, default));
        Assert.Equal(500_000, reserved.Position.LedgerBalance);
        Assert.Equal(400_000, reserved.Position.AvailableBalance);
        Assert.Empty(reserved.Position.Findings);
        Assert.Empty(await service.PositionsAsync(lab.Unrelated.Id, default));
        Assert.Equal(250_000, await service.PreviewFundingAsync(lab.Respondent.Id, source, destination,
            CorrespondentFundingStrategy.Threshold, 100_000, 250_000, 300_000, default));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DecideBookAsync(lab.Issuer.Id, order, "payment-maker", true, default));
        await service.DecideBookAsync(lab.Issuer.Id, order, "issuer-approver", true, default);
        await service.DecideBookAsync(lab.Issuer.Id, order, "issuer-approver", true, default);
        var clock = new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero);
        var rule = await service.ProposeFundingAsync(lab.Respondent.Id, new CorrespondentFundingRule {
            SourceAccountId = source, DestinationAccountId = destination, Strategy = CorrespondentFundingStrategy.Fixed,
            Maximum = 250_000, NextRunAt = clock }, "funding-maker", default);
        await service.ApproveFundingAsync(lab.Issuer.Id, rule, "issuer-approver", false, default);
        await service.ApproveFundingAsync(lab.Respondent.Id, rule, "source-approver", false, default);
        await Task.WhenAll(service.ProcessDueAsync(default), service.ProcessDueAsync(default));
        await lab.Services.GetRequiredService<SimulationStateService>().AdvanceAsync(TimeSpan.FromHours(1));
        var statement = await lab.Generation.GenerateAsync(lab.Issuer.Id, destination, StatementKind.Daily, clock, clock.AddHours(1), null, default);
        await using var db = await lab.Factory.CreateDbContextAsync();
        Assert.Equal(150_000, (await db.CorrespondentAccounts.FindAsync(source))!.IssuerBalance);
        Assert.Equal(350_000, (await db.CorrespondentAccounts.FindAsync(destination))!.IssuerBalance);
        Assert.Equal(0, (await db.CorrespondentAccounts.FindAsync(source))!.HeldBalance);
        Assert.Equal(1, await db.CorrespondentFundingRuns.CountAsync());
        Assert.Empty(await db.WireTransfers.ToListAsync());
        var settledPosition = Assert.Single(await service.PositionsAsync(lab.Respondent.Id, default));
        Assert.Equal(150_000, settledPosition.Position.AvailableBalance);
        var controls = await lab.Services.GetRequiredService<PaymentReconciliationService>().ReconcileAsync(lab.Respondent.Id, default);
        Assert.Equal(0, controls.CorrespondentBreaks);
        Assert.Equal(-350_000, settledPosition.Position.MirrorDifference);
        Assert.Empty(settledPosition.Position.Findings);
        Assert.Equal(350_000, (await db.AccountStatements.FindAsync(statement))!.ClosingBalance);
        Assert.Equal(0, await db.FedSettlements.CountAsync());
        var lines = await db.CorrespondentLedgerEntries.ToListAsync();
        Assert.All(lines.GroupBy(x => x.JournalId), journal => Assert.Equal(journal.Sum(x => x.Debit), journal.Sum(x => x.Credit)));
        Assert.Equal(0, lines.Where(x => x.AccountCode == "CORRESPONDENT_BOOK_CLEARING").Sum(x => x.Debit - x.Credit));
        Assert.Equal(11, await db.CorrespondentAuthorizationEvents.CountAsync());
        var sourceAgreement = await db.CorrespondentServiceAgreements.SingleAsync(x => x.AccountId == source);
        var rejected = await service.RequestBookAsync(lab.Respondent.Id, source, destination, 10_000, "payment-maker", default);
        await service.ApproveAgreementAsync(lab.Issuer.Id, sourceAgreement.Id, "issuer-approver", true, default);
        await Assert.ThrowsAsync<ArgumentException>(() => service.DecideBookAsync(lab.Issuer.Id, rejected, "issuer-approver", true, default));
        await service.DecideBookAsync(lab.Issuer.Id, rejected, "issuer-approver", false, default);
        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestBookAsync(lab.Respondent.Id, source, destination, 1, "payment-maker", default));
        await db.Entry(await db.CorrespondentAccounts.SingleAsync(x => x.Id == source)).ReloadAsync();
        Assert.Equal(0, (await db.CorrespondentAccounts.FindAsync(source))!.HeldBalance);
        var receipt = await db.CorrespondentAuthorizationEvents.FirstAsync();
        receipt.Actor = "tampered";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    private static bool Enabled => Environment.GetEnvironmentVariable("BANKING_RUN_EXTERNAL_INTEGRATION") == "1";

    private sealed class Lab : IAsyncDisposable
    {
        public required ServiceProvider Services { get; init; }
        public required IDbContextFactory<BankingDbContext> Factory { get; init; }
        public required Bank Issuer { get; init; }
        public required Bank Respondent { get; init; }
        public required Bank Other { get; init; }
        public required Bank Unrelated { get; init; }
        public required ScopedBus Bus { get; init; }
        public CorrespondentPostingService Postings => Services.GetRequiredService<CorrespondentPostingService>();
        public StatementGenerationService Generation => Services.GetRequiredService<StatementGenerationService>();
        public StatementScheduleService Schedules => Services.GetRequiredService<StatementScheduleService>();
        public StatementReconciliationService Reconciliation => Services.GetRequiredService<StatementReconciliationService>();
        private readonly List<BackgroundService> workers = [];
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromMinutes(2));
        public static async Task<Lab> CreateAsync(bool network)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["ConnectionStrings:DefaultConnection"] = $"Server=localhost,11433;Database=BankingStatementsTest_{Guid.NewGuid():N};User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True",
                ["RabbitMq:HostName"] = "localhost", ["RabbitMq:UserName"] = "taskwrapper", ["RabbitMq:Password"] = "taskwrapper123" }).Build();
            var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config).AddBankingInfrastructure(config).BuildServiceProvider();
            await services.InitializeDatabaseAsync();
            var factory = services.GetRequiredService<IDbContextFactory<BankingDbContext>>();
            var banks = Enumerable.Range(1, 4).Select(i => new Bank { Name = i == 1 ? "Bankers Bank" : $"Synthetic community bank {i}",
                RoutingNumber = $"99000000{i}", FedParticipantId = $"STMTBANK{i}", Bic = $"STB{i}US44XXX", TownName = "Synthetic city", CountryCode = "US", MasterAccountBalance = 5_000_000 }).ToArray();
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Banks.AddRange(banks);
                (await db.SimulationStates.SingleAsync()).CurrentUtc = new(2026, 11, 10, 14, 0, 0, TimeSpan.Zero);
                await db.SaveChangesAsync();
            }
            var lab = new Lab { Services = services, Factory = factory, Issuer = banks[0], Respondent = banks[1], Other = banks[2], Unrelated = banks[3], Bus = new(services.GetRequiredService<IMessageBus>()) };
            if (network)
            {
                var iso = new IsoMessageService();
                await lab.Start(new Banking.WireService.Worker(lab.Bus, factory, iso, new CbprPlusMessageService(iso), services.GetRequiredService<IPaymentRouteResolver>(), new WireComplianceService(), NullLogger<Banking.WireService.Worker>.Instance));
                await lab.Start(new Banking.MessageManager.Worker(lab.Bus, factory, NullLogger<Banking.MessageManager.Worker>.Instance));
                await lab.Start(new Banking.FedwireSimulator.Worker(lab.Bus, new FedwirePaymentProcessor(new FedwireSettlementService(factory, iso), new FedwireResponsePublisher(lab.Bus, iso), NullLogger<FedwirePaymentProcessor>.Instance)));
                await lab.Start(new StatementWorker(lab.Bus, lab.Postings, lab.Generation, services.GetRequiredService<CorrespondentPlatformService>(), lab.Reconciliation, lab.Schedules, NullLogger<StatementWorker>.Instance));
            }
            return lab;
        }
        private async Task Start(BackgroundService worker) { workers.Add(worker); await worker.StartAsync(lifetime.Token); }
        public WiresController Wires(Guid bankId, string name)
        {
            var iso = new IsoMessageService();
            return SetContext(new WiresController(Factory, iso, new CbprPlusMessageService(iso), new WireIsoMessageService(iso), new NonValueMessageWorkflowService(iso), new IncomingWirePostingService(Factory)), bankId, name);
        }
        public StatementsController Statements(Guid bankId) => SetContext(new StatementsController(Factory, Postings, Generation, Schedules, Reconciliation, new StatementExportService()), bankId, "operations");
        private static T SetContext<T>(T controller, Guid bankId, string name) where T : Controller
        {
            var http = new DefaultHttpContext { User = new(new ClaimsIdentity([new("bank_id", bankId.ToString()), new(ClaimTypes.Name, name)], "test")) };
            http.Session = new MemorySession();
            controller.ControllerContext = new() { HttpContext = http };
            controller.TempData = new TempDataDictionary(http, new MemoryTempData());
            return controller;
        }
        public async Task PumpUntil(Func<BankingDbContext, Task<bool>> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var dispatcher = new OutboxDispatcher(Factory, Bus, NullLogger<OutboxDispatcher>.Instance);
            while (true)
            {
                await dispatcher.PublishBatchAsync(timeout.Token);
                await using var db = await Factory.CreateDbContextAsync(timeout.Token);
                if (await condition(db)) return;
                Assert.Empty(await db.DeadLetterMessages.Select(x => x.Error).ToListAsync(timeout.Token));
                await Task.Delay(100, timeout.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            foreach (var worker in workers) { try { await worker.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { } worker.Dispose(); }
            var connectionFactory = new ConnectionFactory { HostName = "localhost", UserName = "taskwrapper", Password = "taskwrapper123" };
            await using var connection = await connectionFactory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            foreach (var queue in Bus.Queues) await channel.QueueDeleteAsync(queue);
            await using (var db = await Factory.CreateDbContextAsync()) await db.Database.EnsureDeletedAsync();
            await Services.DisposeAsync(); lifetime.Dispose();
        }
    }
    private sealed class MemorySession : ISession
    {
        private readonly Dictionary<string, byte[]> values = new();
        public bool IsAvailable => true;
        public string Id { get; } = Guid.NewGuid().ToString();
        public IEnumerable<string> Keys => values.Keys;
        public void Clear() => values.Clear();
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Remove(string key) => values.Remove(key);
        public void Set(string key, byte[] value) => values[key] = value;
        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? value) => values.TryGetValue(key, out value);
    }
    private sealed class MemoryTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
    private sealed class ScopedBus(IMessageBus inner) : IMessageBus
    {
        private readonly string prefix = $"statements.test.{Guid.NewGuid():N}.";
        private readonly ConcurrentDictionary<string, byte> queues = new();
        public IEnumerable<string> Queues => queues.Keys;
        private string Name(string queue) { var name = prefix + queue; queues.TryAdd(name, 0); return name; }
        public Task PublishAsync<T>(string queue, T message, CancellationToken cancellationToken = default, string? messageId = null) => inner.PublishAsync(Name(queue), message, cancellationToken, messageId);
        public Task ConsumeAsync<T>(string queue, Func<T, CancellationToken, Task> handler, CancellationToken cancellationToken) => inner.ConsumeAsync(Name(queue), handler, cancellationToken);
    }
}
