using Banking.Domain;
using Banking.Infrastructure;

namespace Banking.Web.Models;

public sealed record PaymentStatusSummary(int Created, int Pending, int Settled, int Rejected)
{
    public int Total => Created + Pending + Settled + Rejected;
}

public sealed record OperationsExceptionViewModel(
    string Area, string Type, string Reference, string Detail, DateTimeOffset OccurredDate);

public sealed record SettlementMovementViewModel(
    string Rail, string Reference, string Counterparty, decimal Amount, DateTimeOffset RecordedDate);

public sealed record JournalSummaryViewModel(
    Guid JournalId, string Rail, int EntryCount, decimal Debits, decimal Credits, DateTimeOffset CreatedDate)
{
    public bool IsBalanced => Debits == Credits;
}

public sealed record RailHealthViewModel(string Rail, string Status, string Detail);

public sealed record DeadLetterViewModel(Guid Id, string Queue, int Attempts, string Error,
    DateTimeOffset FailedDate);

public sealed record StuckOutboxViewModel(Guid Id, string Queue, int Attempts, string? Error,
    DateTimeOffset CreatedDate);

public sealed record OperationsCaseViewModel(Guid Id, string Rail, string CaseType, string Reference,
    string Summary, OperationsCaseStatus Status, OperationsCaseSeverity Severity, string? AssignedTo,
    DateTimeOffset DueDate, string? Notes);

public sealed record AccountAvailabilityViewModel(string AccountNumber, string Customer,
    decimal Ledger, decimal Collected, decimal Available, decimal Holds, decimal Pending,
    decimal Provisional, AccountStatus Status);

public sealed record OperationsDashboardViewModel(
    Bank Bank,
    PaymentStatusSummary Payments,
    QueueMonitorSnapshot QueueSnapshot,
    IReadOnlyList<OperationsExceptionViewModel> Exceptions,
    IReadOnlyList<SettlementMovementViewModel> SettlementMovements,
    IReadOnlyList<JournalSummaryViewModel> Journals,
    IReadOnlyList<RailHealthViewModel> Rails,
    IReadOnlyList<DeadLetterViewModel> DeadLetters,
    IReadOnlyList<StuckOutboxViewModel> StuckOutbox,
    PaymentReconciliationResult Reconciliation,
    SimulationState Simulation,
    IReadOnlyList<OperationsCaseViewModel> Cases,
    IReadOnlyList<AccountAvailabilityViewModel> AccountAvailability,
    IReadOnlyDictionary<string, decimal> TrialBalance,
    DateTimeOffset RefreshedDate);
