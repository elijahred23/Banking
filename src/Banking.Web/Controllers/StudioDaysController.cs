using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize(Roles = BankSecurity.Operations)]
public sealed class StudioDaysController(IDbContextFactory<BankingDbContext> factory, StudioDayService days) : Controller
{
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(string kind, bool useRoster, CancellationToken token)
    {
        if (!StudioDayPolicy.ValidKind(kind)) return BadRequest();
        return await Run(kind, async bankId => await days.StartAsync(bankId, kind, User.Identity?.Name ?? "Operations", useRoster, token), token);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Advance(Guid id, int phase, bool close, CancellationToken token)
    {
        var (bankId, kind) = await Resolve(id, token);
        if (kind is null) return NotFound();
        return await Run(kind, _ => days.AdvanceAsync(bankId, id, phase, close, token), token);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Incident(Guid id, string incident, CancellationToken token)
    {
        var (bankId, kind) = await Resolve(id, token);
        if (kind is null) return NotFound();
        return await Run(kind, _ => days.QueueIncidentAsync(bankId, id, incident, token), token);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewCase(Guid id, string notes, bool resolve, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var item = await db.OperationsCases.SingleOrDefaultAsync(x => x.BankId == bankId && x.Id == id && (x.Rail == "Wire" || x.Rail == "Ach"), token);
        if (item is null) return NotFound();
        try { await days.ReviewCaseAsync(bankId, id, User.Identity?.Name ?? "Operations", notes, resolve, token); }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; return Back(item.Rail); }
        TempData["Notice"] = "Investigation recorded. Case closure does not change payment, compliance, or accounting state.";
        return Back(item.Rail);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AnswerInquiry(Guid id, Guid paymentId, string outcome, string explanation, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var day = await db.StudioDays.SingleOrDefaultAsync(x => x.BankId == bankId && x.Id == id, token);
        if (day is null) return NotFound();
        string expected, status;
        if (day.Kind == "Wire")
        {
            var wire = await db.WireTransfers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == paymentId && x.BankId == bankId
                && x.Direction == WireDirection.Outgoing && x.CreatedBy == WireTraffic.Maker && x.CreatedDate >= day.StartedAt
                && (day.EndedAt == null || x.CreatedDate <= day.EndedAt), token);
            if (wire is null) return NotFound();
            expected = StudioDayPolicy.CustomerOutcome(wire.Status); status = wire.Status.ToString();
        }
        else
        {
            var entry = await db.AchEntries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == paymentId && x.OriginatingBankId == bankId
                && x.CreatedBy == AchTraffic.Maker && x.CreatedDate >= day.StartedAt && (day.EndedAt == null || x.CreatedDate <= day.EndedAt), token);
            if (entry is null) return NotFound();
            expected = StudioDayPolicy.CustomerOutcome(entry.Status); status = entry.Status.ToString();
        }
        if (explanation?.Trim().Length is not (>= 12 and <= 500))
        { TempData["Error"] = "Include 12–500 characters describing the payment evidence you inspected."; return Back(day.Kind); }
        if (outcome is not ("Processing" or "Settled" or "Rejected" or "Returned" or "Correction")) return BadRequest("Choose a listed status category.");
        var correct = outcome == expected;
        db.OperationsCases.Add(new OperationsCase { BankId = bankId, Rail = day.Kind, CaseType = "StudioCustomerInquiry",
            Reference = paymentId.ToString(), Summary = "SIMULATED customer inquiry: where is my payment?",
            AssignedTo = User.Identity?.Name, DueDate = DateTimeOffset.UtcNow,
            Notes = $"Observed state at assessment: {status}. Selected: {outcome}. Evidence: {explanation.Trim()}",
            Status = correct ? OperationsCaseStatus.Resolved : OperationsCaseStatus.Investigating,
            Resolution = correct ? $"Status category matched recorded state {status}. Narrative retained for human review; no automatic assessment of its accuracy." : null,
            ResolvedDate = correct ? DateTimeOffset.UtcNow : null });
        await db.SaveChangesAsync(token);
        TempData[correct ? "Notice" : "Error"] = correct
            ? $"Your status category matches {status}. Your explanation is saved for review. This is a simulated inquiry; no customer message was sent."
            : $"At assessment the recorded state was {status}, which maps to {expected}. Inspect its current evidence and revise your explanation.";
        return Back(day.Kind);
    }

    private async Task<(Guid, string?)> Resolve(Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var kind = await db.StudioDays.Where(x => x.BankId == bankId && x.Id == id).Select(x => x.Kind).SingleOrDefaultAsync(token);
        return (bankId, kind);
    }
    private async Task<IActionResult> Run(string kind, Func<Guid, Task> action, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        try { await action(bankId); TempData["Notice"] = "Banking-day controls updated. Inspect the live snapshot for generation and processing evidence."; }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
        return Back(kind);
    }
    private RedirectToActionResult Back(string kind) => RedirectToAction("Index", kind == "Ach" ? "AchSimulator" : "Simulator");
}
