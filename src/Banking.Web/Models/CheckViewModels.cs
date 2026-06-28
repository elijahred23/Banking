using System.ComponentModel.DataAnnotations;
using Banking.Domain;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Banking.Web.Models;

public sealed record CheckIndexViewModel(Bank Bank, IReadOnlyList<CheckDeposit> Deposits);
public sealed record CheckDetailsViewModel(Bank Bank, CheckDeposit Deposit);
public sealed record CheckInstructionsViewModel(Bank Bank,
    IReadOnlyList<CheckInstructionAccountViewModel> DepositingAccounts,
    IReadOnlyList<CheckInstructionDestinationViewModel> PayingAccounts);
public sealed record CheckInstructionAccountViewModel(string CustomerName,
    string AccountNumber, decimal AvailableBalance);
public sealed record CheckInstructionDestinationViewModel(string BankName,
    string CustomerName, string RoutingNumber, string AccountNumber);

public sealed class CheckTiffGeneratorViewModel
{
    [Required, RegularExpression(@"^\d{9}$"), Display(Name = "Paying routing number")]
    public string RoutingNumber { get; set; } = "103000648";
    [Required, StringLength(34), Display(Name = "Paying account number")]
    public string AccountNumber { get; set; } = "654321";
    [Required, StringLength(15), Display(Name = "Check number")]
    public string CheckNumber { get; set; } = "1001";
    [Range(typeof(decimal), "0.01", "9999999")]
    public decimal Amount { get; set; } = 125.50m;
}

public sealed class CreateCheckDepositViewModel
{
    [Required, Display(Name = "Depositing account")]
    public Guid DepositingAccountId { get; set; }
    [Required, MaxLength(120), Display(Name = "Depositor name")]
    public string DepositorName { get; set; } = "";
    [Required, MaxLength(80), Display(Name = "MICR line")]
    public string RawMicrLine { get; set; } = "";
    [Range(typeof(decimal), "0.01", "9999999")]
    public decimal Amount { get; set; }
    public CheckProcessingScenario Scenario { get; set; } = CheckProcessingScenario.Standard;
    [Required, Display(Name = "Front TIFF image")]
    public IFormFile? FrontImage { get; set; }
    [Required, Display(Name = "Back TIFF image")]
    public IFormFile? BackImage { get; set; }
    public List<SelectListItem> Accounts { get; set; } = [];
}
