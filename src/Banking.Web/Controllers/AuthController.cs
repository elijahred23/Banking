using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Banking.Web.Controllers;

public sealed class AuthController(IConfiguration configuration) : Controller
{
    [AllowAnonymous, HttpGet]
    public IActionResult Login(string? returnUrl = null) => View(model: returnUrl);

    [AllowAnonymous, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string username, string password, string? returnUrl)
    {
        var expectedUser = configuration["LabUser:Username"] ?? "operator";
        var expectedPassword = configuration["LabUser:Password"] ?? "fedwire-lab";
        if (!string.Equals(username, expectedUser, StringComparison.Ordinal)
            || !string.Equals(password, expectedPassword, StringComparison.Ordinal))
        {
            ModelState.AddModelError("", "Invalid lab credentials.");
            return View(model: returnUrl);
        }
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, "WireOperator")],
            CookieAuthenticationDefaults.AuthenticationScheme));
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }

    [Authorize, HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }
}
