using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class SimulationAndLearningExpansionTests
{
    [Fact]
    public void Generated_cross_rail_journals_preserve_double_entry_invariant()
    {
        var random = new Random(7429);
        for (var i = 0; i < 500; i++)
        {
            var amount = decimal.Round((decimal)random.NextDouble() * 1_000_000m + .01m, 2);
            var journal = Guid.NewGuid();
            var payment = Guid.NewGuid();
            var rail = Enum.GetValues<PaymentRail>()[random.Next(Enum.GetValues<PaymentRail>().Length)];
            var lines = new[]
            {
                new UnifiedJournalLine(journal, rail, payment, "DEBIT", "Debit", amount, 0, DateTimeOffset.UtcNow),
                new UnifiedJournalLine(journal, rail, payment, "CREDIT", "Credit", 0, amount, DateTimeOffset.UtcNow)
            };
            UnifiedAccountingPolicy.Validate(lines);
            Assert.Equal(0, lines.Sum(x => x.Debit - x.Credit));
        }
    }

    [Fact]
    public void Unified_accounting_rejects_unbalanced_or_two_sided_lines()
    {
        var id = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => UnifiedAccountingPolicy.Validate([
            new(id, PaymentRail.Ach, id, "A", "A", 10, 2, DateTimeOffset.UtcNow),
            new(id, PaymentRail.Ach, id, "B", "B", 0, 8, DateTimeOffset.UtcNow)]));
    }

    [Fact]
    public void Provisional_check_credit_changes_ledger_before_collected_and_available_balances()
    {
        var account = new Account { AccountNumber = "123", Balance = 100 };
        AccountAvailabilityPolicy.PlaceProvisionalCredit(account, 50);
        var provisional = AccountAvailabilityPolicy.Snapshot(account);
        Assert.Equal(100, provisional.Ledger);
        Assert.Equal(100, provisional.Collected);
        Assert.Equal(100, provisional.Available);
        Assert.Equal(50, provisional.PendingCredits);
        Assert.Equal(50, provisional.ProvisionalCredits);

        AccountAvailabilityPolicy.AcceptProvisionalCredit(account, 50);
        var collected = AccountAvailabilityPolicy.Snapshot(account);
        Assert.Equal(150, collected.Collected);
        Assert.Equal(150, collected.Available);
    }

    [Fact]
    public void Account_restrictions_exposure_and_credit_limits_block_value_creation()
    {
        var account = new Account { AccountNumber = "restricted", Balance = 100,
            OverdraftLimit = 25, Status = AccountStatus.Restricted };
        Assert.False(AccountAvailabilityPolicy.CanDebit(account, 10));
        account.Status = AccountStatus.Open;
        Assert.True(AccountAvailabilityPolicy.CanDebit(account, 125));
        Assert.False(AccountAvailabilityPolicy.CanDebit(account, 125.01m));

        var bank = new Bank { Name = "B", RoutingNumber = "101000019", FedParticipantId = "B",
            Bic = "BANKUS00XXX", TownName = "Town", CountryCode = "US", MasterAccountBalance = 100,
            FedwireCreditLimit = 20, AchExposureLimit = 500 };
        Assert.True(PaymentRiskPolicy.WithinFedwireLiquidity(bank, 120));
        Assert.False(PaymentRiskPolicy.WithinFedwireLiquidity(bank, 120.01m));
        Assert.False(PaymentRiskPolicy.WithinAchExposure(bank, 500.01m));
    }

    [Fact]
    public void Scenario_catalog_is_deterministic_and_documents_every_business_effect()
    {
        Assert.Equal(ScenarioFault.DuplicateDelivery,
            ScenarioCatalog.For(ProcessingScenario.DuplicateDelivery).Fault);
        Assert.Equal(ScenarioCatalog.Get("DuplicateDelivery"),
            ScenarioCatalog.Get("duplicatedelivery"));
        Assert.All(ScenarioCatalog.All, scenario =>
        {
            Assert.NotEmpty(scenario.ExpectedTransport);
            Assert.NotEmpty(scenario.ExpectedBusinessOutcome);
            Assert.NotEmpty(scenario.ExpectedAccountingOutcome);
            Assert.True(scenario.Seed > 0);
        });
    }

    [Fact]
    public void Rail_operating_policies_make_windows_returns_timeouts_and_fees_observable()
    {
        var now = new DateTimeOffset(2026, 7, 6, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(3, AchOperatingPolicy.For(now).Count);
        Assert.True(AchOperatingPolicy.ReturnDeadline(new DateOnly(2026, 7, 2), 2)
            > new DateOnly(2026, 7, 2));
        var hold = CheckAvailabilityPolicy.Standard(500, new DateOnly(2026, 7, 6));
        Assert.Equal(225, hold.ImmediateAmount);
        Assert.Equal(275, hold.DeferredAmount);
        Assert.True(FedNowOperationsPolicy.EvaluateReceiverTimeout(now, now.AddMinutes(2),
            TimeSpan.FromMinutes(1)).TimedOut);

        var quote = new SwiftSettlementQuote("USD", "EUR", 1000, .9m, 10,
            SwiftChargeBearer.Beneficiary, SwiftSettlementMethod.Serial);
        SwiftSettlementPolicy.Validate(quote);
        Assert.Equal(890, quote.BeneficiaryAmount);
    }

    [Theory]
    [InlineData("Delivered", 0)]
    [InlineData("Transport was delivered and the status was final.", 2)]
    [InlineData("The transport status correlated to settlement and a balanced journal.", 3)]
    public void Explanation_rubric_requires_multiple_observable_concepts(string explanation, int score) =>
        Assert.Equal(score, LearningExplanationRubric.Score(explanation));

    [Fact]
    public void Sql_model_contains_simulation_case_and_availability_schema()
    {
        var options = new DbContextOptionsBuilder<BankingDbContext>()
            .UseSqlServer("Server=localhost;Database=unused;User Id=unused;Password=unused;TrustServerCertificate=True")
            .Options;
        using var db = new BankingDbContext(options);
        var script = db.Database.GenerateCreateScript();
        Assert.Contains("CREATE TABLE [SimulationStates]", script);
        Assert.Contains("CREATE TABLE [OperationsCases]", script);
        Assert.Contains("[UncollectedBalance] decimal(19,4)", script);
        Assert.Contains("IX_OperationsCases_BankId_Status_DueDate", script);
        Assert.Contains("[LearnerExplanation] nvarchar(1000)", script);
        Assert.Contains("IX_LearningProgress_BankId_LearnerAlias_MissionId", script);
    }
}
