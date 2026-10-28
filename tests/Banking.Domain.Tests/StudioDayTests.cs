using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class StudioDayTests
{
    [Theory]
    [InlineData("Wire", "Reject")]
    [InlineData("Ach", "Closed")]
    public async Task Incident_is_consumed_once_with_generation_and_never_posts_money(string kind, string incident)
    {
        var (factory, source, destination) = await Seed();
        var days = new StudioDayService(factory);
        await days.StartAsync(source.Id, kind, "operations-test", false, default);
        StudioDay day;
        await using (var db = factory.CreateDbContext()) day = await db.StudioDays.SingleAsync();
        await days.QueueIncidentAsync(source.Id, day.Id, incident, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => days.QueueIncidentAsync(source.Id, day.Id, incident, default));
        await Tick(factory, source.Id, kind);
        await using (var db = factory.CreateDbContext())
        {
            var stored = await db.StudioDays.SingleAsync();
            Assert.Equal("None", stored.PendingIncident); Assert.Null(stored.PendingIncidentCaseId);
            var item = await db.OperationsCases.SingleAsync();
            if (kind == "Wire")
            {
                var wire = await db.WireTransfers.SingleAsync();
                Assert.Equal(ProcessingScenario.FedRejects, wire.Scenario);
                Assert.Equal(wire.Id.ToString(), item.Reference);
                Assert.Null(wire.ApprovedBy);
            }
            else
            {
                var entries = await db.AchEntries.ToListAsync();
                Assert.Equal(2, entries.Count);
                Assert.All(entries, x => Assert.Equal(AchProcessingScenario.AccountClosed, x.Scenario));
                Assert.Contains(entries, x => x.Id.ToString() == item.Reference);
            }
            var state = await db.SimulationStates.SingleAsync(x => x.Name != "default");
            state.CurrentUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        await Tick(factory, source.Id, kind);
        await Tick(factory, source.Id, kind);
        Guid caseId;
        await using (var db = factory.CreateDbContext()) caseId = (await db.OperationsCases.SingleAsync()).Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => days.ReviewCaseAsync(destination.Id, caseId, "other-operator", "Evidence reviewed at another bank.", true, default));
        await days.ReviewCaseAsync(source.Id, caseId, "operations-test", "Reviewed the correlated payment and journal evidence.", true, default);
        await using (var db = factory.CreateDbContext())
        {
            var reviewed = Assert.Single(await db.OperationsCases.ToListAsync());
            Assert.Contains("Injected by", reviewed.Notes);
            Assert.Contains("Reviewed the correlated", reviewed.Notes);
            Assert.Equal(OperationsCaseStatus.Resolved, reviewed.Status);
            if (kind == "Wire")
            {
                var wires = await db.WireTransfers.OrderBy(x => x.CreatedDate).ToListAsync();
                Assert.Equal(2, wires.Count);
                Assert.Equal(ProcessingScenario.Standard, wires[1].Scenario);
                Assert.Equal(WireTraffic.Approver, wires[0].ApprovedBy);
                Assert.Equal(2, await db.OutboxMessages.CountAsync());
            }
            else
            {
                Assert.Equal(2, await db.AchEntries.CountAsync(x => x.Scenario == AchProcessingScenario.Standard));
                Assert.Equal(4, await db.OutboxMessages.CountAsync());
            }
            Assert.Empty(await db.LedgerEntries.ToListAsync()); Assert.Empty(await db.AchLedgerEntries.ToListAsync());
            Assert.All(await db.Accounts.ToListAsync(), x => { Assert.Equal(10000m, x.Balance); Assert.Equal(0m, x.HeldBalance); });
            Assert.Equal(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero), (await db.SimulationStates.SingleAsync(x => x.Name == "default")).CurrentUtc);
            Assert.Empty(await db.StudioDays.Where(x => x.BankId == destination.Id).ToListAsync());
        }
    }

    [Fact]
    public async Task Ineligible_generation_retains_incident_and_close_cancels_without_deleting_data()
    {
        var (factory, source, other) = await Seed();
        var days = new StudioDayService(factory);
        await days.StartAsync(source.Id, "Wire", "operations-test", false, default);
        StudioDay day;
        await using (var db = factory.CreateDbContext())
        {
            day = await db.StudioDays.SingleAsync();
            foreach (var account in await db.Accounts.Where(x => x.Customer.BankId == source.Id).ToListAsync()) account.HeldBalance = account.Balance;
            await db.SaveChangesAsync();
        }
        await days.QueueIncidentAsync(source.Id, day.Id, "Duplicate", default);
        await Tick(factory, source.Id, "Wire");
        await Assert.ThrowsAsync<InvalidOperationException>(() => days.AdvanceAsync(other.Id, day.Id, 0, true, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => days.AdvanceAsync(source.Id, day.Id, 0, false, default));
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal("Duplicate", (await db.StudioDays.SingleAsync()).PendingIncident);
            Assert.Empty(await db.WireTransfers.ToListAsync());
        }
        await days.AdvanceAsync(source.Id, day.Id, 0, true, default);
        await Tick(factory, source.Id, "Wire");
        await using (var db = factory.CreateDbContext())
        {
            var stored = await db.StudioDays.SingleAsync();
            Assert.NotNull(stored.EndedAt); Assert.Equal("None", stored.PendingIncident);
            var item = await db.OperationsCases.SingleAsync();
            Assert.Equal(OperationsCaseStatus.Resolved, item.Status); Assert.Contains("canceled", item.Resolution);
            Assert.True((await db.SimulationStates.SingleAsync(x => x.Name == WireTraffic.StateName(source.Id))).IsPaused);
            Assert.Equal(3, await db.Accounts.CountAsync());
        }
    }

    [Fact]
    public async Task Phase_progression_is_bounded_and_stale_advances_cannot_skip_a_phase()
    {
        var (factory, source, _) = await Seed();
        var days = new StudioDayService(factory);
        await days.StartAsync(source.Id, "Ach", "operations-test", false, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => days.StartAsync(source.Id, "Ach", "operations-test", false, default));
        StudioDay day;
        await using (var db = factory.CreateDbContext())
        {
            day = await db.StudioDays.SingleAsync();
            var settings = await db.StudioSettings.SingleAsync(); settings.RemainingRuns = 0;
            await db.SaveChangesAsync();
        }
        await days.AdvanceAsync(source.Id, day.Id, 0, false, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => days.AdvanceAsync(source.Id, day.Id, 0, false, default));
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(1, (await db.StudioDays.SingleAsync()).Phase);
            Assert.Equal(2, (await db.StudioSettings.SingleAsync()).RemainingRuns);
            var active = await db.SimulationStates.SingleAsync(x => x.Name != "default" && !x.IsPaused);
            Assert.Equal(AchTrafficMode.Suppliers, AchTraffic.Mode(active.Name));
        }
    }

    [Fact]
    public async Task Debrief_excludes_other_banks_and_manual_payments_but_retains_late_posting_evidence()
    {
        var (factory, source, other) = await Seed();
        var days = new StudioDayService(factory);
        await days.StartAsync(source.Id, "Wire", "operations-test", false, default);
        await Tick(factory, source.Id, "Wire");
        Guid id;
        await using (var db = factory.CreateDbContext())
        {
            var wire = await db.WireTransfers.SingleAsync(); id = wire.Id; wire.Status = WireStatus.Settled;
            var accounts = await db.Accounts.Include(x => x.Customer).ToListAsync();
            var manual = WireTraffic.Create(accounts.Single(x => x.Customer.BankId == source.Id), accounts.First(x => x.Customer.BankId == other.Id), DateTimeOffset.UtcNow);
            manual.CreatedBy = "human-maker"; db.WireTransfers.Add(manual);
            var outside = WireTraffic.Create(accounts.First(x => x.Customer.BankId == other.Id), accounts.Single(x => x.Customer.BankId == source.Id), DateTimeOffset.UtcNow);
            db.WireTransfers.Add(outside); await db.SaveChangesAsync();
        }
        var reader = new StudioDayDashboardService(factory);
        var snapshot = await reader.ReadAsync(source.Id, "Wire", default);
        Assert.Equal(1, snapshot.Generated); Assert.Equal(1, snapshot.AcceptedWithoutJournal);
        await days.AdvanceAsync(source.Id, snapshot.Day!.Id, 0, true, default);
        await using (var db = factory.CreateDbContext())
        {
            var journal = Guid.NewGuid();
            db.LedgerEntries.AddRange(
                new LedgerEntry { JournalId = journal, WireTransferId = id, AccountCode = "Deposits", AccountName = "Deposits", Debit = 125, Description = "Test" },
                new LedgerEntry { JournalId = journal, WireTransferId = id, AccountCode = "Cash", AccountName = "Cash", Credit = 125, Description = "Test" });
            await db.SaveChangesAsync();
        }
        var closed = await reader.ReadAsync(source.Id, "Wire", default);
        Assert.Equal(1, closed.Generated); Assert.Equal(0, closed.AcceptedWithoutJournal); Assert.True(Assert.Single(closed.Journals).Balanced);
    }

    private static Task Tick(Factory factory, Guid bankId, string kind) => kind == "Wire"
        ? new WireTrafficService(factory).TickAsync(bankId, default) : new AchTrafficService(factory).TickAsync(bankId, default);

    private static async Task<(Factory, Bank, Bank)> Seed()
    {
        var options = new DbContextOptionsBuilder<BankingDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
        var factory = new Factory(options);
        var source = Bank("101000019");
        var other = Bank("111901234");
        await using var db = factory.CreateDbContext();
        db.Accounts.AddRange(Account(source, 1), Account(other, 4), Account(other, 5));
        db.SimulationStates.Add(new SimulationState { Name = "default", CurrentUtc = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero) });
        await db.SaveChangesAsync(); return (factory, source, other);
    }
    private static Bank Bank(string routing) => new() { Name = "Fictional bank", RoutingNumber = routing, FedParticipantId = routing, Bic = "TESTUS00", TownName = "Test", CountryCode = "US", MasterAccountBalance = 100000 };
    private static Account Account(Bank bank, int profile) => new() { AccountNumber = TrafficPopulation.AccountNumber(bank.RoutingNumber, profile), Balance = 10000,
        Customer = new Customer { Name = "Fictional customer", BankId = bank.Id, Bank = bank } };
    private sealed class Factory(DbContextOptions<BankingDbContext> options) : IDbContextFactory<BankingDbContext>
    { public BankingDbContext CreateDbContext() => new(options); }
}
