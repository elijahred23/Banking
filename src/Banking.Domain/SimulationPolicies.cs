namespace Banking.Domain;

public interface ISimulationClock
{
    DateTimeOffset UtcNow { get; }
    DateOnly BusinessDate => DateOnly.FromDateTime(UtcNow.UtcDateTime);
}

public sealed class SystemSimulationClock(TimeProvider timeProvider) : ISimulationClock
{
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
}

public enum ScenarioFault
{
    None, Delay, NetworkReject, MalformedPayload, DuplicateDelivery, OutOfOrderDelivery,
    DroppedDelivery, WorkerCrash, BrokerUnavailable, DatabaseTimeout, ControlTotalMismatch
}

public sealed record ScenarioDefinition(string Id, string Title, ScenarioFault Fault,
    int DelaySeconds, string ExpectedTransport, string ExpectedBusinessOutcome,
    string ExpectedAccountingOutcome, int Seed);

public static class ScenarioCatalog
{
    private static readonly IReadOnlyDictionary<string, ScenarioDefinition> Definitions =
        new Dictionary<string, ScenarioDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["Standard"] = new("Standard", "Normal processing", ScenarioFault.None, 0,
                "Delivered", "Accepted", "One balanced value journal", 101),
            ["PendingThenAccepted"] = new("PendingThenAccepted", "Delayed final acceptance",
                ScenarioFault.Delay, 2, "Delivered with intermediate status", "Accepted",
                "No posting before final acceptance", 211),
            ["FedRejects"] = new("FedRejects", "Network rejection", ScenarioFault.NetworkReject, 0,
                "Delivered", "Rejected", "Hold released; no value journal", 307),
            ["MalformedIso"] = new("MalformedIso", "Malformed payload", ScenarioFault.MalformedPayload, 0,
                "Delivered", "Rejected by validation", "No value journal", 401),
            ["DuplicateDelivery"] = new("DuplicateDelivery", "Duplicate transport delivery",
                ScenarioFault.DuplicateDelivery, 0, "Delivered twice", "Accepted once",
                "Exactly one value journal", 503),
            ["OutOfOrderDelivery"] = new("OutOfOrderDelivery", "Out-of-order status delivery",
                ScenarioFault.OutOfOrderDelivery, 1, "Status order differs", "Accepted",
                "Exactly one final posting", 601),
            ["DroppedDelivery"] = new("DroppedDelivery", "Dropped first delivery",
                ScenarioFault.DroppedDelivery, 2, "Retry required", "Accepted after retry",
                "No posting before acceptance", 701),
            ["ControlTotalMismatch"] = new("ControlTotalMismatch", "File control mismatch",
                ScenarioFault.ControlTotalMismatch, 0, "File delivered", "File rejected",
                "Holds released; no settlement", 809)
        };

    public static ScenarioDefinition Get(string id) => Definitions.TryGetValue(id, out var definition)
        ? definition : Definitions["Standard"];

    public static ScenarioDefinition For(ProcessingScenario scenario) => Get(scenario.ToString());
    public static IReadOnlyCollection<ScenarioDefinition> All => Definitions.Values.ToList();
}

public sealed record AccountAvailability(decimal Ledger, decimal Collected, decimal Available,
    decimal Holds, decimal PendingCredits, decimal ProvisionalCredits);

public static class AccountAvailabilityPolicy
{
    public static AccountAvailability Snapshot(Account account) => new(account.Balance,
        account.CollectedBalance, account.AvailableBalance, account.HeldBalance,
        account.PendingCreditBalance, account.ProvisionalCreditBalance);

    public static bool CanDebit(Account account, decimal amount) => account.Status == AccountStatus.Open
        && amount > 0 && account.AvailableBalance >= amount;

    public static void PlaceProvisionalCredit(Account account, decimal amount)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        account.PendingCreditBalance += amount;
        account.ProvisionalCreditBalance += amount;
    }

    public static void AcceptProvisionalCredit(Account account, decimal amount)
    {
        if (amount <= 0 || amount > account.PendingCreditBalance)
            throw new InvalidOperationException("Only an existing pending credit can be accepted and posted.");
        account.PendingCreditBalance -= amount;
        account.ProvisionalCreditBalance = Math.Max(0, account.ProvisionalCreditBalance - amount);
        account.Balance += amount;
    }
}

public sealed record UnifiedJournalLine(Guid JournalId, PaymentRail Rail, Guid PaymentId,
    string AccountCode, string AccountName, decimal Debit, decimal Credit, DateTimeOffset PostedDate);

public static class UnifiedAccountingPolicy
{
    public static void Validate(IEnumerable<UnifiedJournalLine> lines)
    {
        var materialized = lines.ToList();
        if (materialized.Count < 2 || materialized.Any(x => x.Debit < 0 || x.Credit < 0
                || (x.Debit == 0) == (x.Credit == 0))
            || materialized.Sum(x => x.Debit) != materialized.Sum(x => x.Credit))
            throw new InvalidOperationException("A journal requires balanced, positive, one-sided lines.");
    }

    public static IReadOnlyDictionary<string, decimal> TrialBalance(IEnumerable<UnifiedJournalLine> lines) =>
        lines.GroupBy(x => x.AccountCode).ToDictionary(x => x.Key,
            x => x.Sum(y => y.Debit - y.Credit));
}

public static class PaymentRiskPolicy
{
    public static bool WithinFedwireLiquidity(Bank bank, decimal amount) => amount > 0
        && bank.MasterAccountBalance + bank.FedwireCreditLimit >= amount;

    public static bool WithinAchExposure(Bank bank, decimal pendingCreditTotal) =>
        pendingCreditTotal >= 0 && pendingCreditTotal <= bank.AchExposureLimit;
}
