using System.ComponentModel.DataAnnotations;

namespace Banking.Domain;

public enum WireDirection { Outgoing, Incoming }
public enum WireStatus { Created, Validated, ReadyForFed, SentToFed, PendingAtFed, Settled, Received, Completed, Rejected, Returned }
public enum MessageDirection { Outbound, Inbound }
public enum DeliveryStatus { Pending, Sent, Delivered, Failed }
public enum ProcessingScenario { Standard, PendingThenAccepted, FedRejects, MalformedIso }
public enum WireCaseType { ReturnRequest, Investigation }
public enum WireCaseStatus { Submitted, Resolved, ReturnCompleted, Rejected }
public enum PaymentRail
{
    Fedwire,
    FedNow,
    [Display(Name = "SWIFT international wire (CBPR+)")] SwiftCbprPlus,
    Ach,
    Check
}

public sealed class Bank
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(120)] public required string Name { get; set; }
    [MaxLength(9)] public required string RoutingNumber { get; set; }
    [MaxLength(12)] public required string FedParticipantId { get; set; }
    [MaxLength(11)] public required string Bic { get; set; }
    [MaxLength(35)] public required string TownName { get; set; }
    [MaxLength(2)] public required string CountryCode { get; set; }
    public decimal MasterAccountBalance { get; set; }
    public bool FedNowEnabled { get; set; } = true;
    public bool FedNowSendEnabled { get; set; } = true;
    public bool FedNowReceiveEnabled { get; set; } = true;
    public bool FedNowRequestForPaymentEnabled { get; set; } = true;
    public bool FedNowOnline { get; set; } = true;
    public bool SwiftEnabled { get; set; } = true;
    public List<Customer> Customers { get; set; } = [];
}

public sealed class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    public Bank Bank { get; set; } = null!;
    [MaxLength(120)] public required string Name { get; set; }
    public List<Account> Accounts { get; set; } = [];
}

public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    [MaxLength(34)] public required string AccountNumber { get; set; }
    public decimal Balance { get; set; }
    public decimal HeldBalance { get; set; }
    public decimal AvailableBalance => Balance - HeldBalance;
}

public sealed class WireTransfer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    public Bank Bank { get; set; } = null!;
    public Guid SenderBankId { get; set; }
    public Guid ReceiverBankId { get; set; }
    public Guid? FromAccountId { get; set; }
    public Guid? ToAccountId { get; set; }
    public WireDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public WireStatus Status { get; set; }
    [MaxLength(120)] public required string SenderName { get; set; }
    [MaxLength(120)] public required string ReceiverName { get; set; }
    [MaxLength(34)] public required string BeneficiaryAccountNumber { get; set; }
    public ProcessingScenario Scenario { get; set; }
    public PaymentRail Rail { get; set; }
    [MaxLength(35)] public string? Imad { get; set; }
    [MaxLength(35)] public string? Omad { get; set; }
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
    public List<IsoMessage> IsoMessages { get; set; } = [];
    public List<WireEvent> Events { get; set; } = [];
    public List<WireCase> Cases { get; set; } = [];
    public PaymentRoute? PaymentRoute { get; set; }
}

public sealed class WireCase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WireTransferId { get; set; }
    public WireTransfer WireTransfer { get; set; } = null!;
    public Guid RequestedByBankId { get; set; }
    public WireCaseType Type { get; set; }
    public WireCaseStatus Status { get; set; } = WireCaseStatus.Submitted;
    [MaxLength(500)] public required string Reason { get; set; }
    [MaxLength(20)] public required string RequestMessageType { get; set; }
    [MaxLength(20)] public string? ResponseMessageType { get; set; }
    [MaxLength(500)] public string? Resolution { get; set; }
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedDate { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CorrespondentRelationship
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FromBankId { get; set; }
    public Bank FromBank { get; set; } = null!;
    public Guid ToBankId { get; set; }
    public Bank ToBank { get; set; } = null!;
    [MaxLength(3)] public required string CurrencyCode { get; set; }
    [MaxLength(30)] public required string Rail { get; set; }
    [MaxLength(40)] public required string RelationshipType { get; set; }
    public bool IsActive { get; set; } = true;
    public int Priority { get; set; } = 100;
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PaymentRoute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PaymentId { get; set; }
    public WireTransfer Payment { get; set; } = null!;
    [MaxLength(30)] public required string Rail { get; set; }
    [MaxLength(3)] public required string CurrencyCode { get; set; }
    public Guid OriginBankId { get; set; }
    public Bank OriginBank { get; set; } = null!;
    public Guid DestinationBankId { get; set; }
    public Bank DestinationBank { get; set; } = null!;
    [MaxLength(30)] public required string RouteStatus { get; set; }
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
    public List<PaymentRouteStep> Steps { get; set; } = [];
}

public sealed class PaymentRouteStep
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PaymentRouteId { get; set; }
    public PaymentRoute PaymentRoute { get; set; } = null!;
    public int StepNumber { get; set; }
    public Guid FromBankId { get; set; }
    public Bank FromBank { get; set; } = null!;
    public Guid ToBankId { get; set; }
    public Bank ToBank { get; set; } = null!;
    [MaxLength(40)] public required string StepType { get; set; }
    [MaxLength(30)] public required string Status { get; set; }
    [MaxLength(100)] public string? MessageId { get; set; }
    public Guid? Uetr { get; set; }
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedDate { get; set; }
}

public sealed class LedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JournalId { get; set; }
    public Guid WireTransferId { get; set; }
    public WireTransfer WireTransfer { get; set; } = null!;
    [MaxLength(80)] public required string AccountCode { get; set; }
    [MaxLength(120)] public required string AccountName { get; set; }
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    [MaxLength(240)] public required string Description { get; set; }
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class IsoMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WireTransferId { get; set; }
    public WireTransfer WireTransfer { get; set; } = null!;
    [MaxLength(20)] public required string MessageType { get; set; }
    public MessageDirection Direction { get; set; }
    public required string XmlPayload { get; set; }
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WireEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WireTransferId { get; set; }
    public WireTransfer WireTransfer { get; set; } = null!;
    [MaxLength(60)] public required string EventType { get; set; }
    [MaxLength(500)] public required string Description { get; set; }
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class MessageDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WireTransferId { get; set; }
    [MaxLength(80)] public required string Destination { get; set; }
    public DeliveryStatus Status { get; set; }
    public int Attempts { get; set; }
    [MaxLength(500)] public string? LastError { get; set; }
    public DateTimeOffset UpdatedDate { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FedSettlement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CorrelationId { get; set; }
    [MaxLength(35)] public required string Imad { get; set; }
    [MaxLength(35)] public required string Omad { get; set; }
    [MaxLength(10)] public required string StatusCode { get; set; }
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;
}
