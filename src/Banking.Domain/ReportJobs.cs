using System.ComponentModel.DataAnnotations;

namespace Banking.Domain;

public enum BankReportKind { PaymentActivity, Reconciliation, Liquidity, Exceptions }
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
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
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
