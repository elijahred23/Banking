using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize]
public sealed class StudioCampaignsController(IDbContextFactory<BankingDbContext> factory, StudioCampaignService campaigns, StudioDayService days) : Controller
{
    public async Task<IActionResult> Index(string kind = "Wire", Guid? id = null, CancellationToken token = default)
    {
        if (!StudioDayPolicy.ValidKind(kind)) return BadRequest();
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var all = await db.StudioCampaigns.AsNoTracking().Where(x => x.BankId == bankId && x.Kind == kind).OrderByDescending(x => x.CreatedAt).Take(20).ToListAsync(token);
        var selected = id is null ? all.FirstOrDefault() : await db.StudioCampaigns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.BankId == bankId && x.Kind == kind, token);
        if (id is not null && selected is null) return NotFound();
        var scenarios = await db.StudioScenarios.AsNoTracking().Where(x => x.BankId == bankId && x.Kind == kind).OrderByDescending(x => x.CreatedAt).Take(20).ToListAsync(token);
        var sessions = selected is null ? [] : await db.StudioDays.AsNoTracking().Where(x => x.BankId == bankId && x.CampaignId == selected.Id).OrderBy(x => x.StartedAt).ToListAsync(token);
        var roles = new[] { BankSecurity.PaymentCreator, BankSecurity.PaymentApprover, BankSecurity.ComplianceOfficer, BankSecurity.Operations }.Where(User.IsInRole).ToList();
        var notes = await db.StudioNotes.AsNoTracking().Where(x => x.BankId == bankId && x.AvailableAt <= DateTimeOffset.UtcNow
            && (x.Kind != "Handoff" || x.Actor == User.Identity!.Name || roles.Contains(x.RecipientRole))
            && (selected == null || x.CampaignId == selected.Id || x.CampaignId == null))
            .OrderByDescending(x => x.AvailableAt).Take(50).ToListAsync(token);
        var pending = await db.StudioNotes.CountAsync(x => x.BankId == bankId && x.AvailableAt > DateTimeOffset.UtcNow && (selected == null || x.CampaignId == selected.Id || x.CampaignId == null), token);
        var accounts = await db.Accounts.AsNoTracking().Include(x => x.Customer).Where(x => x.Customer.BankId == bankId).ToListAsync(token);
        var achPending = await db.AchEntries.AsNoTracking().Where(x => x.OriginatingBankId == bankId && x.Status != AchEntryStatus.Posted && x.Status != AchEntryStatus.NocReceived && x.Status != AchEntryStatus.Returned && x.Status != AchEntryStatus.Rejected).ToListAsync(token);
        var wirePending = await db.WireTransfers.AsNoTracking().Where(x => x.BankId == bankId && x.Direction == WireDirection.Outgoing
            && x.Status != WireStatus.Settled && x.Status != WireStatus.Completed && x.Status != WireStatus.Returned && x.Status != WireStatus.Rejected).ToListAsync(token);
        var flows = accounts.Select(a => new CustomerCashFlow(a,
            achPending.Where(x => x.Status != AchEntryStatus.Settled && x.OriginatingAccountId == a.Id && AchRules.IsCredit(x.TransactionCode)).Sum(x => x.Amount) + wirePending.Where(x => x.FromAccountId == a.Id).Sum(x => x.Amount),
            achPending.Where(x => x.Status != AchEntryStatus.Settled && x.OriginatingAccountId == a.Id && !AchRules.IsCredit(x.TransactionCode)).Sum(x => x.Amount))).ToList();
        var auth = await db.StudioDebitAuthorizations.AsNoTracking().Where(x => x.BankId == bankId).OrderByDescending(x => x.CreatedAt).Take(40).ToListAsync(token);
        var repair = await db.AchEntries.AsNoTracking().Include(x => x.Batch).ThenInclude(x => x!.File)
            .Where(x => x.OriginatingBankId == bankId && x.Status == AchEntryStatus.Rejected && x.Batch != null && x.Batch.File != null && x.Batch.File.Status == "Rejected"
                && !db.StudioAchRepairs.Any(r => r.OriginalEntryId == x.Id && r.BankId == bankId)).OrderByDescending(x => x.CreatedDate).Take(20).ToListAsync(token);
        var repairs = await db.StudioAchRepairs.AsNoTracking().Where(x => x.BankId == bankId).OrderByDescending(x => x.CreatedAt).Take(20).ToListAsync(token);
        var wires = await db.WireTransfers.AsNoTracking().Where(x => x.BankId == bankId && x.Direction == WireDirection.Outgoing).OrderByDescending(x => x.CreatedDate).Take(30).ToListAsync(token);
        var incoming = await db.WireTransfers.AsNoTracking().Where(x => x.BankId == bankId && x.Direction == WireDirection.Incoming && x.TransferType == WireTransferType.FinancialInstitutionCreditTransfer
            && (x.Status == WireStatus.Completed || x.Status == WireStatus.Settled)).OrderByDescending(x => x.CreatedDate).Take(30).ToListAsync(token);
        var funding = await db.StudioFundingRequests.AsNoTracking().Where(x => x.BankId == bankId).OrderByDescending(x => x.CreatedAt).Take(20).ToListAsync(token);
        var clock = await db.SimulationStates.AsNoTracking().SingleAsync(x => x.Name == "default", token);
        var assessments = new List<CampaignDayEvidence>();
        if (selected is not null && sessions.Count > 0)
        {
            var target = StudioCampaignPlan.Parse(selected.PlanJson, kind).MaxOutstanding;
            var start = sessions[0].StartedAt;
            var end = sessions[^1].EndedAt ?? DateTimeOffset.UtcNow;
            var sourceItems = new List<(Guid Id, DateTimeOffset At, bool Pending, bool Settled)>();
            var journalGroups = new List<(Guid PaymentId, bool Balanced)>();
            if (kind == "Wire")
            {
                var sources = await db.WireTransfers.AsNoTracking().Where(x => x.BankId == bankId && x.Direction == WireDirection.Outgoing && x.CreatedBy == WireTraffic.Maker && x.CreatedDate >= start && x.CreatedDate <= end).ToListAsync(token);
                sourceItems.AddRange(sources.Select(x => (x.Id, x.CreatedDate, StudioDayPolicy.CustomerOutcome(x.Status) == "Processing", StudioDayPolicy.Settled(x.Status))));
                var sourceIds = sources.Select(x => x.Id).ToList();
                var lines = await db.LedgerEntries.AsNoTracking().Where(x => x.WireTransfer.BankId == bankId && sourceIds.Contains(x.WireTransferId)).ToListAsync(token);
                journalGroups.AddRange(lines.GroupBy(x => new { x.JournalId, x.WireTransferId }).Select(g => (g.Key.WireTransferId, g.Count() >= 2 && g.Sum(x => x.Debit) > 0 && g.Sum(x => x.Debit) == g.Sum(x => x.Credit))));
            }
            else
            {
                var sources = await db.AchEntries.AsNoTracking().Where(x => x.OriginatingBankId == bankId && x.CreatedBy == AchTraffic.Maker && x.CreatedDate >= start && x.CreatedDate <= end).ToListAsync(token);
                sourceItems.AddRange(sources.Select(x => (x.Id, x.CreatedDate, !AchTraffic.IsTerminal(x.Status), StudioDayPolicy.Settled(x.Status))));
                var sourceIds = sources.Select(x => x.Id).ToList();
                var lines = await db.AchLedgerEntries.AsNoTracking().Where(x => x.Entry.OriginatingBankId == bankId && sourceIds.Contains(x.AchEntryId)).ToListAsync(token);
                journalGroups.AddRange(lines.GroupBy(x => new { x.JournalId, x.AchEntryId }).Select(g => (g.Key.AchEntryId, g.Count() >= 2 && g.Sum(x => x.Debit) > 0 && g.Sum(x => x.Debit) == g.Sum(x => x.Credit))));
            }
            foreach (var session in sessions)
            {
                var items = sourceItems.Where(x => x.At >= session.StartedAt && (session.EndedAt is null || x.At <= session.EndedAt)).ToList();
                var pendingCount = items.Count(x => x.Pending);
                var missingCount = items.Count(x => x.Settled && !journalGroups.Any(j => j.PaymentId == x.Id));
                var unbalanced = journalGroups.Count(j => !j.Balanced && items.Any(x => x.Id == j.PaymentId));
                var result = session.EndedAt is null ? "Still running" : session.Phase != 3 ? "Closed before all phases" : items.Count == 0 ? "No generated evidence"
                    : pendingCount <= target && missingCount == 0 && unbalanced == 0 ? "Operational targets met" : "Review outstanding or accounting evidence";
                assessments.Add(new(session.Id, items.Count, pendingCount, missingCount, unbalanced, result));
            }
        }
        return View(new CampaignRoomViewModel(bank, kind, selected, all, scenarios, sessions, notes, pending, flows, auth, repair, repairs, wires, incoming, funding,
            DateOnly.FromDateTime(clock.CurrentUtc.UtcDateTime), achPending.Count + wirePending.Count, assessments));
    }
    public async Task<IActionResult> Replay(Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var day = await db.StudioDays.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.BankId == bankId, token);
        if (day is null) return NotFound();
        var events = new List<StudioReplayEvent>();
        if (day.Kind == "Wire")
        {
            var wires = await db.WireTransfers.AsNoTracking().Include(x => x.Events).Include(x => x.IsoMessages).AsSplitQuery()
                .Where(x => x.BankId == bankId && x.Direction == WireDirection.Outgoing && x.CreatedBy == WireTraffic.Maker
                    && x.CreatedDate >= day.StartedAt && (day.EndedAt == null || x.CreatedDate <= day.EndedAt)).ToListAsync(token);
            var ids = wires.Select(x => x.Id).ToList();
            foreach (var wire in wires)
            {
                events.AddRange(wire.Events.Select(e => new StudioReplayEvent(e.CreatedDate, e.EventType, wire.Id, e.Description)));
                events.AddRange(wire.IsoMessages.Select(m => new StudioReplayEvent(m.CreatedDate, m.MessageType, wire.Id, m.XmlPayload)));
            }
            var lines = await db.LedgerEntries.AsNoTracking().Where(x => x.WireTransfer.BankId == bankId && ids.Contains(x.WireTransferId)).ToListAsync(token);
            events.AddRange(lines.Select(l => new StudioReplayEvent(l.CreatedDate, "Journal line", l.WireTransferId, $"Journal {l.JournalId}: {l.AccountCode} · {l.Description}", l.Debit, l.Credit)));
        }
        else
        {
            var entries = await db.AchEntries.AsNoTracking().Include(x => x.Batch).ThenInclude(x => x!.File)
                .Where(x => x.OriginatingBankId == bankId && x.CreatedBy == AchTraffic.Maker && x.CreatedDate >= day.StartedAt
                    && (day.EndedAt == null || x.CreatedDate <= day.EndedAt)).ToListAsync(token);
            var ids = entries.Select(x => x.Id).ToList();
            foreach (var entry in entries)
            {
                events.Add(new(entry.CreatedDate, "Instruction captured", entry.Id, $"{entry.CompanyName} → {entry.ReceiverName}, {entry.Amount:C}; effective {entry.EffectiveEntryDate}."));
                if (entry.ApprovedDate is DateTimeOffset approved) events.Add(new(approved, "Approved", entry.Id, $"Separate approver: {entry.ApprovedBy}"));
            }
            foreach (var file in entries.Where(x => x.Batch?.File is not null).Select(x => x.Batch!.File!).DistinctBy(x => x.Id))
                events.Add(new(file.CreatedDate, "NACHA file created", entries.First(x => x.Batch?.AchFileId == file.Id).Id, file.RawNachaPayload));
            var lines = await db.AchLedgerEntries.AsNoTracking().Where(x => x.Entry.OriginatingBankId == bankId && ids.Contains(x.AchEntryId)).ToListAsync(token);
            events.AddRange(lines.Select(l => new StudioReplayEvent(l.CreatedDate, "Journal line", l.AchEntryId, $"Journal {l.JournalId}: {l.AccountCode} · {l.Description}", l.Debit, l.Credit)));
            var returns = await db.AchReturns.AsNoTracking().Where(x => x.Entry.OriginatingBankId == bankId && ids.Contains(x.AchEntryId)).ToListAsync(token);
            events.AddRange(returns.Select(r => new StudioReplayEvent(r.ReceivedDate, "Return received", r.AchEntryId, $"{r.ReturnCode}: {r.Reason}")));
            var corrections = await db.AchNotificationsOfChange.AsNoTracking().Where(x => x.Entry.OriginatingBankId == bankId && ids.Contains(x.AchEntryId)).ToListAsync(token);
            events.AddRange(corrections.Select(n => new StudioReplayEvent(n.ReceivedDate, "Correction received", n.AchEntryId, $"{n.ChangeCode}: {n.Description}")));
        }
        return View(new StudioReplayViewModel(day, events.OrderBy(x => x.At).ThenBy(x => x.PaymentId).ThenBy(x => x.Type, StringComparer.Ordinal).ToList()));
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public Task<IActionResult> Author(string kind, string title, int morning, int afternoon, int deadline, int rate, string profile, bool requireAuthorization, int incidentPhase, string incident, int outstanding, string hint, CancellationToken token)
    {
        var plan = new StudioCampaignPlan([morning, afternoon, deadline], rate, profile, requireAuthorization, incidentPhase, incident, outstanding, hint ?? "");
        return Run(kind, bank => campaigns.AuthorAsync(bank, kind, title, plan.Serialize(), Actor, token), token);
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public Task<IActionResult> CreateCampaign(string kind, Guid scenarioId, int count, DateOnly firstDate, CancellationToken token) => Run(kind, bank => campaigns.CreateCampaignAsync(bank, scenarioId, count, firstDate, token), token);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public Task<IActionResult> NextDay(string kind, Guid id, CancellationToken token) => Run(kind, bank => days.StartAsync(bank, kind, Actor, false, token, id), token);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public Task<IActionResult> Grant(Guid accountId, string routing, string account, decimal limit, DateOnly through, CancellationToken token) => Run("Ach", bank => campaigns.AuthorizeAsync(bank, accountId, routing, account, limit, through, Actor, token), token);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public Task<IActionResult> Revoke(Guid id, CancellationToken token) => Run("Ach", bank => campaigns.RevokeAsync(bank, id, token), token);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentCreator)]
    public async Task<IActionResult> Repair(Guid id, string routing, string account, DateOnly date, string reason, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token); var bank = await ActiveBank.ResolveAsync(HttpContext, db, token);
        try { var replacement = await campaigns.RepairAsync(bank, id, routing, account, date, reason, Actor, token); return RedirectToAction("Details", "Ach", new { id = replacement }); }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; return RedirectToAction(nameof(Index), new { kind = "Ach" }); }
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public Task<IActionResult> Ask(Guid wireId, string text, CancellationToken token) => Run("Wire", bank => campaigns.ConversationAsync(bank, wireId, text, Actor, token), token);
    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Handoff(string kind, Guid campaignId, string role, string text, CancellationToken token) => Run(kind, bank => campaigns.HandoffAsync(bank, campaignId, role, text, Actor, token), token);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public Task<IActionResult> Funding(string kind, decimal amount, string reason, CancellationToken token) => Run(kind, bank => campaigns.FundingAsync(bank, amount, reason, Actor, token), token);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public Task<IActionResult> MatchFunding(string kind, Guid id, Guid wireId, CancellationToken token) => Run(kind, bank => campaigns.ReconcileFundingAsync(bank, id, wireId, token), token);
    private string Actor => User.Identity?.Name ?? "Lab operator";
    private async Task<IActionResult> Run(string kind, Func<Guid, Task> action, CancellationToken token)
    {
        if (!StudioDayPolicy.ValidKind(kind)) return BadRequest();
        if (!ModelState.IsValid) { TempData["Error"] = "Check the submitted amounts, identifiers, and dates."; return RedirectToAction(nameof(Index), new { kind }); }
        await using var db = await factory.CreateDbContextAsync(token); var bank = await ActiveBank.ResolveAsync(HttpContext, db, token);
        try { await action(bank); TempData["Notice"] = "Simulation work recorded. Inspect the resulting evidence and any required independent approval."; }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index), new { kind });
    }
}
