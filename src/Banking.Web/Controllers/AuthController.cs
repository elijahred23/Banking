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
        var roleKey = model.Role?.Trim().ToLowerInvariant() ?? "";
        var role = ResolveRole(roleKey);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bank = await db.Banks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == model.BankId, token);
        if (role is null || bank is null
            || !string.Equals(password, expectedPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError("", "Invalid lab credentials.");
            return View(await PopulateAsync(model, db, token));
        }
        var principal = CreatePrincipal(roleKey, role, bank.Id, bank.Name, bank.RoutingNumber);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return LocalRedirect(Url.IsLocalUrl(model.ReturnUrl) ? model.ReturnUrl! : "/");
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Switch(Guid bankId, string? role, string? returnUrl = null,
        CancellationToken token = default)
    {
        var roleKey = role?.Trim().ToLowerInvariant() ?? "";
        var roleName = ResolveRole(roleKey);
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bank = await db.Banks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == bankId, token);
        if (roleName is null || bank is null)
        {
            TempData["Error"] = "The selected lab bank or user is no longer available.";
            return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
        }

        var principal = CreatePrincipal(roleKey, roleName, bank.Id, bank.Name, bank.RoutingNumber);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        TempData["Notice"] = $"Switched to {RoleLabel(roleKey)} at {bank.Name}.";
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    [Authorize, HttpGet("/auth/access-denied")]
    public IActionResult AccessDenied(string? returnUrl = null)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        ViewData["ReturnUrl"] = returnUrl;
        return View();
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

    private static string? ResolveRole(string roleKey) => roleKey switch
    {
        "maker" => BankSecurity.PaymentCreator,
        "approver" => BankSecurity.PaymentApprover,
        "compliance" => BankSecurity.ComplianceOfficer,
        "operations" => BankSecurity.Operations,
        _ => null
    };

    private static string RoleLabel(string roleKey) => roleKey switch
    {
        "maker" => "Maker",
        "approver" => "Approver",
        "compliance" => "Compliance",
        "operations" => "Operations",
        _ => roleKey
    };

    private static ClaimsPrincipal CreatePrincipal(string roleKey, string role, Guid bankId,
        string bankName, string routingNumber) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, $"{roleKey}@{routingNumber}"),
            new Claim(ClaimTypes.Role, role),
            new Claim(BankSecurity.BankIdClaim, bankId.ToString()),
            new Claim("bank_name", bankName)],
        CookieAuthenticationDefaults.AuthenticationScheme));
}
