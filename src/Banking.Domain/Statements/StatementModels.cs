using System.ComponentModel.DataAnnotations;

namespace Banking.Domain;

public sealed class CorrespondentAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IssuerBankId { get; set; }
    public Guid RespondentBankId { get; set; }
    [MaxLength(34)] public string AccountNumber { get; set; } = "";
    public decimal OpeningBalance { get; set; }
    public decimal IssuerBalance { get; set; }
    public decimal RespondentBalance { get; set; }
    public decimal HeldBalance { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
public enum CorrespondentPaymentStatus { Pending, Posted, Rejected }
public sealed class CorrespondentPaymentLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public CorrespondentAccount Account { get; set; } = null!;
    public Guid WireTransferId { get; set; }
    public bool IsCredit { get; set; }
    public CorrespondentPaymentStatus Status { get; set; }
    public DateOnly ValueDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class CorrespondentLedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public CorrespondentAccount Account { get; set; } = null!;
    public Guid BankId { get; set; }
    public Guid PostingId { get; set; }
    public Guid JournalId { get; set; }
    public bool IsAccountLine { get; set; }
    [MaxLength(80)] public string AccountCode { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public DateTimeOffset BookingAt { get; set; }
    public DateOnly ValueDate { get; set; }
    public Guid? WireTransferId { get; set; }
    public Guid CorrelationId { get; set; }
    [MaxLength(200)] public string Reference { get; set; } = "";
}
public enum StatementKind { Intraday, Daily, Notification }
public sealed class AccountStatement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public CorrespondentAccount Account { get; set; } = null!;
    public Guid IssuerBankId { get; set; }
    public Guid RecipientBankId { get; set; }
    [MaxLength(100)] public string StatementNumber { get; set; } = "";
    public StatementKind Kind { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public int Version { get; set; } = 1;
    public int SequenceNumber { get; set; }
    public Guid? PreviousStatementId { get; set; }
    public Guid? NotificationPostingId { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal ClosingBalance { get; set; }
    public decimal TotalDebits { get; set; }
    public decimal TotalCredits { get; set; }
    public decimal GenerationDifference { get; set; }
    [MaxLength(120)] public string IssuerName { get; set; } = "";
    [MaxLength(120)] public string RecipientName { get; set; } = "";
    [MaxLength(34)] public string AccountNumber { get; set; } = "";
    public string XmlPayload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public List<AccountStatementEntry> Entries { get; set; } = [];
}
public sealed class AccountStatementEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StatementId { get; set; }
    public AccountStatement Statement { get; set; } = null!;
    public Guid LedgerEntryId { get; set; }
    public Guid PostingId { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid? WireTransferId { get; set; }
    public decimal Amount { get; set; }
    public bool IsCredit { get; set; }
    public DateTimeOffset BookingAt { get; set; }
    public DateOnly ValueDate { get; set; }
    [MaxLength(200)] public string Reference { get; set; } = "";
}
public sealed class StatementSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public CorrespondentAccount Account { get; set; } = null!;
    [MaxLength(100)] public string TimeZone { get; set; } = "America/Chicago";
    public TimeOnly LocalTime { get; set; } = new(18, 0);
    public string HolidaysJson { get; set; } = "[]";
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset NextRunAt { get; set; }
    public bool Enabled { get; set; } = true;
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    [MaxLength(1000)] public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public enum StatementDeliveryStatus { Pending, Delivered }
public sealed class StatementDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StatementId { get; set; }
    public AccountStatement Statement { get; set; } = null!;
    public Guid RecipientBankId { get; set; }
    public StatementDeliveryStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}
public enum StatementReconciliationStatus { Reconciled, TimingDifference, Exception }
public sealed class StatementReconciliation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StatementId { get; set; }
    public AccountStatement Statement { get; set; } = null!;
    public Guid BankId { get; set; }
    public decimal ExpectedBalance { get; set; }
    public decimal ActualBalance { get; set; }
    public decimal Difference { get; set; }
    public StatementReconciliationStatus Status { get; set; }
    public string FindingsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed record StatementFinding(string Type, string Reference, decimal Difference, string Explanation);
public sealed record CorrespondentPostingNotice(Guid AccountId, Guid PostingId);
public sealed record StatementReady(Guid StatementId, Guid RecipientBankId);

public static class StatementQueues
{
    public const string Posting = "CORRESPONDENT.POSTING";
    public const string Delivery = "STATEMENT.READY";
}
