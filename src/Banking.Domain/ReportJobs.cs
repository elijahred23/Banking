using System.ComponentModel.DataAnnotations;

namespace Banking.Domain;

public enum BankReportKind
{
    PaymentActivity, Reconciliation, Liquidity, Exceptions,
    AchBatchPreparation, AchCutoffProcessing, ReturnNocReview, StuckPayments,
    SettlementReconciliation, HoldReconciliation, LiquidityForecast, DuplicateScreening,
    DeliveryRecovery, ApprovalReminders, CaseEscalation, EndOfDayClose
}
public enum ReportFrequency { Every15Minutes, Hourly, Daily }
public enum ReportRunStatus { Queued, Running, Succeeded, Failed }

public sealed class BankReportTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    [MaxLength(100)] public required string Name { get; set; }
    public BankReportKind Kind { get; set; }
    public ReportFrequency Frequency { get; set; } = ReportFrequency.Daily;
    public TimeOnly DailyAtUtc { get; set; } = new(6, 0);
    public int LookbackHours { get; set; } = 24;
    public bool Enabled { get; set; }
    public DateTimeOffset NextRunAt { get; set; }
    [MaxLength(120)] public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BankReportRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    public Guid TaskId { get; set; }
    public BankReportTask Task { get; set; } = null!;
    [MaxLength(100)] public required string TaskName { get; set; }
    public BankReportKind Kind { get; set; }
    public int LookbackHours { get; set; }
    public ReportRunStatus Status { get; set; } = ReportRunStatus.Queued;
    [MaxLength(120)] public required string RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ScheduledFor { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public int Attempts { get; set; }
    [MaxLength(1000)] public string? Error { get; set; }
    public string? ReportJson { get; set; }
}

public static class ReportSchedulePolicy
{
    public static DateTimeOffset Next(ReportFrequency frequency, TimeOnly dailyAtUtc, DateTimeOffset now)
    {
        now = now.ToUniversalTime();
        return frequency switch
        {
            ReportFrequency.Every15Minutes => now.AddMinutes(15),
            ReportFrequency.Hourly => now.AddHours(1),
            ReportFrequency.Daily => Daily(dailyAtUtc, now),
            _ => throw new ArgumentOutOfRangeException(nameof(frequency))
        };
    }
    private static DateTimeOffset Daily(TimeOnly at, DateTimeOffset now)
    {
        var candidate = new DateTimeOffset(DateOnly.FromDateTime(now.UtcDateTime).ToDateTime(at), TimeSpan.Zero);
        return candidate > now ? candidate : candidate.AddDays(1);
    }
    public static string Title(BankReportKind kind) => kind switch
    {
        BankReportKind.PaymentActivity => "Payment activity",
        BankReportKind.Reconciliation => "Reconciliation controls",
        BankReportKind.Liquidity => "Liquidity & account balances",
        BankReportKind.Exceptions => "Exceptions & operations cases",
        BankReportKind.AchBatchPreparation => "ACH batch preparation",
        BankReportKind.AchCutoffProcessing => "ACH cutoff processing",
        BankReportKind.ReturnNocReview => "Return & NOC review",
        BankReportKind.StuckPayments => "Stuck-payment detection",
        BankReportKind.SettlementReconciliation => "Settlement reconciliation",
        BankReportKind.HoldReconciliation => "Hold reconciliation",
        BankReportKind.LiquidityForecast => "Liquidity forecasting",
        BankReportKind.DuplicateScreening => "Duplicate-payment screening",
        BankReportKind.DeliveryRecovery => "Delivery recovery",
        BankReportKind.ApprovalReminders => "Approval reminders",
        BankReportKind.CaseEscalation => "Operations case escalation",
        BankReportKind.EndOfDayClose => "End-of-day close review",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static bool IsOperational(BankReportKind kind) => kind >= BankReportKind.AchBatchPreparation && Enum.IsDefined(kind);
    public static string Description(BankReportKind kind) => kind switch
    {
        BankReportKind.PaymentActivity => "Capture payment volumes, face amounts, recorded states, and evidence.",
        BankReportKind.Reconciliation => "Capture journal, hold, and transport controls without changing records.",
        BankReportKind.Liquidity => "Capture bank cash, customer balances, available funds, and holds.",
        BankReportKind.Exceptions => "Capture payment exceptions and open operations cases.",
        BankReportKind.AchBatchPreparation => "Validate and batch already-released ACH entries; retain independent approval and funds/exposure checks.",
        BankReportKind.AchCutoffProcessing => "Close eligible ACH batches and queue saved NACHA files through the durable outbox. Uses the shared lab banking date.",
        BankReportKind.ReturnNocReview => "Create operations work items for recorded ACH returns and NOCs, preserving original and reversing evidence.",
        BankReportKind.StuckPayments => "Find wires idle for 15 minutes and ACH processing older than an hour; exclude future-dated batches. Open review cases.",
        BankReportKind.SettlementReconciliation => "Check final wire/ACH posting evidence and wire network settlement records; create cases for discrepancies.",
        BankReportKind.HoldReconciliation => "Compare customer holds with outstanding validated credit instructions and open discrepancy cases.",
        BankReportKind.LiquidityForecast => "Compare bank liquidity with outstanding outgoing Fedwire and ACH credit face amounts; create funding-gap cases.",
        BankReportKind.DuplicateScreening => "Flag similar originating instructions in the activity window for review. Does not reject or release payments.",
        BankReportKind.DeliveryRecovery => "Make eligible failed original outbox messages due for retry. Preserve payload, queue, attempts, and stable delivery ID. Dead letters require review.",
        BankReportKind.ApprovalReminders => "Create in-app Approver reminders for instructions awaiting independent approval for at least 15 minutes.",
        BankReportKind.CaseEscalation => "Raise overdue unresolved cases to High severity and flag unassigned cases without changing ownership, notes, or resolution.",
        BankReportKind.EndOfDayClose => "Save a closing review of unsettled work, controls, returns, funding, and cases. A person must assess and close the review.",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
    public static ReportFrequency DefaultFrequency(BankReportKind kind) => kind switch
    {
        BankReportKind.EndOfDayClose => ReportFrequency.Daily,
        BankReportKind.AchBatchPreparation or BankReportKind.AchCutoffProcessing or BankReportKind.StuckPayments
            or BankReportKind.LiquidityForecast or BankReportKind.ApprovalReminders or BankReportKind.DeliveryRecovery => ReportFrequency.Every15Minutes,
        _ => ReportFrequency.Hourly
    };
    public static string Cadence(ReportFrequency frequency, TimeOnly at) => frequency switch
    {
        ReportFrequency.Every15Minutes => "Every 15 minutes",
        ReportFrequency.Hourly => "Every hour",
        ReportFrequency.Daily => $"Daily at {at:HH:mm} UTC",
        _ => "Unknown schedule"
    };
}

public sealed record BankReportDocument(int Version, Guid BankId, string BankName, BankReportKind Kind,
    DateTimeOffset PeriodFrom, DateTimeOffset PeriodTo, DateTimeOffset CapturedAt,
    DateTimeOffset LabTime, string Scope, IReadOnlyList<BankReportMetric> Metrics,
    IReadOnlyList<BankReportSection> Sections);
public sealed record BankReportMetric(string Label, string Value, string Hint);
public sealed record BankReportSection(string Title, string Description, IReadOnlyList<BankReportRow> Rows);
public sealed record BankReportRow(string Reference, string Status, string Detail, decimal? Amount = null,
    Guid? ItemId = null, string? Controller = null);
