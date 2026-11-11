using System.ComponentModel.DataAnnotations;
namespace Banking.Domain;

public sealed class CorrespondentServiceAgreement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    [MaxLength(120)] public string CreatedBy { get; set; } = "";
    public bool IssuerApproved { get; set; }
    public bool RespondentApproved { get; set; }
    public bool Suspended { get; set; }
    public decimal TransferLimit { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsActive => IssuerApproved && RespondentApproved && !Suspended;
}
public enum CorrespondentOrderStatus { Pending, Posted, Rejected }
public sealed class CorrespondentBookOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceAccountId { get; set; }
    public Guid DestinationAccountId { get; set; }
    public decimal Amount { get; set; }
    [MaxLength(120)] public string CreatedBy { get; set; } = "";
    [MaxLength(120)] public string? DecidedBy { get; set; }
    public CorrespondentOrderStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}
public enum CorrespondentFundingStrategy { Fixed, Threshold, Sweep, ReplenishToMinimum }
public sealed class CorrespondentFundingRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceAccountId { get; set; }
    public Guid DestinationAccountId { get; set; }
    public CorrespondentFundingStrategy Strategy { get; set; }
    public decimal Threshold { get; set; }
    public decimal Target { get; set; }
    public decimal Maximum { get; set; }
    public DateTimeOffset NextRunAt { get; set; }
    [MaxLength(120)] public string CreatedBy { get; set; } = "";
    public bool IssuerApproved { get; set; }
    public bool SourceApproved { get; set; }
    public bool Enabled { get; set; } = true;
}
public sealed class CorrespondentFundingRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RuleId { get; set; }
    public DateTimeOffset ScheduledFor { get; set; }
    public decimal Amount { get; set; }
    [MaxLength(200)] public string Outcome { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
public static class CorrespondentFundingPolicy
{
    public static decimal Calculate(CorrespondentFundingStrategy strategy, decimal sourceAvailable, decimal destinationAvailable,
        decimal threshold, decimal target, decimal maximum) => decimal.Round(Math.Max(0, Math.Min(sourceAvailable,
            Math.Min(maximum, strategy switch {
                CorrespondentFundingStrategy.Fixed => maximum,
                CorrespondentFundingStrategy.Threshold when destinationAvailable < threshold => target - destinationAvailable,
                CorrespondentFundingStrategy.Sweep => sourceAvailable - target,
                CorrespondentFundingStrategy.ReplenishToMinimum when destinationAvailable < threshold => threshold - destinationAvailable,
                _ => 0
            }))), 4, MidpointRounding.ToZero);
    // Coalesce missed weekdays into one evaluation; never replay missed transfers on clock jumps.
    public static DateTimeOffset NextWeekday(DateTimeOffset now)
    {
        var next = now.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) next = next.AddDays(1);
        return next;
    }
}

public sealed class CorrespondentAuthorizationEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Guid SubjectId { get; set; }
    public Guid BankId { get; set; }
    [MaxLength(120)] public string Actor { get; set; } = "";
    [MaxLength(40)] public string Action { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}
