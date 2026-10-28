using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web;

public sealed class StudioDayDashboardService(IDbContextFactory<BankingDbContext> factory)
{
    public async Task<StudioDayPanelViewModel> ReadAsync(Guid bankId, string kind, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var day = await db.StudioDays.AsNoTracking().Where(x => x.BankId == bankId && x.Kind == kind)
            .OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync(token);
        var settings = await db.StudioSettings.AsNoTracking().SingleOrDefaultAsync(x => x.BankId == bankId && x.Kind == kind, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var accounts = await db.Accounts.AsNoTracking().Where(x => x.Customer.BankId == bankId).ToListAsync(token);
        var clock = await db.SimulationStates.AsNoTracking().SingleAsync(x => x.Name == "default", token);
        var cases = await db.OperationsCases.AsNoTracking().Where(x => x.BankId == bankId && x.Rail == kind
            && x.Status != OperationsCaseStatus.Resolved).OrderBy(x => x.DueDate).Take(20).ToListAsync(token);
        var inbox = new List<StudioInboxItem>();
        var journals = new List<StudioJournalEvidence>();
        var count = 0; var settled = 0; var outstanding = 0; var missing = 0; decimal inFlight = 0;
        Guid? inquiry = null;
        // A day owns the bank's generated source instructions created in its start/end interval.
        // No manual payments or other bank's incoming counterparts enter the day totals.
        if (day is not null)
        {
            if (kind == "Wire")
            {
                var wires = await db.WireTransfers.AsNoTracking().Include(x => x.Cases).Where(x => x.BankId == bankId
                    && x.Direction == WireDirection.Outgoing && x.CreatedBy == WireTraffic.Maker
                    && x.CreatedDate >= day.StartedAt && (day.EndedAt == null || x.CreatedDate <= day.EndedAt))
                    .OrderByDescending(x => x.CreatedDate).ToListAsync(token);
                var ids = wires.Select(x => x.Id).ToList();
                var lines = await db.LedgerEntries.AsNoTracking().Where(x => x.WireTransfer.BankId == bankId && ids.Contains(x.WireTransferId))
                    .ToListAsync(token);
                journals.AddRange(lines.GroupBy(x => new { x.JournalId, x.WireTransferId })
                    .Select(g => new StudioJournalEvidence(g.Key.JournalId, g.Key.WireTransferId, g.Sum(x => x.Debit), g.Sum(x => x.Credit), g.Count())));
                count = wires.Count; settled = wires.Count(x => StudioDayPolicy.Settled(x.Status));
                var pending = wires.Where(x => !StudioDayPolicy.Settled(x.Status) && x.Status is not (WireStatus.Rejected or WireStatus.Returned)).ToList();
                outstanding = pending.Count; inFlight = pending.Sum(x => x.Amount);
                missing = wires.Count(x => StudioDayPolicy.Settled(x.Status) && !lines.Any(y => y.WireTransferId == x.Id));
                inquiry = wires.FirstOrDefault()?.Id;
                foreach (var wire in wires)
                {
                    var action = wire.Status switch
                    {
                        WireStatus.PendingApproval => "Resume generation for the separate simulated approver, or inspect the authorized approval workflow. No customer posting is justified yet.",
                        WireStatus.PendingComplianceReview => "Switch to an authorized compliance persona and inspect the screening decision.",
                        WireStatus.Rejected => "Review rejection evidence and applicable hold release; repair through the normal instruction workflow.",
                        WireStatus.Returned => "Reconcile the original and return journals; retain the investigation audit trail.",
                        WireStatus.PendingAtFed or WireStatus.SentToFed => "Trace message correlation and final outcome before considering a retry.",
                        _ => null
                    };
                    if (action is not null) inbox.Add(new(wire.Id, "Wires", $"{wire.SenderName} → {wire.ReceiverName}", wire.Status.ToString(), action));
                    foreach (var item in wire.Cases.Where(x => x.Status is WireCaseStatus.Resolved or WireCaseStatus.Rejected or WireCaseStatus.ReturnCompleted))
                        inbox.Add(new(wire.Id, "Wires", $"Investigation response: {item.Type}", item.Status.ToString(), item.Resolution ?? "Read the correlated response; case response and return of funds are separate evidence."));
                }
            }
            else
            {
                var entries = await db.AchEntries.AsNoTracking().Include(x => x.Batch).ThenInclude(x => x!.File)
                    .Where(x => x.OriginatingBankId == bankId && x.CreatedBy == AchTraffic.Maker
                        && x.CreatedDate >= day.StartedAt && (day.EndedAt == null || x.CreatedDate <= day.EndedAt))
                    .OrderByDescending(x => x.CreatedDate).ToListAsync(token);
                var ids = entries.Select(x => x.Id).ToList();
                var lines = await db.AchLedgerEntries.AsNoTracking().Where(x => x.Entry.OriginatingBankId == bankId && ids.Contains(x.AchEntryId)).ToListAsync(token);
                journals.AddRange(lines.GroupBy(x => new { x.JournalId, x.AchEntryId })
                    .Select(g => new StudioJournalEvidence(g.Key.JournalId, g.Key.AchEntryId, g.Sum(x => x.Debit), g.Sum(x => x.Credit), g.Count())));
                count = entries.Count; settled = entries.Count(x => StudioDayPolicy.Settled(x.Status));
                var pending = entries.Where(x => !AchTraffic.IsTerminal(x.Status)).ToList();
                outstanding = pending.Count; inFlight = pending.Sum(x => x.Amount);
                missing = entries.Count(x => StudioDayPolicy.Settled(x.Status) && !lines.Any(y => y.AchEntryId == x.Id));
                inquiry = entries.FirstOrDefault()?.Id;
                foreach (var entry in entries)
                {
                    var action = entry.Status switch
                    {
                        AchEntryStatus.PendingApproval => "Resume generation for the separate simulated approver, or inspect the authorized approval workflow before batching.",
                        AchEntryStatus.Returned => $"Inspect return code {entry.ReturnCode ?? "and reason"} and reconcile the reversing journal.",
                        AchEntryStatus.NocReceived => "Review corrected account data for future instructions; do not reverse a payment solely for a notification of change.",
                        AchEntryStatus.Rejected => "Determine whether instruction validation or the operator file failed; verify applicable hold releases.",
                        AchEntryStatus.Batched when entry.EffectiveEntryDate > DateOnly.FromDateTime(clock.CurrentUtc.UtcDateTime) => "The entry is scheduled for a future effective date. Inspect the shared clock and file cutoff.",
                        _ => null
                    };
                    if (action is not null) inbox.Add(new(entry.Id, "Ach", $"{entry.CompanyName} → {entry.ReceiverName}", entry.Status.ToString(), action));
                }
            }
        }
        var rosterCount = await db.PayrollMembers.CountAsync(x => x.BankId == bankId && x.Enabled, token);
        return new(kind, day, settings, bank, accounts.Sum(x => x.Balance), accounts.Sum(x => x.HeldBalance), clock.CurrentUtc,
            inbox.Take(20).ToList(), cases, journals, count, settled, outstanding, inFlight, missing, rosterCount, inquiry);
    }
}
