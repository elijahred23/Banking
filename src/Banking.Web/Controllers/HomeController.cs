using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize]
public sealed class HomeController(IDbContextFactory<BankingDbContext> dbFactory) : Controller
{
    public async Task<IActionResult> Index(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.SingleAsync(x => x.Id == bankId, token);
        var wires = await db.WireTransfers.Where(x => x.BankId == bankId)
            .OrderByDescending(x => x.CreatedDate).Take(8).AsNoTracking().ToListAsync(token);
        return View(new DashboardViewModel(bank, wires,
            wires.Count(x => x.Direction == Domain.WireDirection.Incoming),
            wires.Count(x => x.Direction == Domain.WireDirection.Outgoing),
            wires.Where(x => x.Direction == Domain.WireDirection.Outgoing).Sum(x => x.Amount)));
    }
}
