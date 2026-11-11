using System.ComponentModel.DataAnnotations;
using Banking.Domain;

namespace Banking.Web.Models;

public sealed record StatementsIndexViewModel(Guid BankId, DateTimeOffset Now, IReadOnlyList<CorrespondentAccount> Accounts,
    IReadOnlyList<AccountStatement> Statements, IReadOnlyDictionary<Guid, string> Banks,
    IReadOnlyList<StatementDelivery> Deliveries, IReadOnlyList<StatementReconciliation> Reconciliations);
public sealed record StatementDetailsViewModel(Guid BankId, AccountStatement Statement, StatementDelivery? Delivery,
    IReadOnlyList<StatementReconciliation> Reconciliations, IReadOnlySet<Guid> OwnWires);
public sealed record StatementAccountViewModel(Guid BankId, DateTimeOffset Now, CorrespondentAccount Account,
    IReadOnlyDictionary<Guid, string> Banks, IReadOnlyList<CorrespondentPaymentLink> Payments,
    IReadOnlyList<CorrespondentLedgerEntry> Ledger, IReadOnlyList<WireTransfer> IncomingWires, StatementSchedule? Schedule);
public sealed class OpenCorrespondentForm
{
    public Guid RespondentBankId { get; set; }
    [Required, StringLength(34)] public string AccountNumber { get; set; } = "";
    [Range(typeof(decimal), "0", "999999999999999")] public decimal OpeningBalance { get; set; } = 500_000;
}
public sealed class GenerateStatementForm
{
    public Guid AccountId { get; set; }
    public StatementKind Kind { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public Guid? Corrects { get; set; }
}
public sealed class StatementScheduleForm
{
    public Guid AccountId { get; set; }
    [Required, StringLength(100)] public string TimeZone { get; set; } = "America/Chicago";
    public TimeOnly LocalTime { get; set; } = new(18, 0);
    [StringLength(6000)] public string? Holidays { get; set; }
    public bool Enabled { get; set; } = true;
}
