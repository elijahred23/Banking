using Microsoft.AspNetCore.Mvc.Rendering;

namespace Banking.Web.Models;

public sealed class LoginViewModel
{
    public string? ReturnUrl { get; set; }
    public Guid BankId { get; set; }
    public string Role { get; set; } = "maker";
    public List<SelectListItem> Banks { get; set; } = [];
}
