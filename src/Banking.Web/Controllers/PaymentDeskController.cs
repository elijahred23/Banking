using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize(Roles = BankSecurity.Operations)]
public sealed class PaymentDeskController(IDbContextFactory<BankingDbContext> factory,
    SimulationStateService simulation, PaymentReconciliationService reconciliation,
    UnifiedJournalService journals) : Controller
{
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(CancellationToken token) => View(await ReadAsync(token));

    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Evidence(CancellationToken token)
    {
        var model = await ReadAsync(token);
        return File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            Scope = "Bank-scoped learning evidence; not an independent operator statement or reconciliation certificate",
            Bank = model.Bank.Name, CapturedAt = DateTimeOffset.UtcNow, LabTime = model.Clock.CurrentUtc,
            SnapshotLimit = "Latest 100 wires and 100 ACH entries; journal controls cover all visible ACH and wire journals",
            Payments = model.Items, model.Reconciliation, model.Journals,
            model.WireDemand, model.AchCreditExposure
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
            "application/json", "payment-desk-evidence.json");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RecordReview(string? explanation, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(explanation) || explanation.Trim().Length < 30 || explanation.Length > 1500)
        {
            TempData["Error"] = "Describe the evidence checked and the next action in 30–1500 characters.";
            return RedirectToAction(nameof(Index));
        }
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var result = await reconciliation.ReconcileAsync(bankId, token);
        var now = DateTimeOffset.UtcNow;
        var summary = $"Outbox {result.PendingOutboxMessages}; inbox {result.IncompleteInboxMessages}; dead letters {result.DeadLetterMessages}; unbalanced {result.UnbalancedJournals}; holds {result.HoldMismatches}; missing wire/ACH journals {result.SettledWiresWithoutJournals}/{result.SettledAchWithoutJournals}.";
        db.OperationsCases.Add(new OperationsCase
        {
            BankId = bankId, Rail = "Cross-rail", CaseType = "Reconciliation review",
            Reference = $"desk-{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..50],
            Summary = summary, Notes = $"Evidence snapshot {now:u}. {summary}\nReviewer explanation: {explanation.Trim()}",
            Status = OperationsCaseStatus.Investigating, Severity = result.IsClear ? OperationsCaseSeverity.Low : OperationsCaseSeverity.High,
            AssignedTo = User.Identity?.Name, DueDate = now.AddHours(result.IsClear ? 8 : 1)
        });
        await db.SaveChangesAsync(token);
        TempData["Notice"] = "Evidence review saved to the Operations case queue. A human must assess and close it; this does not certify reconciliation or complete a mission.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<PaymentDeskViewModel> ReadAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var clock = await simulation.GetAsync(token);
        var date = DateOnly.FromDateTime(clock.CurrentUtc.UtcDateTime);
        var wires = await db.WireTransfers.AsNoTracking().Where(x => x.BankId == bankId)
            .OrderByDescending(x => x.CreatedDate).Take(100).ToListAsync(token);
        var ach = await db.AchEntries.AsNoTracking().Where(x => x.OriginatingBankId == bankId
            || x.ReceivingBankId == bankId && (x.Status == AchEntryStatus.SentToOperator
                || x.Status == AchEntryStatus.Settled || x.Status == AchEntryStatus.Posted
                || x.Status == AchEntryStatus.Returned || x.Status == AchEntryStatus.NocReceived))
            .OrderByDescending(x => x.CreatedDate).Take(100).ToListAsync(token);
        var futureDates = await db.AchEntries.AsNoTracking().Where(x => x.OriginatingBankId == bankId
                && x.Status == AchEntryStatus.Batched && x.EffectiveEntryDate > date)
            .Select(x => x.EffectiveEntryDate).Distinct().ToListAsync(token);
        var items = wires.Select(x => new PaymentDeskItem(x.Id, x.Rail.ToString(),
            $"{x.SenderName} → {x.ReceiverName}", x.Amount, x.Status.ToString(), "Wires",
            PaymentAttentionPolicy.For(x), x.CreatedDate))
            .Concat(ach.Select(x => new PaymentDeskItem(x.Id, "ACH", $"{x.CompanyName} → {x.ReceiverName}",
                x.Amount, x.Status.ToString(), "Ach", PaymentAttentionPolicy.For(x, date), x.CreatedDate)))
            .OrderBy(x => x.Attention.Category == "action" ? 0 : x.Attention.Category == "review" ? 1 : x.Attention.Category == "waiting" ? 2 : 3)
            .ThenByDescending(x => x.CreatedAt).ToList();
        var lines = await journals.ReadAsync(bankId, token);
        var control = lines.Where(x => x.Rail != PaymentRail.Check).GroupBy(x => new { x.PaymentId, x.JournalId, x.Rail })
            .Select(x => new DeskJournal(x.Key.PaymentId, x.Key.Rail == PaymentRail.Ach ? "Ach" : "Wires",
                x.Key.Rail.ToString(), x.Key.JournalId, x.Sum(y => y.Debit), x.Sum(y => y.Credit), x.Count()))
            .OrderBy(x => x.Balanced).ThenBy(x => x.Rail).ToList();
        var wireDemand = await db.WireTransfers.Where(x => x.BankId == bankId
                && x.Direction == WireDirection.Outgoing && x.Rail == PaymentRail.Fedwire
                && (x.Status == WireStatus.PendingApproval || x.Status == WireStatus.Created
                    || x.Status == WireStatus.Validated || x.Status == WireStatus.ReadyForFed
                    || x.Status == WireStatus.SentToFed || x.Status == WireStatus.PendingAtFed))
            .SumAsync(x => (decimal?)x.Amount, token) ?? 0;
        var exposure = await db.AchEntries.Where(x => x.OriginatingBankId == bankId
                && (x.TransactionCode == AchTransactionCode.CheckingCredit || x.TransactionCode == AchTransactionCode.SavingsCredit)
                && (x.Status == AchEntryStatus.Validated || x.Status == AchEntryStatus.Batched || x.Status == AchEntryStatus.SentToOperator))
            .SumAsync(x => (decimal?)x.Amount, token) ?? 0;
        return new PaymentDeskViewModel(bank, clock, LabClockPolicy.Next(clock.CurrentUtc, futureDates),
            items, await reconciliation.ReconcileAsync(bankId, token), control, wireDemand, exposure);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> NextEvent(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        // Recompute on the server; clients cannot choose another bank's event or rewind time.
        var state = await simulation.AdvanceToNextAchDateAsync(bankId, token);
        TempData["Notice"] = $"Shared lab clock advanced to {state.CurrentUtc:u}. This affects all banks. Eligible ACH work proceeds when its workers run; wire transport and generator pace still use wall time.";
        return RedirectToAction(nameof(Index));
    }
}
