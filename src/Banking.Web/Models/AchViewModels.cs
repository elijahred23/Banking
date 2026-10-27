using System.ComponentModel.DataAnnotations;
using Banking.Domain;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Banking.Web.Models;

public sealed class CreateAchViewModel
{
    [Required] public Guid OriginatingAccountId { get; set; }
    [Required, StringLength(16)] public string CompanyName { get; set; } = "";
    [Required, StringLength(10)] public string CompanyId { get; set; } = "";
    public AchStandardEntryClass SecCode { get; set; } = AchStandardEntryClass.Ppd;
    [Required, StringLength(22)] public string ReceiverName { get; set; } = "";
    [Required, RegularExpression(@"^\d{9}$")] public string ReceivingRoutingNumber { get; set; } = "";
    [Required, StringLength(17)] public string ReceivingAccountNumber { get; set; } = "";
    public AchTransactionCode TransactionCode { get; set; } = AchTransactionCode.CheckingCredit;
    [Range(typeof(decimal), "0.01", "999999999.99")] public decimal Amount { get; set; }
    [Required, StringLength(10)] public string EntryDescription { get; set; } = "PAYMENT";
    [DataType(DataType.Date)] public DateOnly EffectiveEntryDate { get; set; } = AchBusinessCalendar.NextBankingDay(DateOnly.FromDateTime(DateTime.Today));
    [StringLength(80)] public string? Addenda05 { get; set; }
    public AchPaymentPurpose Purpose { get; set; }
    [Display(Name = "Exercise mode")] public AchLearningCaseType CaseType { get; set; }
    public List<SelectListItem> Accounts { get; set; } = [];
    public Dictionary<string, AchFieldChoices> Choices { get; set; } = [];
}

public sealed class CreateEftpsViewModel
{
    [Required] public Guid OriginatingAccountId { get; set; }
    [Required, StringLength(16)] public string CompanyName { get; set; } = "";
    [Required, StringLength(10)] public string CompanyId { get; set; } = "";
    [Required] public string TaxpayerIdentificationNumber { get; set; } = "";
    [Required] public string TaxTypeCode { get; set; } = "";
    [Required, RegularExpression(@"^\d{6}$", ErrorMessage = "Use YYMMDD.")] public string TaxPeriodEndDate { get; set; } = "";
    [Range(typeof(decimal), "0.01", "999999999.99")] public decimal Amount { get; set; }
    [DataType(DataType.Date)] public DateOnly SettlementDate { get; set; } = AchBusinessCalendar.NextBankingDay(DateOnly.FromDateTime(DateTime.Today));
    [Display(Name = "Exercise mode")] public AchLearningCaseType CaseType { get; set; }
    public List<SelectListItem> Accounts { get; set; } = [];
    public Dictionary<string, AchFieldChoices> Choices { get; set; } = [];
}

public sealed record AchValueOption(
    string Label,
    string? Detail,
    IReadOnlyDictionary<string, string> Values);

public sealed record AchFieldChoices(
    string Title,
    string Guidance,
    IReadOnlyList<AchValueOption> Options);

public sealed record AchValuePickerViewModel(string FieldId, AchFieldChoices Choices);

public sealed record AchIndexViewModel(Bank Bank, IReadOnlyList<AchEntry> Entries);
public sealed record AchBatchIndexViewModel(Bank Bank, IReadOnlyList<AchBatch> Batches);
public sealed record AchFileIndexViewModel(Bank Bank, IReadOnlyList<AchFile> Files);
public sealed record AchExceptionIndexViewModel(Bank Bank, IReadOnlyList<AchReturn> Returns,
    IReadOnlyList<AchNotificationOfChange> Notifications);

public sealed record AchLifecycleStepViewModel(string Stage, string Actor, string Evidence,
    string ValueEffect, DateTimeOffset? OccurredDate, bool Complete);

public sealed record AchBalanceViewModel(string Name, decimal Ledger, decimal Held, decimal Available);

public sealed record AchExposureViewModel(decimal Current, decimal Proposed, decimal Limit)
{
    public bool WithinLimit => Proposed <= Limit;
}

public sealed record AchDetailsViewModel(Bank Bank, AchEntry Entry, AchBatch? Batch, AchFile? File,
    IReadOnlyList<AchReturn> Returns, IReadOnlyList<AchNotificationOfChange> Notifications,
    IReadOnlyList<AchLedgerEntry> Journal, IReadOnlyList<AchLifecycleStepViewModel> Lifecycle,
    IReadOnlyList<AchBalanceViewModel> Balances, AchExposureViewModel Exposure,
    AchLearningDecision Decision);

public sealed class AchNachaSandboxViewModel
{
    public Guid? FileId { get; set; }
    public Guid? DraftBatchId { get; set; }
    public DateOnly? EffectiveEntryDate { get; set; }
    public string? FileStatus { get; set; }
    [Required] public string Payload { get; set; } = "";
    [Display(Name = "Predict the result")] public string Prediction { get; set; } = "Accepted";
    public NachaValidationResult? Result { get; set; }
    public bool? PredictionCorrect { get; set; }
    public IReadOnlyList<NachaRecordAnnotation> Records { get; set; } = [];
}

public sealed record AchWindowsViewModel(DateTimeOffset SimulatedNow,
    IReadOnlyList<AchProcessingWindow> Windows, string NextAction, DateOnly NextSettlementDate);

public sealed record AchOperationsCaseViewModel(AchEntry Entry, string Evidence,
    AchLearningDecision Expected, string? SelectedAction, bool? Correct);

public sealed record AchOperationsViewModel(Bank Bank, IReadOnlyList<AchOperationsCaseViewModel> Cases);

public sealed record AchInstructionsViewModel(
    Bank Bank,
    IReadOnlyList<AchInstructionAccountViewModel> SourceAccounts,
    IReadOnlyList<AchInstructionDestinationViewModel> Destinations);

public sealed record AchInstructionAccountViewModel(
    string CustomerName,
    string AccountNumber,
    decimal AvailableBalance);

public sealed record AchInstructionDestinationViewModel(
    string BankName,
    string CustomerName,
    string AccountNumber,
    string RoutingNumber);
