using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class StudioCampaignTests
{
    [Fact]
    public async Task Campaign_carries_balances_cases_and_banking_dates_without_advancing_shared_clock()
    {
        var (factory, bank, other) = await Seed();
        var campaigns = new StudioCampaignService(factory);
        var plan = StudioCampaignPlan.Default("Wire") with { Counts = [2, 3, 1], IncidentPhase = 0, Incident = "Duplicate" };
        await campaigns.AuthorAsync(bank.Id, "Wire", "Two day continuity", plan.Serialize(), "operator", default);
        Guid scenario;
        await using (var db = factory.CreateDbContext()) scenario = (await db.StudioScenarios.SingleAsync()).Id;
        await campaigns.CreateCampaignAsync(bank.Id, scenario, 2, new(2026, 10, 30), default);
        Guid campaign;
        await using (var db = factory.CreateDbContext()) campaign = (await db.StudioCampaigns.SingleAsync()).Id;
        var days = new StudioDayService(factory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => days.StartAsync(other.Id, "Wire", "other", false, default, campaign));
        await days.StartAsync(bank.Id, "Wire", "operator", false, default, campaign);
        StudioDay first;
        await using (var db = factory.CreateDbContext())
        {
            first = await db.StudioDays.SingleAsync();
            Assert.Equal(2, (await db.StudioSettings.SingleAsync()).RemainingRuns);
            Assert.Equal("Duplicate", first.PendingIncident);
            var account = await db.Accounts.SingleAsync(x => x.AccountNumber == TrafficPopulation.AccountNumber(bank.RoutingNumber, 1));
            account.Balance = 4321m; await db.SaveChangesAsync();
        }
        await days.AdvanceAsync(bank.Id, first.Id, 0, true, default);
        await days.StartAsync(bank.Id, "Wire", "operator", false, default, campaign);
        await using (var db = factory.CreateDbContext())
        {
            var second = await db.StudioDays.SingleAsync(x => x.EndedAt == null);
            Assert.Equal(new DateOnly(2026, 11, 2), second.BusinessDate);
            Assert.Equal(24321m, second.OpeningCustomerBalance);
            Assert.Equal(2, await db.OperationsCases.CountAsync());
            Assert.Equal(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero), (await db.SimulationStates.SingleAsync(x => x.Name == "default")).CurrentUtc);
            await days.AdvanceAsync(bank.Id, second.Id, 0, true, default);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => days.StartAsync(bank.Id, "Wire", "operator", false, default, campaign));
    }

    [Fact]
    public async Task Required_authorization_and_revocation_control_future_debit_generation_without_canceling_existing_entries()
    {
        var (factory, bank, other) = await Seed();
        var service = new StudioCampaignService(factory);
        var plan = StudioCampaignPlan.Default("Ach") with { Counts = [1, 1, 2], RequireDebitAuthorization = true };
        await service.AuthorAsync(bank.Id, "Ach", "Authorization exercise", plan.Serialize(), "operator", default);
        Guid campaignId; Guid collectionId;
        await using (var db = factory.CreateDbContext())
        {
            var scenario = await db.StudioScenarios.SingleAsync();
            await service.CreateCampaignAsync(bank.Id, scenario.Id, 2, new(2026, 10, 26), default);
            campaignId = (await db.StudioCampaigns.SingleAsync()).Id;
            collectionId = (await db.Accounts.SingleAsync(x => x.AccountNumber == TrafficPopulation.AccountNumber(bank.RoutingNumber, 3))).Id;
        }
        var days = new StudioDayService(factory); await days.StartAsync(bank.Id, "Ach", "operator", false, default, campaignId);
        Guid dayId;
        await using (var db = factory.CreateDbContext()) dayId = (await db.StudioDays.SingleAsync()).Id;
        for (var phase = 0; phase < 2; phase++)
        {
            await using var db = factory.CreateDbContext(); (await db.StudioSettings.SingleAsync()).RemainingRuns = 0; await db.SaveChangesAsync();
            await days.AdvanceAsync(bank.Id, dayId, phase, false, default);
        }
        var traffic = new AchTrafficService(factory);
        await traffic.TickAsync(bank.Id, default);
        await using (var db = factory.CreateDbContext()) Assert.Empty(await db.AchEntries.ToListAsync());
        await service.AuthorizeAsync(bank.Id, collectionId, other.RoutingNumber, TrafficPopulation.AccountNumber(other.RoutingNumber, 4), 1000, new(2027, 1, 1), "operator", default);
        await Due(factory); await traffic.TickAsync(bank.Id, default);
        Guid authId;
        await using (var db = factory.CreateDbContext())
        {
            Assert.Single(await db.AchEntries.ToListAsync()); authId = (await db.StudioDebitAuthorizations.SingleAsync()).Id;
        }
        await service.RevokeAsync(bank.Id, authId, default);
        await Due(factory); await traffic.TickAsync(bank.Id, default);
        await using (var db = factory.CreateDbContext())
        {
            var entry = await db.AchEntries.SingleAsync(); Assert.Equal(AchEntryStatus.Entered, entry.Status);
            Assert.Equal(1, (await db.StudioSettings.SingleAsync()).RemainingRuns);
            Assert.Single(await db.OutboxMessages.ToListAsync());
            Assert.All(await db.Accounts.ToListAsync(), x => Assert.Equal(10000m, x.Balance));
        }
    }

    [Fact]
    public async Task Repair_is_idempotent_and_preserves_original_file_and_independent_approval()
    {
        var (factory, bank, other) = await Seed();
        var service = new StudioCampaignService(factory); Guid originalId;
        await using (var db = factory.CreateDbContext())
        {
            var source = await db.Accounts.Include(x => x.Customer).ThenInclude(x => x.Bank).SingleAsync(x => x.AccountNumber == TrafficPopulation.AccountNumber(bank.RoutingNumber, 1));
            var destination = await db.Accounts.Include(x => x.Customer).ThenInclude(x => x.Bank).SingleAsync(x => x.AccountNumber == TrafficPopulation.AccountNumber(other.RoutingNumber, 4));
            var entry = AchTraffic.CreateRun(source, [destination], AchTrafficMode.Payroll, new(2026, 10, 26), DateTimeOffset.UtcNow).Single();
            entry.Status = AchEntryStatus.Rejected; entry.Batch = new AchBatch { OriginatingBankId = bank.Id, File = new AchFile { OriginatingBankId = bank.Id, Status = "Rejected", RawNachaPayload = "original rejected payload" } };
            originalId = entry.Id; db.AchEntries.Add(entry); await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RepairAsync(other.Id, originalId, other.RoutingNumber, "DEMO", new(2026, 10, 27), "Reviewed original file controls.", "maker", default));
        var replacement = await service.RepairAsync(bank.Id, originalId, other.RoutingNumber, "DEMO", new(2026, 10, 27), "Reviewed original file controls.", "human-maker", default);
        Assert.Equal(replacement, await service.RepairAsync(bank.Id, originalId, other.RoutingNumber, "OTHER", new(2026, 10, 28), "Repeated submission after response loss.", "human-maker", default));
        await using (var db = factory.CreateDbContext())
        {
            var created = await db.AchEntries.SingleAsync(x => x.Id == replacement);
            Assert.Equal("human-maker", created.CreatedBy); Assert.Null(created.ApprovedBy);
            Assert.Equal(AchEntryStatus.PendingApproval, created.Status); Assert.Null(created.AchBatchId);
            var original = await db.AchEntries.Include(x => x.Batch).ThenInclude(x => x!.File).SingleAsync(x => x.Id == originalId);
            Assert.Equal(AchEntryStatus.Rejected, original.Status); Assert.Equal("original rejected payload", original.Batch!.File!.RawNachaPayload);
            Assert.Single(await db.StudioAchRepairs.ToListAsync()); Assert.Empty(await db.OutboxMessages.ToListAsync()); Assert.Empty(await db.AchLedgerEntries.ToListAsync());
        }
    }

    [Fact]
    public async Task Conversation_retains_delayed_response_and_requests_missing_correlation_without_changing_wire()
    {
        var (factory, bank, other) = await Seed(); Guid wireId;
        await using (var db = factory.CreateDbContext())
        {
            var a = await db.Accounts.Include(x => x.Customer).ToListAsync();
            var wire = WireTraffic.Create(a.First(x => x.Customer.BankId == bank.Id), a.First(x => x.Customer.BankId == other.Id), DateTimeOffset.UtcNow);
            wire.Status = WireStatus.PendingAtFed; wireId = wire.Id; db.WireTransfers.Add(wire); await db.SaveChangesAsync();
        }
        var service = new StudioCampaignService(factory);
        await service.ConversationAsync(bank.Id, wireId, "Please investigate the missing outcome.", "operator", default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConversationAsync(bank.Id, wireId, "Please investigate the missing outcome.", "operator", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConversationAsync(other.Id, wireId, "Please investigate the missing outcome.", "operator", default));
        await using (var db = factory.CreateDbContext())
        {
            var response = await db.StudioNotes.SingleAsync(x => x.Kind == "ReceiverResponse");
            Assert.True(response.AvailableAt > response.CreatedAt); Assert.Contains("Please supply correlation", response.Text);
            Assert.Equal(WireStatus.PendingAtFed, (await db.WireTransfers.SingleAsync()).Status);
            Assert.Empty(await db.IsoMessages.ToListAsync()); Assert.Empty(await db.LedgerEntries.ToListAsync());
        }
    }

    [Fact]
    public async Task Funding_requires_correlated_balanced_incoming_wire_and_never_mints_cash()
    {
        var (factory, bank, other) = await Seed(); var service = new StudioCampaignService(factory);
        await service.FundingAsync(bank.Id, 500m, "Forecast outgoing payroll settlement needs.", "operator", default);
        StudioFundingRequest request; Guid incomingId;
        await using (var db = factory.CreateDbContext())
        {
            request = await db.StudioFundingRequests.SingleAsync();
            Assert.Equal(100000m, (await db.Banks.SingleAsync(x => x.Id == bank.Id)).MasterAccountBalance);
            var a = await db.Accounts.Include(x => x.Customer).ToListAsync();
            var outgoing = WireTraffic.Create(a.First(x => x.Customer.BankId == other.Id), a.First(x => x.Customer.BankId == bank.Id), DateTimeOffset.UtcNow);
            outgoing.CustomerReference = request.Reference; outgoing.TransferType = WireTransferType.FinancialInstitutionCreditTransfer; outgoing.Amount = 500; outgoing.Status = WireStatus.Settled;
            var incoming = new WireTransfer { BankId = bank.Id, SenderBankId = other.Id, ReceiverBankId = bank.Id, Direction = WireDirection.Incoming,
                CorrelationId = outgoing.CorrelationId, Amount = 500, TransferType = WireTransferType.FinancialInstitutionCreditTransfer, Status = WireStatus.Completed,
                SenderName = "Source bank", ReceiverName = "Funding bank", BeneficiaryAccountNumber = "BANK" };
            incomingId = incoming.Id; db.WireTransfers.AddRange(outgoing, incoming); await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReconcileFundingAsync(bank.Id, request.Id, incomingId, default));
        await using (var db = factory.CreateDbContext())
        {
            var journal = Guid.NewGuid(); db.LedgerEntries.AddRange(
                new LedgerEntry { JournalId = journal, WireTransferId = incomingId, AccountCode = "Cash", AccountName = "Cash", Debit = 500, Description = "Test funding" },
                new LedgerEntry { JournalId = journal, WireTransferId = incomingId, AccountCode = "Settlement", AccountName = "Settlement", Credit = 500, Description = "Test funding" });
            await db.SaveChangesAsync();
        }
        await service.ReconcileFundingAsync(bank.Id, request.Id, incomingId, default);
        await service.ReconcileFundingAsync(bank.Id, request.Id, incomingId, default);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(incomingId, (await db.StudioFundingRequests.SingleAsync()).IncomingWireId);
            Assert.Equal(100000m, (await db.Banks.SingleAsync(x => x.Id == bank.Id)).MasterAccountBalance);
            Assert.Equal(2, await db.LedgerEntries.CountAsync());
        }
    }

    [Theory]
    [InlineData("Wire")]
    [InlineData("Ach")]
    public async Task Slow_receiver_profile_changes_generated_processing_without_advancing_shared_time(string kind)
    {
        var (factory, bank, _) = await Seed(); var service = new StudioCampaignService(factory);
        var plan = StudioCampaignPlan.Default(kind) with { ReceiverProfile = "Slow", Counts = [1, 1, 1] };
        await service.AuthorAsync(bank.Id, kind, "Slow receiver exercise", plan.Serialize(), "operator", default);
        Guid scenario; Guid campaign;
        await using (var db = factory.CreateDbContext()) scenario = (await db.StudioScenarios.SingleAsync()).Id;
        await service.CreateCampaignAsync(bank.Id, scenario, 2, new(2026, 10, 26), default);
        await using (var db = factory.CreateDbContext()) campaign = (await db.StudioCampaigns.SingleAsync()).Id;
        await new StudioDayService(factory).StartAsync(bank.Id, kind, "operator", false, default, campaign);
        if (kind == "Wire") await new WireTrafficService(factory).TickAsync(bank.Id, default);
        else await new AchTrafficService(factory).TickAsync(bank.Id, default);
        await using (var db = factory.CreateDbContext())
        {
            if (kind == "Wire") Assert.Equal(ProcessingScenario.PendingThenAccepted, (await db.WireTransfers.SingleAsync()).Scenario);
            else Assert.All(await db.AchEntries.ToListAsync(), x => Assert.Equal(new DateOnly(2026, 10, 27), x.EffectiveEntryDate));
            Assert.Equal(0, (await db.StudioSettings.SingleAsync()).RemainingRuns);
            Assert.Equal(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero), (await db.SimulationStates.SingleAsync(x => x.Name == "default")).CurrentUtc);
        }
    }

    private static async Task Due(Factory factory)
    { await using var db = factory.CreateDbContext(); foreach (var state in await db.SimulationStates.Where(x => x.Name != "default" && !x.IsPaused).ToListAsync()) state.CurrentUtc = DateTimeOffset.UtcNow.AddMinutes(-1); await db.SaveChangesAsync(); }
    private static async Task<(Factory, Bank, Bank)> Seed()
    {
        var options = new DbContextOptionsBuilder<BankingDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
        var factory = new Factory(options); var bank = Bank("101000019"); var other = Bank("111901234");
        await using var db = factory.CreateDbContext();
        foreach (var n in new[] { 1, 2, 3 }) db.Accounts.Add(Account(bank, n));
        foreach (var n in new[] { 4, 5 }) db.Accounts.Add(Account(other, n));
        db.SimulationStates.Add(new SimulationState { Name = "default", CurrentUtc = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero) }); await db.SaveChangesAsync();
        return (factory, bank, other);
    }
    private static Bank Bank(string routing) => new() { Name = "Fictional bank", RoutingNumber = routing, FedParticipantId = routing, Bic = "TESTUS00", TownName = "Test", CountryCode = "US", MasterAccountBalance = 100000 };
    private static Account Account(Bank bank, int n) => new() { AccountNumber = TrafficPopulation.AccountNumber(bank.RoutingNumber, n), Balance = 10000, Customer = new Customer { Name = "Fictional customer", BankId = bank.Id, Bank = bank } };
    private sealed class Factory(DbContextOptions<BankingDbContext> options) : IDbContextFactory<BankingDbContext> { public BankingDbContext CreateDbContext() => new(options); }
}
