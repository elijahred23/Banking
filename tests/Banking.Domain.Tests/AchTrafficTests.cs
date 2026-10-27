using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class AchTrafficTests
{
    [Theory]
    [InlineData(AchTrafficMode.Payroll, AchStandardEntryClass.Ppd, AchTransactionCode.CheckingCredit, 650, 2400)]
    [InlineData(AchTrafficMode.Suppliers, AchStandardEntryClass.Ccd, AchTransactionCode.CheckingCredit, 125, 1800)]
    [InlineData(AchTrafficMode.Utilities, AchStandardEntryClass.Web, AchTransactionCode.CheckingDebit, 35, 220)]
    public void Activities_use_valid_random_cent_amounts_and_preserve_funds_before_settlement(
        AchTrafficMode mode, AchStandardEntryClass sec, AchTransactionCode code, int minimum, int maximum)
    {
        var company = Account(Guid.NewGuid(), "101000019", 450000m);
        var receivers = Enumerable.Range(0, 4).Select(_ => Account(Guid.NewGuid(), "103000648", 9000m)).ToList();
        var entries = AchTraffic.CreateRun(company, receivers, mode, new DateOnly(2026, 10, 31), DateTimeOffset.UtcNow, new Random(41));
        Assert.Equal(4, entries.Count);
        Assert.Equal(4, entries.Select(x => x.Amount).Distinct().Count());
        Assert.All(entries, entry =>
        {
            Assert.Equal(sec, entry.SecCode);
            Assert.Equal(code, entry.TransactionCode);
            Assert.InRange(entry.Amount, minimum, maximum);
            Assert.Equal(decimal.Round(entry.Amount, 2), entry.Amount);
            Assert.Equal(new DateOnly(2026, 11, 2), entry.EffectiveEntryDate);
            Assert.Empty(AchRules.Validate(entry, new DateOnly(2026, 10, 31)));
            Assert.Equal(AchEntryStatus.PendingApproval, entry.Status);
            AchTraffic.Approve(entry, DateTimeOffset.UtcNow);
            Assert.NotEqual(entry.CreatedBy, entry.ApprovedBy);
            Assert.Equal(AchEntryStatus.Entered, entry.Status);
            Assert.Throws<InvalidOperationException>(() => AchTraffic.Approve(entry, DateTimeOffset.UtcNow));
        });
        Assert.Single(entries.Select(x => x.Addenda05).Distinct());
        Assert.Equal(450000m, company.Balance);
        Assert.Equal(0m, company.HeldBalance);
        Assert.All(receivers, x => Assert.Equal(9000m, x.Balance));
    }

    [Fact]
    public void Generation_rejects_closed_accounts_and_cannot_approve_human_entries()
    {
        var company = Account(Guid.NewGuid(), "101000019", 450000m);
        var receiver = Account(Guid.NewGuid(), "103000648", 9000m);
        receiver.Status = AccountStatus.Frozen;
        Assert.Throws<InvalidOperationException>(() => AchTraffic.CreateRun(company, [receiver], AchTrafficMode.Payroll,
            new DateOnly(2026, 10, 26), DateTimeOffset.UtcNow));
        var human = new AchEntry { CreatedBy = "human", Status = AchEntryStatus.PendingApproval };
        Assert.Throws<InvalidOperationException>(() => AchTraffic.Approve(human, DateTimeOffset.UtcNow));
        receiver.Status = AccountStatus.Open;
        company.HeldBalance = company.Balance;
        Assert.Empty(AchTraffic.CreateRun(company, [receiver], AchTrafficMode.Payroll, new DateOnly(2026, 10, 26), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Random_amounts_are_reproducible_varied_and_bounded_by_funds()
    {
        var first = new Random(27);
        var second = new Random(27);
        var samples = Enumerable.Range(0, 30).Select(_ => TrafficAmounts.Sample(1m, 250m, 13.57m, first)).ToArray();
        Assert.Equal(samples, Enumerable.Range(0, 30).Select(_ => TrafficAmounts.Sample(1m, 250m, 13.57m, second)));
        Assert.True(samples.Distinct().Count() > 20);
        Assert.All(samples, x => { Assert.InRange(x, 1m, 13.57m); Assert.Equal(decimal.Round(x, 2), x); });
        Assert.Equal(0m, TrafficAmounts.Sample(1m, 250m, .99m));
        Assert.Equal(1m, TrafficAmounts.Sample(1m, 250m, 1m));
    }

    [Fact]
    public async Task Population_adds_customers_and_accounts_once_and_never_resets_balances()
    {
        var factory = await SeededFactory();
        Guid accountId;
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(44, await db.Customers.CountAsync());
            Assert.Equal(76, await db.Accounts.CountAsync());
            var account = await db.Accounts.FirstAsync();
            accountId = account.Id; account.Balance = 123.45m; account.HeldBalance = 12.34m;
            await db.SaveChangesAsync();
        }
        await TrafficPopulation.SeedAsync(factory);
        await using var check = factory.CreateDbContext();
        Assert.Equal(44, await check.Customers.CountAsync());
        Assert.Equal(76, await check.Accounts.CountAsync());
        var preserved = await check.Accounts.SingleAsync(x => x.Id == accountId);
        Assert.Equal(123.45m, preserved.Balance);
        Assert.Equal(12.34m, preserved.HeldBalance);
    }

    [Fact]
    public async Task Generator_persists_run_then_enqueues_approvals_once_and_respects_pause_and_bank_scope()
    {
        var factory = await SeededFactory();
        Guid bankId;
        await using (var db = factory.CreateDbContext())
        {
            bankId = (await db.Banks.SingleAsync(x => x.RoutingNumber == "101000019")).Id;
            db.SimulationStates.Add(new SimulationState { Name = "default", CurrentUtc = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero) });
            db.SimulationStates.Add(new SimulationState { Name = AchTraffic.StateName(bankId, AchTrafficMode.Payroll), IsPaused = false, SpeedMultiplier = 1 });
            await db.SaveChangesAsync();
        }
        var traffic = new AchTrafficService(factory);
        await traffic.TickAsync(Guid.NewGuid(), default);
        await traffic.TickAsync(bankId, default);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(4, await db.AchEntries.CountAsync());
            Assert.Empty(await db.OutboxMessages.ToListAsync());
            Assert.All(await db.AchEntries.ToListAsync(), x => Assert.Equal(bankId, x.OriginatingBankId));
        }
        await traffic.TickAsync(bankId, default);
        await traffic.TickAsync(bankId, default);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(4, await db.OutboxMessages.CountAsync());
            Assert.All(await db.AchEntries.ToListAsync(), x => Assert.Equal(AchEntryStatus.Entered, x.Status));
            var state = await db.SimulationStates.SingleAsync(x => x.Name != "default");
            state.IsPaused = true; state.CurrentUtc = DateTimeOffset.MinValue;
            await db.SaveChangesAsync();
        }
        await traffic.TickAsync(bankId, default);
        await using var check = factory.CreateDbContext();
        Assert.Equal(4, await check.AchEntries.CountAsync());
    }

    private static Account Account(Guid bankId, string routing, decimal balance) => new()
    {
        AccountNumber = "123456789", Balance = balance,
        Customer = new Customer { BankId = bankId, Name = "Fictional person", Bank = Bank(routing) }
    };
    private static Bank Bank(string routing) => new() { Name = "Lab bank", RoutingNumber = routing,
        FedParticipantId = "LAB", Bic = "LABBUS33XXX", TownName = "Tulsa", CountryCode = "US" };
    private static async Task<Factory> SeededFactory()
    {
        var options = new DbContextOptionsBuilder<BankingDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
        var factory = new Factory(options);
        await using (var db = factory.CreateDbContext())
        {
            db.Banks.AddRange(TrafficPopulation.Profiles.Select(x => x.Routing).Distinct().Select(Bank));
            await db.SaveChangesAsync();
        }
        await TrafficPopulation.SeedAsync(factory);
        return factory;
    }
    private sealed class Factory(DbContextOptions<BankingDbContext> options) : IDbContextFactory<BankingDbContext>
    { public BankingDbContext CreateDbContext() => new(options); }
}
