using System.Security.Claims;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

public sealed class AuthController(IConfiguration configuration,
    IDbContextFactory<BankingDbContext> dbFactory) : Controller
{
    [AllowAnonymous, HttpGet]
    public async Task<IActionResult> Login(string? returnUrl = null, CancellationToken token = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        return View(await PopulateAsync(new LoginViewModel { ReturnUrl = returnUrl }, db, token));
    }

    [AllowAnonymous, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string password,
        CancellationToken token)
    {
        var expectedPassword = configuration["LabUser:Password"] ?? "fedwire-lab";
        var roleKey = model.Role.Trim().ToLowerInvariant();
        var role = roleKey switch
        {
            "maker" => BankSecurity.PaymentCreator,
            "approver" => BankSecurity.PaymentApprover,
            "compliance" => BankSecurity.ComplianceOfficer,
            "operations" => BankSecurity.Operations,
            _ => null
        };
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bank = await db.Banks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == model.BankId, token);
        if (role is null || bank is null
            || !string.Equals(password, expectedPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError("", "Invalid lab credentials.");
            return View(await PopulateAsync(model, db, token));
        }
        var username = $"{roleKey}@{bank.RoutingNumber}";
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, role),
                new Claim(BankSecurity.BankIdClaim, bank.Id.ToString()),
                new Claim("bank_name", bank.Name)],
            CookieAuthenticationDefaults.AuthenticationScheme));
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return LocalRedirect(Url.IsLocalUrl(model.ReturnUrl) ? model.ReturnUrl! : "/");
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    private static async Task<LoginViewModel> PopulateAsync(LoginViewModel model,
        BankingDbContext db, CancellationToken token)
    {
        var banks = await db.Banks.AsNoTracking().OrderBy(x => x.Name).ToListAsync(token);
        if (model.BankId == Guid.Empty)
            model.BankId = banks.FirstOrDefault(x => x.RoutingNumber == "101000019")?.Id
                ?? banks.First().Id;
        model.Banks = banks.Select(x => new Microsoft.AspNetCore.Mvc.Rendering.SelectListItem(
            $"{x.Name} · {x.RoutingNumber}", x.Id.ToString())).ToList();
        return model;
    }
}
