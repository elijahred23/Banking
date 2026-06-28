using System.ComponentModel.DataAnnotations;

namespace Banking.Domain;

public enum CheckDepositStatus
{
    Captured,
    Validated,
    ImageCashLetterCreated,
    SentToExchange,
    PresentedToPayingBank,
    Settled,
    Returned,
    Rejected
}

public enum CheckImageSide { Front, Back }
public enum CheckImageFormat { Tiff }

public enum CheckReturnReason
{
    InsufficientFunds,
    AccountClosed,
    StopPayment,
    InvalidMicr,
    DuplicatePresentment,
    ImageQualityFailure,
    PayingBankNotFound
}

public enum CheckProcessingScenario
{
    Standard,
    PayingBankReturns,
    InvalidMicr,
    PoorImageQuality,
    DuplicatePresentment
}

public sealed class CheckDeposit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DepositoryBankId { get; set; }
    public Bank DepositoryBank { get; set; } = null!;
    public Guid? PayingBankId { get; set; }
    public Bank? PayingBank { get; set; }
    public Guid DepositingAccountId { get; set; }
    public Account DepositingAccount { get; set; } = null!;
    public Guid? CheckCashLetterId { get; set; }
    public CheckCashLetter? CashLetter { get; set; }
    [MaxLength(120)] public required string DepositorName { get; set; }
    [MaxLength(9)] public required string PayingRoutingNumber { get; set; }
    [MaxLength(34)] public required string PayingAccountNumber { get; set; }
    [MaxLength(15)] public required string CheckNumber { get; set; }
    [MaxLength(80)] public required string RawMicrLine { get; set; }
    public decimal Amount { get; set; }
    public CheckDepositStatus Status { get; set; } = CheckDepositStatus.Captured;
    public CheckProcessingScenario Scenario { get; set; } = CheckProcessingScenario.Standard;
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
    public string? ImageCashLetterPayload { get; set; }
    [MaxLength(10)] public string? ReturnCode { get; set; }
    [MaxLength(240)] public string? ReturnReason { get; set; }
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
    public List<CheckImage> Images { get; set; } = [];
    public List<CheckEvent> Events { get; set; } = [];
    public List<CheckReturn> Returns { get; set; } = [];
    public List<CheckLedgerEntry> LedgerEntries { get; set; } = [];
}

public sealed class CheckImage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CheckDepositId { get; set; }
    public CheckDeposit CheckDeposit { get; set; } = null!;
    public CheckImageSide Side { get; set; }
    public CheckImageFormat Format { get; set; } = CheckImageFormat.Tiff;
    [MaxLength(120)] public required string FileName { get; set; }
    [MaxLength(80)] public required string ContentType { get; set; }
    public byte[] Content { get; set; } = [];
    public int SizeBytes { get; set; }
    [MaxLength(128)] public required string Sha256Hash { get; set; }
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CheckCashLetter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DepositoryBankId { get; set; }
    public Bank DepositoryBank { get; set; } = null!;
    [MaxLength(9)] public required string DestinationRoutingNumber { get; set; }
    [MaxLength(9)] public required string OriginRoutingNumber { get; set; }
    [MaxLength(1)] public required string FileIdModifier { get; set; }
    public string RawX937Payload { get; set; } = string.Empty;
    [MaxLength(30)] public string Status { get; set; } = "Created";
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
    public List<CheckDeposit> Deposits { get; set; } = [];
}

public sealed class CheckReturn
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CheckDepositId { get; set; }
    public CheckDeposit CheckDeposit { get; set; } = null!;
    [MaxLength(10)] public required string ReturnCode { get; set; }
    [MaxLength(240)] public required string Reason { get; set; }
    public DateTimeOffset ReceivedDate { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CheckLedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JournalId { get; set; }
    public Guid CheckDepositId { get; set; }
    public CheckDeposit CheckDeposit { get; set; } = null!;
    [MaxLength(80)] public required string AccountCode { get; set; }
    [MaxLength(120)] public required string AccountName { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    [MaxLength(240)] public required string Description { get; set; }
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CheckEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CheckDepositId { get; set; }
    public CheckDeposit CheckDeposit { get; set; } = null!;
    [MaxLength(60)] public required string EventType { get; set; }
    [MaxLength(500)] public required string Description { get; set; }
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
}
