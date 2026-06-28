using Banking.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize]
public sealed class BankContextController(IDbContextFactory<BankingDbContext> dbFactory) : Controller
{
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Switch(Guid bankId, string? returnUrl, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        if (await db.Banks.AnyAsync(x => x.Id == bankId, token)) ActiveBank.Set(HttpContext, bankId);
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }
}
