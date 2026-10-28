using System.Data;
using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize(Roles = BankSecurity.Operations)]
public sealed class AchSimulatorController(IDbContextFactory<BankingDbContext> factory,
    SimulationStateService simulation, StudioDayDashboardService dashboard) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken token) => View(await SnapshotAsync(token));

    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Snapshot(CancellationToken token) => PartialView("_Snapshot", await SnapshotAsync(token));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Control(bool running, AchTrafficMode mode = AchTrafficMode.Payroll,
        int rate = 2, CancellationToken token = default)
    {
        if (!Enum.IsDefined(mode) || rate is < 1 or > 4) return BadRequest("Choose a supported activity and 1–4 runs per minute.");
        await using var probe = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, probe, token);
        string? dayError = null;
        await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var activeDay = await db.StudioDays.SingleOrDefaultAsync(x => x.BankId == bankId && x.Kind == "Ach" && x.EndedAt == null, token);
            if (running && activeDay?.Phase == 3) { dayError = "This day is in reconciliation. Finish it before starting another day."; return; }
            if (activeDay is not null) mode = StudioDayPolicy.Phases[activeDay.Phase].Activity;

            var prefix = AchTraffic.BankPrefix(bankId);
            var states = await db.SimulationStates.Where(x => x.Name.StartsWith(prefix)).ToListAsync(token);
            var now = DateTimeOffset.UtcNow;
            foreach (var old in states) { old.IsPaused = true; old.UpdatedDate = now; }
            if (running)
            {
                var name = AchTraffic.StateName(bankId, mode);
                var state = states.SingleOrDefault(x => x.Name == name);
                if (state is null) { state = new SimulationState { Name = name }; db.SimulationStates.Add(state); }
                state.IsPaused = false;
                state.SpeedMultiplier = rate;
                state.CurrentUtc = now;
                state.UpdatedDate = now;
            }
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });
        if (dayError is not null) return BadRequest(dayError);
        TempData["Notice"] = running
            ? $"{AchTraffic.Title(mode)} enabled for this bank. Generated entries use separate simulated maker and approver identities; utility mandates are fictional lab authorizations."
            : "ACH generation and simulated approvals paused for this bank. Released entries and scheduled files continue normally.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<AchTrafficDashboardViewModel> SnapshotAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var prefix = AchTraffic.BankPrefix(bankId);
        var state = await db.SimulationStates.AsNoTracking().Where(x => x.Name.StartsWith(prefix))
            .OrderBy(x => x.IsPaused).ThenByDescending(x => x.UpdatedDate).FirstOrDefaultAsync(token);
        var entries = await db.AchEntries.AsNoTracking().Include(x => x.Batch).ThenInclude(x => x!.File)
            .Include(x => x.OriginatingBank).Include(x => x.ReceivingBank)
            .Where(x => x.CreatedBy == AchTraffic.Maker && (x.OriginatingBankId == bankId
                || x.ReceivingBankId == bankId && (x.Status == AchEntryStatus.SentToOperator
                    || x.Status == AchEntryStatus.Settled || x.Status == AchEntryStatus.Posted
                    || x.Status == AchEntryStatus.Returned || x.Status == AchEntryStatus.NocReceived)))
            .OrderByDescending(x => x.CreatedDate).ThenBy(x => x.Id).Take(80).ToListAsync(token);
        var ids = entries.Select(x => x.Id).ToList();
        var postings = await db.AchLedgerEntries.AsNoTracking().Where(x => ids.Contains(x.AchEntryId))
            .GroupBy(x => x.AchEntryId).Select(x => new { Id = x.Key, Debits = x.Sum(y => y.Debit),
                Credits = x.Sum(y => y.Credit), Date = x.Max(y => y.CreatedDate) }).ToListAsync(token);
        var accounts = await db.Accounts.AsNoTracking().Include(x => x.Customer)
            .Where(x => x.Customer.BankId == bankId).OrderBy(x => x.Customer.Name).ThenBy(x => x.AccountNumber).ToListAsync(token);
        return new(bank, state, accounts, entries, postings.ToDictionary(x => x.Id,
            x => new AchTrafficPosting(x.Debits, x.Credits, x.Date)),
            await simulation.UtcNowAsync(token), DateTimeOffset.UtcNow) { DayPanel = await dashboard.ReadAsync(bankId, "Ach", token) };
    }
}
