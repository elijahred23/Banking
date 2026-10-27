using System.Data;
using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize(Roles = BankSecurity.Operations)]
public sealed class SimulatorController(IDbContextFactory<BankingDbContext> factory) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken token) => View(await SnapshotAsync(token));

    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Snapshot(CancellationToken token) => PartialView("_Snapshot", await SnapshotAsync(token));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Control(bool running, int rate = 6, CancellationToken token = default)
    {
        if (rate is < 1 or > 12) return BadRequest("Choose 1–12 wires per minute.");
        await using var probe = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, probe, token);
        await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var name = WireTraffic.StateName(bankId);
            var state = await db.SimulationStates.SingleOrDefaultAsync(x => x.Name == name, token);
            if (state is null)
            {
                state = new SimulationState { Name = name };
                db.SimulationStates.Add(state);
            }
            state.IsPaused = !running;
            state.SpeedMultiplier = rate;
            state.CurrentUtc = DateTimeOffset.UtcNow;
            state.UpdatedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });
        TempData["Notice"] = running
            ? "Lab traffic enabled for this bank. Separate simulated maker and approver identities release generated wires through the normal payment pipeline."
            : "New traffic paused for this bank. Released payments continue processing; pending simulator approvals wait until resumed.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<TrafficDashboardViewModel> SnapshotAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var state = await db.SimulationStates.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Name == WireTraffic.StateName(bankId), token);
        var generated = db.WireTransfers.Where(x => x.CreatedBy == WireTraffic.Maker).Select(x => x.CorrelationId);
        var wires = await db.WireTransfers.AsNoTracking().Include(x => x.Events).AsSplitQuery()
            .Where(x => x.BankId == bankId && generated.Contains(x.CorrelationId))
            .OrderByDescending(x => x.CreatedDate).Take(40).ToListAsync(token);
        var accounts = await db.Accounts.AsNoTracking().Include(x => x.Customer)
            .Where(x => x.Customer.BankId == bankId).OrderBy(x => x.Customer.Name).ThenBy(x => x.AccountNumber)
            .ToListAsync(token);
        var ids = wires.SelectMany(x => new[] { x.SenderBankId, x.ReceiverBankId }).Append(bankId).Distinct().ToList();
        var names = await db.Banks.AsNoTracking().Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, token);
        return new(bank, state, accounts, wires, names, DateTimeOffset.UtcNow);
    }
}
