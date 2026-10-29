using System.ComponentModel.DataAnnotations;
using Banking.Domain;

namespace Banking.Web.Models;

public sealed class ReportTaskForm
{
    public Guid? Id { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [EnumDataType(typeof(BankReportKind))] public BankReportKind Kind { get; set; }
    [EnumDataType(typeof(ReportFrequency))] public ReportFrequency Frequency { get; set; } = ReportFrequency.Daily;
    [DataType(DataType.Time)] public TimeOnly DailyAtUtc { get; set; } = new(6, 0);
    [Range(1, 744)] public int LookbackHours { get; set; } = 24;
    public bool Enabled { get; set; }
}
public sealed record ReportLibraryViewModel(Bank Bank, IReadOnlyList<Bank> Banks,
    IReadOnlyList<BankReportTask> Tasks, IReadOnlyList<BankReportRun> Runs,
    BankReportKind? Kind, DateOnly From, DateOnly To, int Page, int Total);
public sealed record SavedReportViewModel(BankReportRun Run, BankReportDocument? Report, string BankName);
