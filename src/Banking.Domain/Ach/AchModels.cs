using System.ComponentModel.DataAnnotations;

namespace Banking.Domain;

public enum AchStandardEntryClass { Ppd, Ccd, Ctx, Web, Tel, Iat }
public enum AchTransactionCode { CheckingCredit = 22, CheckingDebit = 27, SavingsCredit = 32, SavingsDebit = 37 }
public enum AchEntryStatus { Entered, Validated, Batched, SentToOperator, Settled, Returned, NocReceived, Posted }
public enum AchPaymentPurpose { Payroll, VendorPayment, ConsumerDebit, TaxPaymentEftps }
public enum AchProcessingScenario
{
    Standard,
    InvalidRoutingNumber,
    AccountNotFound,
    InvalidAccountNumber,
    InsufficientFunds,
    AccountClosed,
    NocCorrectedAccount,
    NocCorrectedRouting,
    NocCorrectedAccountAndRouting,
    MalformedFile,
    ControlTotalMismatch
}

public sealed class AchEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OriginatingBankId { get; set; }
    public Bank OriginatingBank { get; set; } = null!;
    public Guid? ReceivingBankId { get; set; }
    public Bank? ReceivingBank { get; set; }
    public Guid OriginatingAccountId { get; set; }
    public Account OriginatingAccount { get; set; } = null!;
    public Guid? AchBatchId { get; set; }
    public AchBatch? Batch { get; set; }
    [MaxLength(16)] public string CompanyName { get; set; } = "";
    [MaxLength(10)] public string CompanyId { get; set; } = "";
    public AchStandardEntryClass SecCode { get; set; }
    [MaxLength(22)] public string ReceiverName { get; set; } = "";
    [MaxLength(9)] public string ReceivingRoutingNumber { get; set; } = "";
    [MaxLength(17)] public string ReceivingAccountNumber { get; set; } = "";
    public AchTransactionCode TransactionCode { get; set; }
    public decimal Amount { get; set; }
    [MaxLength(10)] public string EntryDescription { get; set; } = "";
    public DateOnly EffectiveEntryDate { get; set; }
    [MaxLength(80)] public string? Addenda05 { get; set; }
    [MaxLength(3)] public string? ReturnCode { get; set; }
    [MaxLength(15)] public string? TraceNumber { get; set; }
    public AchEntryStatus Status { get; set; }
    public AchPaymentPurpose Purpose { get; set; }
    public AchProcessingScenario Scenario { get; set; }
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AchBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OriginatingBankId { get; set; }
    public Bank OriginatingBank { get; set; } = null!;
    public Guid? AchFileId { get; set; }
    public AchFile? File { get; set; }
    public AchStandardEntryClass SecCode { get; set; }
    [MaxLength(16)] public string CompanyName { get; set; } = "";
    [MaxLength(10)] public string CompanyId { get; set; } = "";
    public DateOnly EffectiveEntryDate { get; set; }
    [MaxLength(3)] public string ServiceClassCode { get; set; } = "200";
    [MaxLength(20)] public string Status { get; set; } = "Open";
    public int BatchNumber { get; set; }
    public List<AchEntry> Entries { get; set; } = [];
}

public sealed class AchFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OriginatingBankId { get; set; }
    public Bank OriginatingBank { get; set; } = null!;
    [MaxLength(9)] public string ImmediateDestinationRoutingNumber { get; set; } = "";
    [MaxLength(9)] public string ImmediateOriginRoutingNumber { get; set; } = "";
    [MaxLength(1)] public string FileIdModifier { get; set; } = "A";
    public string RawNachaPayload { get; set; } = "";
    [MaxLength(20)] public string Status { get; set; } = "Created";
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
    public List<AchBatch> Batches { get; set; } = [];
}

public sealed class AchReturn
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AchEntryId { get; set; }
    public AchEntry Entry { get; set; } = null!;
    [MaxLength(3)] public string ReturnCode { get; set; } = "";
    [MaxLength(240)] public string Reason { get; set; } = "";
    public DateTimeOffset ReceivedDate { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AchNotificationOfChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AchEntryId { get; set; }
    public AchEntry Entry { get; set; } = null!;
    [MaxLength(3)] public string ChangeCode { get; set; } = "";
    [MaxLength(35)] public string CorrectedData { get; set; } = "";
    [MaxLength(240)] public string Description { get; set; } = "";
    public DateTimeOffset ReceivedDate { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AchLedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JournalId { get; set; }
    public Guid AchEntryId { get; set; }
    public AchEntry Entry { get; set; } = null!;
    [MaxLength(80)] public string AccountCode { get; set; } = "";
    [MaxLength(120)] public string AccountName { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    [MaxLength(240)] public string Description { get; set; } = "";
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class EftpsTaxPayment
{
    public string TaxpayerIdentificationNumber { get; set; } = "";
    public string TaxTypeCode { get; set; } = "";
    public string TaxPeriodEndDate { get; set; } = "";
    public decimal Amount { get; set; }
}
