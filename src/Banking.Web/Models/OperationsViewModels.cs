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

public sealed record OperationsDashboardViewModel(
    Bank Bank,
    PaymentStatusSummary Payments,
    QueueMonitorSnapshot QueueSnapshot,
    IReadOnlyList<OperationsExceptionViewModel> Exceptions,
    IReadOnlyList<SettlementMovementViewModel> SettlementMovements,
    IReadOnlyList<JournalSummaryViewModel> Journals,
    IReadOnlyList<RailHealthViewModel> Rails,
    DateTimeOffset RefreshedDate);
