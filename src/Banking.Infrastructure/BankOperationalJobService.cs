using System.Data;
using System.Globalization;
using System.Text.Json;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed class BankOperationalJobService(IDbContextFactory<BankingDbContext> factory,
    AchBatchProcessor achProcessor, PaymentReconciliationService reconciliation)
{
    public async Task<BankReportDocument> ExecuteAsync(BankReportRun claim, CancellationToken token)
    {
        await using var probe = await factory.CreateDbContextAsync(token);
        return await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var run = await db.BankReportRuns.SingleOrDefaultAsync(x => x.Id == claim.Id && x.BankId == claim.BankId
                && x.Status == ReportRunStatus.Running && x.LeaseToken == claim.LeaseToken && x.CompletedAt == null, token)
                ?? throw new DbUpdateConcurrencyException("This job lease is no longer owned by this worker.");
            if (run.ReportJson is not null) return JsonSerializer.Deserialize<BankReportDocument>(run.ReportJson)!;
            var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == run.BankId, token);
            var lab = await db.SimulationStates.AsNoTracking().SingleAsync(x => x.Name == "default", token);
            var now = DateTimeOffset.UtcNow;
            var labDate = DateOnly.FromDateTime(lab.CurrentUtc.UtcDateTime);
            var from = run.RequestedAt.AddHours(-run.LookbackHours);
            var rows = new List<BankReportRow>();
            var metrics = new List<BankReportMetric>();
            var casesOpened = 0; var existingCases = 0; var checkedCount = 0;

            async Task AddCase(string reference, string summary, string rail, Guid? itemId = null,
                string? controller = null, decimal? amount = null, OperationsCaseSeverity severity = OperationsCaseSeverity.Medium)
            {
                var type = ReportSchedulePolicy.Title(run.Kind);
                var item = db.OperationsCases.Local.FirstOrDefault(x => x.BankId == run.BankId && x.CaseType == type && x.Reference == reference)
                    ?? await db.OperationsCases.Where(x => x.BankId == run.BankId && x.CaseType == type && x.Reference == reference).OrderByDescending(x => x.CreatedDate).FirstOrDefaultAsync(token);
                // Retain human decisions for identical findings; changed evidence may need a fresh case.
                var capturedSummary = summary[..Math.Min(240, summary.Length)];
                if (item is null || item.Status == OperationsCaseStatus.Resolved && item.Summary != capturedSummary)
                {
                    item = new() { BankId = run.BankId, CaseType = type, Reference = reference, Rail = rail,
                        Summary = capturedSummary, Severity = severity,
                        DueDate = now.AddHours(severity >= OperationsCaseSeverity.High ? 1 : 8),
                        Notes = $"Opened by operational job run {run.Id}. {summary}"[..Math.Min(2000, $"Opened by operational job run {run.Id}. {summary}".Length)] };
                    db.OperationsCases.Add(item); casesOpened++;
                    if (run.Kind == BankReportKind.ApprovalReminders)
                        db.StudioNotes.Add(new StudioNote { BankId = run.BankId, Kind = "Handoff", RecipientRole = "PaymentApprover",
                            Actor = "Operational job", WireId = controller == "Wires" ? itemId : null,
                            Text = $"{summary} Reference {reference}. Open the payment's approval workflow. Job run {run.Id}." });
                }
                else existingCases++;
                rows.Add(new(reference, $"Case {item.Status}",
                    $"{summary} Case {item.Id}. {(db.Entry(item).State == EntityState.Added ? "Opened by this run." : "Existing case retained.")}", amount, itemId, controller));
            }
            var wires = db.WireTransfers.AsNoTracking().Where(x => x.BankId == run.BankId);
            var ach = db.AchEntries.AsNoTracking().Where(x => x.OriginatingBankId == run.BankId
                || x.ReceivingBankId == run.BankId && (x.Status == AchEntryStatus.SentToOperator || x.Status == AchEntryStatus.Settled
                    || x.Status == AchEntryStatus.Posted || x.Status == AchEntryStatus.Returned || x.Status == AchEntryStatus.NocReceived));

            switch (run.Kind)
            {
                case BankReportKind.AchBatchPreparation:
                {
                    var entries = await db.AchEntries.Where(x => x.OriginatingBankId == run.BankId && x.Status == AchEntryStatus.Entered)
                        .OrderBy(x => x.CreatedDate).Select(x => x.Id).Take(200).ToListAsync(token);
                    checkedCount = entries.Count;
                    foreach (var id in entries)
                    {
                        var result = await achProcessor.PrepareAsync(db, run.BankId, id, labDate, token);
                        if (result is not null) rows.Add(result);
                        else rows.Add(new(id.ToString("N")[..12], "Not released", "Independent approval evidence is required; this job did not change the entry.", null, id, "Ach"));
                        await db.SaveChangesAsync(token); // Preserve batch grouping and exposure checks within the same transaction.
                    }
                    break;
                }
                case BankReportKind.AchCutoffProcessing:
                    checkedCount = await db.AchBatches.CountAsync(x => x.OriginatingBankId == run.BankId && x.Status == "Open", token);
                    rows.AddRange(await achProcessor.CloseAsync(db, run.BankId, labDate, token));
                    metrics.Add(new("Lab banking date", labDate.ToString("yyyy-MM-dd"), AchBusinessCalendar.IsBankingDay(labDate) ? "Eligible batches only" : "Not a banking day; no files submitted"));
                    break;
                case BankReportKind.ReturnNocReview:
                {
                    var returns = await db.AchReturns.AsNoTracking().Where(x => x.Entry.OriginatingBankId == run.BankId || x.Entry.ReceivingBankId == run.BankId).ToListAsync(token);
                    var changes = await db.AchNotificationsOfChange.AsNoTracking().Where(x => x.Entry.OriginatingBankId == run.BankId || x.Entry.ReceivingBankId == run.BankId).ToListAsync(token);
                    checkedCount = returns.Count + changes.Count;
                    foreach (var item in returns) await AddCase($"return:{item.Id:N}", $"Return {item.ReturnCode}: {item.Reason} Review original and reversing journals before deciding next steps.", "ACH", item.AchEntryId, "Ach");
                    foreach (var item in changes) await AddCase($"noc:{item.Id:N}", $"NOC {item.ChangeCode}: {item.Description} Review corrected instructions for future payments; preserve the original.", "ACH", item.AchEntryId, "Ach");
                    break;
                }
                case BankReportKind.StuckPayments:
                {
                    var pendingWires = await wires.Where(x => x.Status != WireStatus.Settled && x.Status != WireStatus.Completed && x.Status != WireStatus.Rejected && x.Status != WireStatus.Returned && x.Status != WireStatus.PendingApproval)
                        .Select(x => new { Wire = x, LastEvent = x.Events.Select(e => (DateTimeOffset?)e.CreatedDate).Max() }).ToListAsync(token);
                    var pendingAch = await ach.Include(x => x.Batch).ThenInclude(x => x!.File).Where(x => x.Status == AchEntryStatus.Entered || x.Status == AchEntryStatus.Validated || x.Status == AchEntryStatus.Batched || x.Status == AchEntryStatus.SentToOperator).ToListAsync(token);
                    checkedCount = pendingWires.Count + pendingAch.Count;
                    foreach (var item in pendingWires.Where(x => OperationalJobPolicy.IsStuck(x.Wire, x.LastEvent ?? x.Wire.CreatedDate, now)))
                        await AddCase($"wire:{item.Wire.Id:N}:{item.Wire.Status}", $"No new wire event for 15 minutes. {PaymentAttentionPolicy.For(item.Wire).Action}", item.Wire.Rail.ToString(), item.Wire.Id, "Wires", item.Wire.Amount);
                    foreach (var item in pendingAch.Where(x => OperationalJobPolicy.IsStuck(x, now, labDate, x.Batch?.File?.CreatedDate)))
                        await AddCase($"ach:{item.Id:N}:{item.Status}", $"ACH {item.Status} older than one hour since saved-file creation or approval/capture. {PaymentAttentionPolicy.For(item, labDate).Action}", "ACH", item.Id, "Ach", item.Amount);
                    break;
                }
                case BankReportKind.SettlementReconciliation:
                case BankReportKind.HoldReconciliation:
                {
                    var result = await reconciliation.ReconcileAsync(run.BankId, token);
                    var findings = result.Findings.Where(x => run.Kind == BankReportKind.HoldReconciliation ? x.Area == "Holds" : x.Area != "Holds");
                    foreach (var finding in findings)
                        await AddCase($"{finding.Area}:{finding.ItemId:N}", $"{finding.Reference}: expected {finding.Expected:N2}; observed {finding.Observed:N2}. {finding.Explanation}", finding.Area,
                            finding.Area is "Ach" or "Wires" ? finding.ItemId : null, finding.Area is "Ach" or "Wires" ? finding.Area : null, severity: OperationsCaseSeverity.High);
                    checkedCount = run.Kind == BankReportKind.HoldReconciliation
                        ? await db.Accounts.CountAsync(x => x.Customer.BankId == run.BankId, token)
                        : await wires.CountAsync(token) + await ach.CountAsync(token);
                    if (run.Kind == BankReportKind.SettlementReconciliation)
                    {
                        var mismatches = await wires.Where(x => (x.Rail == PaymentRail.Fedwire || x.Rail == PaymentRail.FedNow)
                            && (x.Status == WireStatus.Settled || x.Status == WireStatus.Completed)
                            && !db.FedSettlements.Any(s => s.CorrelationId == x.CorrelationId && s.StatusCode == "ACSC")).ToListAsync(token);
                        foreach (var item in mismatches) await AddCase($"network:{item.Id:N}", "Final wire state lacks an accepted correlated network settlement record. Inspect network and accounting evidence; do not resubmit automatically.", item.Rail.ToString(), item.Id, "Wires", item.Amount, OperationsCaseSeverity.High);
                        var achFileGaps = await ach.Where(x => (x.Status == AchEntryStatus.Settled || x.Status == AchEntryStatus.Posted || x.Status == AchEntryStatus.Returned || x.Status == AchEntryStatus.NocReceived)
                            && (x.Batch == null || x.Batch.File == null || x.Batch.File.Status != "Settled")).ToListAsync(token);
                        foreach (var item in achFileGaps) await AddCase($"ach-file:{item.Id:N}", "Final ACH state lacks a settled saved-file record. Inspect operator acceptance, trace, and original/return journals; no resubmission or accounting correction was performed.", "ACH", item.Id, "Ach", item.Amount, OperationsCaseSeverity.High);
                        if (result.UnbalancedJournals > 0) await AddCase("unbalanced-journals", $"{result.UnbalancedJournals} unbalanced journal(s) observed across rail subledgers. Inspect Operations journal controls.", "Cross-rail", severity: OperationsCaseSeverity.Critical);
                        metrics.Add(new("Unbalanced journals", result.UnbalancedJournals.ToString(), "All bank-visible rail journals"));
                    }
                    break;
                }
                case BankReportKind.LiquidityForecast:
                {
                    var outgoing = await wires.Where(x => x.Direction == WireDirection.Outgoing && x.Rail == PaymentRail.Fedwire
                        && x.Status != WireStatus.Settled && x.Status != WireStatus.Completed && x.Status != WireStatus.Rejected && x.Status != WireStatus.Returned).ToListAsync(token);
                    var credits = await db.AchEntries.Where(x => x.OriginatingBankId == run.BankId
                        && (x.TransactionCode == AchTransactionCode.CheckingCredit || x.TransactionCode == AchTransactionCode.SavingsCredit)
                        && (x.Status == AchEntryStatus.PendingApproval || x.Status == AchEntryStatus.Entered || x.Status == AchEntryStatus.Validated || x.Status == AchEntryStatus.Batched || x.Status == AchEntryStatus.SentToOperator)).ToListAsync(token);
                    checkedCount = outgoing.Count + credits.Count;
                    var wireDemand = outgoing.Sum(x => x.Amount); var achDemand = credits.Sum(x => x.Amount);
                    var cashGap = Math.Max(0, wireDemand + achDemand - bank.MasterAccountBalance - bank.FedwireCreditLimit);
                    var exposure = credits.Where(x => x.Status is AchEntryStatus.Validated or AchEntryStatus.Batched or AchEntryStatus.SentToOperator).Sum(x => x.Amount);
                    metrics.AddRange([Money("Bank cash", bank.MasterAccountBalance), Money("Fedwire credit capacity", bank.FedwireCreditLimit), Money("Outgoing Fedwire face amount", wireDemand), Money("Outgoing ACH credit face amount", achDemand), Money("Illustrative funding gap", cashGap), Money("Validated ACH exposure", exposure)]);
                    rows.Add(new("forecast", cashGap > 0 ? "Funding review" : "Within illustrative capacity", "Conservative combined face-amount forecast; assumes no incoming funds or offsetting debits. Fedwire credit capacity is not an ACH funding entitlement. Pending approvals may not release.", wireDemand + achDemand));
                    if (cashGap > 0) await AddCase("bank-funding-gap", $"Illustrative combined funding gap {cashGap:C}; review treasury funding and actual rail-specific limits before release.", "Cross-rail", severity: OperationsCaseSeverity.High);
                    if (exposure > bank.AchExposureLimit) await AddCase("ach-exposure-gap", $"Validated ACH exposure {exposure:C} exceeds teaching limit {bank.AchExposureLimit:C}.", "ACH", severity: OperationsCaseSeverity.High);
                    break;
                }
                case BankReportKind.DuplicateScreening:
                {
                    var wireCandidates = await wires.Where(x => x.Direction == WireDirection.Outgoing && x.CreatedDate >= from && x.CreatedDate <= run.RequestedAt && x.Status != WireStatus.Rejected).ToListAsync(token);
                    var achCandidates = await db.AchEntries.Where(x => x.OriginatingBankId == run.BankId && x.CreatedDate >= from && x.CreatedDate <= run.RequestedAt && x.Status != AchEntryStatus.Rejected).ToListAsync(token);
                    checkedCount = wireCandidates.Count + achCandidates.Count;
                    foreach (var group in wireCandidates.DistinctBy(x => x.CorrelationId).GroupBy(OperationalJobPolicy.DuplicateKey).Where(x => x.Count() > 1))
                    {
                        var first = group.OrderBy(x => x.CreatedDate).First();
                        await AddCase("wire-duplicate:" + OperationalJobPolicy.Fingerprint(group.Select(x => x.Id.ToString()).Order().ToArray()), $"{group.Count()} similar original wire instructions. Correlations: {string.Join(", ", group.Select(x => x.CorrelationId.ToString("N")[..12]))}. Distinct from transport redelivery; investigate before release.", first.Rail.ToString(), first.Id, "Wires", group.Sum(x => x.Amount));
                    }
                    foreach (var group in achCandidates.GroupBy(OperationalJobPolicy.DuplicateKey).Where(x => x.Count() > 1))
                    {
                        var first = group.OrderBy(x => x.CreatedDate).First();
                        await AddCase("ach-duplicate:" + OperationalJobPolicy.Fingerprint(group.Select(x => x.Id.ToString()).Order().ToArray()), $"{group.Count()} similar original ACH entries share origin, receiver, SEC/type, effective date, and amount. Entries: {string.Join(", ", group.Select(x => x.Id.ToString("N")[..12]))}. Review before release; saved files are unchanged.", "ACH", first.Id, "Ach", group.Sum(x => x.Amount));
                    }
                    break;
                }
                case BankReportKind.DeliveryRecovery:
                {
                    var pending = await db.OutboxMessages.Where(x => x.BankId == run.BankId && x.PublishedDate == null && x.Attempts > 0
                        && (x.NextAttemptDate == null || x.NextAttemptDate <= now)).OrderBy(x => x.CreatedDate).Take(100).ToListAsync(token);
                    checkedCount = pending.Count;
                    foreach (var item in pending)
                    {
                        item.NextAttemptDate = now;
                        rows.Add(new(item.Id.ToString("N"), "Original retry due", $"{item.Queue}: {item.Attempts} failed attempt(s). Existing publisher will resend with this same stable ID and payload. No new payment or message record created."));
                    }
                    var dead = await db.DeadLetterMessages.AsNoTracking().Where(x => x.BankId == run.BankId && x.ReplayedDate == null).ToListAsync(token);
                    foreach (var item in dead) await AddCase($"dead-letter:{item.Id:N}", $"Dead letter on {item.Queue}; {item.Attempts} failed attempts. Inspect the payload and authorize replay in Operations.", "Transport", severity: OperationsCaseSeverity.High);
                    break;
                }
                case BankReportKind.ApprovalReminders:
                {
                    var cutoff = now.AddMinutes(-15);
                    var awaitingWires = await wires.Where(x => x.Status == WireStatus.PendingApproval && x.CreatedDate <= cutoff).ToListAsync(token);
                    var awaitingAch = await db.AchEntries.Where(x => x.OriginatingBankId == run.BankId && x.Status == AchEntryStatus.PendingApproval && x.CreatedDate <= cutoff).ToListAsync(token);
                    checkedCount = awaitingWires.Count + awaitingAch.Count;
                    foreach (var item in awaitingWires) await AddCase($"wire-approval:{item.Id:N}", $"Approver handoff: wire awaiting independent approval since {item.CreatedDate:u}. Maker: {item.CreatedBy}. No release was performed.", item.Rail.ToString(), item.Id, "Wires", item.Amount);
                    foreach (var item in awaitingAch) await AddCase($"ach-approval:{item.Id:N}", $"Approver handoff: ACH awaiting independent approval since {item.CreatedDate:u}. Maker: {item.CreatedBy}. No release was performed.", "ACH", item.Id, "Ach", item.Amount);
                    break;
                }
                case BankReportKind.CaseEscalation:
                {
                    var cases = await db.OperationsCases.Where(x => x.BankId == run.BankId && x.Status != OperationsCaseStatus.Resolved
                        && (x.DueDate <= now || x.AssignedTo == null || x.AssignedTo == "")).ToListAsync(token);
                    checkedCount = cases.Count;
                    foreach (var item in cases)
                    {
                        var raised = item.DueDate <= now && item.Severity < OperationsCaseSeverity.High;
                        if (raised) { item.Severity = OperationsCaseSeverity.High; item.UpdatedDate = now; }
                        rows.Add(new(item.Reference, raised ? "Escalated to High" : "Review required", $"Case {item.Id}. {(item.DueDate <= now ? "Overdue" : "Unassigned")}; due {item.DueDate:u}. Ownership, work notes, and resolution preserved."));
                    }
                    break;
                }
                case BankReportKind.EndOfDayClose:
                {
                    var pendingWires = await wires.Where(x => x.Status != WireStatus.Settled && x.Status != WireStatus.Completed && x.Status != WireStatus.Rejected && x.Status != WireStatus.Returned).CountAsync(token);
                    var pendingAch = await ach.Where(x => x.Status == AchEntryStatus.PendingApproval || x.Status == AchEntryStatus.Entered || x.Status == AchEntryStatus.Validated || x.Status == AchEntryStatus.Batched || x.Status == AchEntryStatus.SentToOperator).CountAsync(token);
                    var openCases = await db.OperationsCases.CountAsync(x => x.BankId == run.BankId && x.Status != OperationsCaseStatus.Resolved, token);
                    var returns = await db.AchReturns.CountAsync(x => (x.Entry.OriginatingBankId == run.BankId || x.Entry.ReceivingBankId == run.BankId) && x.ReceivedDate >= from && x.ReceivedDate <= run.RequestedAt, token);
                    var controls = await reconciliation.ReconcileAsync(run.BankId, token);
                    checkedCount = pendingWires + pendingAch;
                    var wireDemand = await wires.Where(x => x.Direction == WireDirection.Outgoing && x.Rail == PaymentRail.Fedwire
                        && x.Status != WireStatus.Settled && x.Status != WireStatus.Completed && x.Status != WireStatus.Rejected && x.Status != WireStatus.Returned).SumAsync(x => (decimal?)x.Amount, token) ?? 0;
                    var achDemand = await ach.Where(x => x.OriginatingBankId == run.BankId && (x.TransactionCode == AchTransactionCode.CheckingCredit || x.TransactionCode == AchTransactionCode.SavingsCredit)
                        && (x.Status == AchEntryStatus.PendingApproval || x.Status == AchEntryStatus.Entered || x.Status == AchEntryStatus.Validated || x.Status == AchEntryStatus.Batched || x.Status == AchEntryStatus.SentToOperator)).SumAsync(x => (decimal?)x.Amount, token) ?? 0;
                    metrics.Add(Money("Illustrative combined funding gap", Math.Max(0, wireDemand + achDemand - bank.MasterAccountBalance - bank.FedwireCreditLimit)));
                    metrics.Add(Money("Customer funds on hold", await db.Accounts.Where(x => x.Customer.BankId == run.BankId).SumAsync(x => (decimal?)x.HeldBalance, token) ?? 0));
                    metrics.AddRange([Money("Closing observed bank cash", bank.MasterAccountBalance), new("Unsettled wires", pendingWires.ToString(), "Current state"), new("Unsettled ACH entries", pendingAch.ToString(), "Includes future-dated work"), new("Open cases", openCases.ToString(), "Before this review case"), new("ACH returns in window", returns.ToString(), "By return receipt timestamp"), new("Controls", controls.IsClear ? "Clear" : "Review", "Current observations; not a close certificate")]);
                    await AddCase($"close:{labDate:yyyyMMdd}:{DateOnly.FromDateTime(now.UtcDateTime):yyyyMMdd}", $"End-of-day review for lab {labDate:yyyy-MM-dd}: {pendingWires} unsettled wires, {pendingAch} ACH, {openCases} open cases; controls {(controls.IsClear ? "clear" : "need review")}. Human close assessment required.", "Cross-rail");
                    foreach (var finding in controls.Findings) rows.Add(new(finding.Reference, finding.Area, $"Expected {finding.Expected:N2}; observed {finding.Observed:N2}. {finding.Explanation}"));
                    break;
                }
                default: throw new ArgumentOutOfRangeException(nameof(run.Kind));
            }
            metrics.InsertRange(0, [new("Records checked", checkedCount.ToString(), "Current bank scope"), new("Findings / action rows", rows.Count.ToString(), "Evidence rows before display limit"), new("Cases opened", casesOpened.ToString(), "Deduplicated across tasks and runs"), new("Existing cases retained", existingCases.ToString(), "Includes human-resolved cases")]);
            var document = new BankReportDocument(2, bank.Id, bank.Name, run.Kind, from, run.RequestedAt, now, lab.CurrentUtc,
                $"Operational job: {ReportSchedulePolicy.Description(run.Kind)} Full pending-bank checks unless the job description specifies a window. ACH preparation handles up to 200 entries per run; retry recovery handles up to 100 messages. Showing at most 200 evidence rows. These are lab controls and observations, not rail certification or an atomic regulatory closing statement.",
                metrics, [new("Job findings & actions", "Cases are in the Operations inbox. Identical observations retain existing cases, including resolved cases; new payment states and new source events can create new cases. No automatic approval, journal correction, or case resolution.", rows.Take(200).ToList())]);
            // Action evidence and effects commit together. A recovered worker returns this snapshot instead of repeating actions.
            run.ReportJson = JsonSerializer.Serialize(document);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return document;
        });
    }
    private static BankReportMetric Money(string label, decimal value) => new(label, value.ToString("C", CultureInfo.GetCultureInfo("en-US")), "Simulated USD");
}
