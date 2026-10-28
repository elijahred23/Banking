using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Banking.Domain;

public sealed class StudioScenario
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    [MaxLength(8)] public string Kind { get; set; } = "Wire";
    [MaxLength(100)] public string Title { get; set; } = "";
    [MaxLength(4000)] public string PlanJson { get; set; } = "";
    [MaxLength(120)] public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class StudioCampaign
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    [MaxLength(8)] public string Kind { get; set; } = "Wire";
    [MaxLength(100)] public string Title { get; set; } = "";
    [MaxLength(4000)] public string PlanJson { get; set; } = "";
    public int PlannedDays { get; set; } = 3;
    public DateOnly FirstDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed record StudioCampaignPlan(int[] Counts, int Rate, string ReceiverProfile,
    bool RequireDebitAuthorization, int IncidentPhase, string Incident, int MaxOutstanding, string Hint)
{
    public static StudioCampaignPlan Default(string kind) => new(kind == "Wire" ? [6, 4, 3] : [2, 2, 2], kind == "Wire" ? 6 : 2, "Normal", false, -1, "None", 0, "Trace acceptance, settlement, and balanced journals separately.");
    public string Serialize() => JsonSerializer.Serialize(this);
    public static StudioCampaignPlan Parse(string json, string kind)
    {
        StudioCampaignPlan plan;
        try { plan = JsonSerializer.Deserialize<StudioCampaignPlan>(json) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("The scenario plan is not valid JSON."); }
        if (!StudioDayPolicy.ValidKind(kind) || plan.Counts is not { Length: 3 } || plan.Counts.Any(x => x < 1 || x > (kind == "Wire" ? 20 : 5))
            || plan.Rate < 1 || plan.Rate > (kind == "Wire" ? 12 : 4) || plan.ReceiverProfile is not ("Normal" or "Slow" or "AccountProblem" or "Mixed")
            || plan.IncidentPhase is < -1 or > 2 || plan.MaxOutstanding is < 0 or > 100 || plan.Hint is null || plan.Hint.Length > 500
            || plan.IncidentPhase >= 0 && StudioDayPolicy.Incident(kind, plan.Incident) is null)
            throw new InvalidOperationException("Use three bounded workload counts, a supported pace/profile, a valid incident phase, and a hint up to 500 characters.");
        return plan;
    }
    public string ProfileFor(Guid bankId) => ReceiverProfile != "Mixed" ? ReceiverProfile : (bankId.ToByteArray()[0] % 3) switch { 0 => "Normal", 1 => "Slow", _ => "AccountProblem" };
    public ProcessingScenario WireScenario(Guid bankId) => ProfileFor(bankId) switch { "Slow" => ProcessingScenario.PendingThenAccepted, "AccountProblem" => ProcessingScenario.FedRejects, _ => ProcessingScenario.Standard };
    public AchProcessingScenario AchScenario(Guid bankId) => ProfileFor(bankId) == "AccountProblem" ? AchProcessingScenario.AccountClosed : AchProcessingScenario.Standard;
    public int ResponseSeconds(Guid bankId) => ProfileFor(bankId) == "Slow" ? 60 : 10;
}

public sealed class StudioDebitAuthorization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    public Guid OriginatingAccountId { get; set; }
    [MaxLength(9)] public string Routing { get; set; } = "";
    [MaxLength(17)] public string Account { get; set; } = "";
    public decimal Limit { get; set; }
    public DateOnly ValidThrough { get; set; }
    [MaxLength(120)] public string GrantedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
    public bool Covers(AchEntry entry, DateOnly date) => RevokedAt is null && ValidThrough >= date && entry.OriginatingBankId == BankId
        && entry.OriginatingAccountId == OriginatingAccountId && entry.ReceivingRoutingNumber == Routing && entry.ReceivingAccountNumber == Account
        && !AchRules.IsCredit(entry.TransactionCode) && entry.Amount <= Limit;
}
public sealed class StudioAchRepair
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    public Guid OriginalEntryId { get; set; }
    public Guid ReplacementEntryId { get; set; }
    [MaxLength(500)] public string Reason { get; set; } = "";
    [MaxLength(120)] public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public static class StudioRepairPolicy
{
    public static AchEntry Replacement(AchEntry original, string routing, string account, DateOnly date, string actor)
    {
        if (original.Status != AchEntryStatus.Rejected || original.Batch?.File?.Status != "Rejected")
            throw new InvalidOperationException("Only entries in a rejected operator file may be repaired here. Returns require their own workflow.");
        return new AchEntry { OriginatingBankId = original.OriginatingBankId, OriginatingAccountId = original.OriginatingAccountId,
            CompanyName = original.CompanyName, CompanyId = original.CompanyId, SecCode = original.SecCode, ReceiverName = original.ReceiverName,
            ReceivingRoutingNumber = routing, ReceivingAccountNumber = account, Amount = original.Amount, TransactionCode = original.TransactionCode,
            EntryDescription = original.EntryDescription, EffectiveEntryDate = date, Addenda05 = original.Addenda05, Purpose = original.Purpose,
            Scenario = AchProcessingScenario.Standard, Status = AchEntryStatus.PendingApproval, CreatedBy = actor };
    }
}
public sealed class StudioNote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? WireId { get; set; }
    [MaxLength(24)] public string Kind { get; set; } = "Handoff";
    [MaxLength(40)] public string RecipientRole { get; set; } = "Operations";
    [MaxLength(120)] public string Actor { get; set; } = "";
    [MaxLength(1000)] public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset AvailableAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class StudioFundingRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    public decimal Amount { get; set; }
    [MaxLength(500)] public string Reason { get; set; } = "";
    [MaxLength(120)] public string RequestedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? IncomingWireId { get; set; }
    public DateTimeOffset? ReconciledAt { get; set; }
    public string Reference => "FUND-" + Id.ToString("N")[..30];
}
