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
    [DataType(DataType.Date)] public DateOnly EffectiveEntryDate { get; set; } = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
    [StringLength(80)] public string? Addenda05 { get; set; }
    public AchPaymentPurpose Purpose { get; set; }
    public AchProcessingScenario Scenario { get; set; }
    public List<SelectListItem> Accounts { get; set; } = [];
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
    [DataType(DataType.Date)] public DateOnly SettlementDate { get; set; } = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
    public AchProcessingScenario Scenario { get; set; }
    public List<SelectListItem> Accounts { get; set; } = [];
}

public sealed record AchIndexViewModel(Bank Bank, IReadOnlyList<AchEntry> Entries);
public sealed record AchBatchIndexViewModel(Bank Bank, IReadOnlyList<AchBatch> Batches);
public sealed record AchFileIndexViewModel(Bank Bank, IReadOnlyList<AchFile> Files);
public sealed record AchExceptionIndexViewModel(Bank Bank, IReadOnlyList<AchReturn> Returns,
    IReadOnlyList<AchNotificationOfChange> Notifications);

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
