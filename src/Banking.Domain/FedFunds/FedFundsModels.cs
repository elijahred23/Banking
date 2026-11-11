using System.ComponentModel.DataAnnotations;

namespace Banking.Domain;

public enum FedFundsTradeStatus { Proposed, PendingApprovals, FundingPending, FundingException, Active, MaturityDue, Repaid, Overdue, Rejected, Cancelled }
public enum FedFundsObligationType { Funding, Principal, Interest }
public enum FedFundsObligationStatus { Pending, Submitted, Settled, Failed }
public enum FedFundsQuoteSide { Borrow, Lend }

public sealed class FedFundsTrade
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LenderBankId { get; set; }
    public Guid BorrowerBankId { get; set; }
    public Guid ProposalBankId { get; set; }
    public decimal Principal { get; set; }
    // Percentage, e.g. 4.50, not the fractional value 0.045.
    public decimal AnnualRate { get; set; }
    public DateOnly ValueDate { get; set; }
    public DateOnly MaturityDate { get; set; }
    public FedFundsTradeStatus Status { get; set; } = FedFundsTradeStatus.Proposed;
    public bool CreditReserved { get; set; }
    [MaxLength(120)] public string CreatedBy { get; set; } = "";
    [MaxLength(120)] public string? AcceptedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public byte[] RowVersion { get; set; } = [];
    public List<FedFundsApproval> Approvals { get; set; } = [];
    public List<FedFundsAccrual> Accruals { get; set; } = [];
    public List<FedFundsPaymentObligation> Obligations { get; set; } = [];
    public List<FedFundsEvent> Events { get; set; } = [];
}
public sealed class FedFundsQuote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    public FedFundsQuoteSide Side { get; set; }
    public decimal MinAmount { get; set; }
    public decimal MaxAmount { get; set; }
    public decimal Rate { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class FedFundsCreditLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LenderBankId { get; set; }
    public Guid BorrowerBankId { get; set; }
    public decimal ApprovedLimit { get; set; }
    public decimal ReservedAmount { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public byte[] RowVersion { get; set; } = [];
}
public sealed class FedFundsApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TradeId { get; set; }
    public FedFundsTrade Trade { get; set; } = null!;
    public Guid BankId { get; set; }
    [MaxLength(120)] public string OperatorId { get; set; } = "";
    public bool Approved { get; set; }
    public DateTimeOffset DecidedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class FedFundsAccrual
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TradeId { get; set; }
    public FedFundsTrade Trade { get; set; } = null!;
    public DateOnly AccrualDate { get; set; }
    public decimal Amount { get; set; }
    public Guid LenderJournalId { get; set; }
    public Guid BorrowerJournalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class FedFundsPaymentObligation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TradeId { get; set; }
    public FedFundsTrade Trade { get; set; } = null!;
    public FedFundsObligationType Type { get; set; }
    public decimal Amount { get; set; }
    public DateOnly DueDate { get; set; }
    public FedFundsObligationStatus Status { get; set; }
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
    public Guid? WireTransferId { get; set; }
    public int Attempts { get; set; }
    public Guid? LenderJournalId { get; set; }
    public Guid? BorrowerJournalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class FedFundsEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TradeId { get; set; }
    public FedFundsTrade Trade { get; set; } = null!;
    [MaxLength(60)] public string EventType { get; set; } = "";
    [MaxLength(120)] public string ActorId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(1000)] public string Details { get; set; } = "";
}
