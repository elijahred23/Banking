using System.ComponentModel.DataAnnotations;
using Banking.Domain;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Banking.Web.Models;

public sealed record CheckIndexViewModel(Bank Bank, IReadOnlyList<CheckDeposit> Deposits);
public sealed record CheckDetailsViewModel(Bank Bank, CheckDeposit Deposit);

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
