using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web;

public sealed class LearningEvidenceService(IDbContextFactory<BankingDbContext> dbFactory)
{
    public async Task<LearningRunViewModel?> EvaluateLatestAsync(Guid bankId, string missionId,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var run = await db.LearningRuns.Where(x => x.BankId == bankId && x.MissionId == missionId)
            .OrderByDescending(x => x.StartedDate).FirstOrDefaultAsync(token);
        return run is null ? null : await EvaluateAsync(db, run, token);
    }

    public async Task<LearningRunViewModel?> EvaluateAsync(Guid bankId, Guid runId,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var run = await db.LearningRuns.SingleOrDefaultAsync(x => x.Id == runId && x.BankId == bankId, token);
        return run is null ? null : await EvaluateAsync(db, run, token);
    }

    private static async Task<LearningRunViewModel> EvaluateAsync(BankingDbContext db,
        LearningRun run, CancellationToken token)
    {
        var evidence = new List<LearningEvidenceViewModel>();
        var events = new List<LearningEventViewModel>();
        var journal = new List<LearningJournalLineViewModel>();

        if (run.ItemId is null)
            await AttachLatestItemAsync(db, run, token);

        if (run.ItemType == "Wire" && run.ItemId is Guid wireId)
            await EvaluateWireAsync(db, run, wireId, evidence, events, journal, token);
        else if (run.ItemType == "ACH" && run.ItemId is Guid achId)
            await EvaluateAchAsync(db, run, achId, evidence, events, journal, token);
        else if (run.ItemType == "Check" && run.ItemId is Guid checkId)
            await EvaluateCheckAsync(db, run, checkId, evidence, events, journal, token);
        else if (run.ItemType == "Message" && run.ItemId is Guid exchangeId)
            await EvaluateMessageAsync(db, run.BankId, exchangeId, evidence, events, token);
        else
        {
            var answered = await db.LearningAttempts.AnyAsync(x => x.BankId == run.BankId
                && x.MissionId == run.MissionId && x.IsCorrect && x.CreatedDate >= run.StartedDate, token);
            Add(evidence, "assessment", "Reasoned decision", "A supported correct decision",
                answered ? "Correct answer recorded" : "No correct answer since this run started", answered, "Business acceptance");
        }

        var current = await CurrentBalancesAsync(db, run.BankId, token);
        var passed = evidence.Count > 0 && evidence.All(x => x.Passed);
        run.EvidenceScore = evidence.Count == 0 ? 0 : (int)Math.Round(100m * evidence.Count(x => x.Passed) / evidence.Count);
        run.Status = passed ? "EvidenceVerified" : run.ItemId is null ? "AwaitingAction" : "EvidencePending";
        run.EvidenceSummary = string.Join(" | ", evidence.Select(x => $"{x.Code}:{(x.Passed ? "pass" : "pending")}"));
        run.UpdatedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);

        return new(run.Id, run.Status, run.LearnerAlias, run.Rail, run.ItemType, run.ItemId,
            run.EvidenceScore, run.StartedDate, run.BaselineCustomerBalance, current.Customer,
            run.BaselineHeldBalance, current.Held, run.BaselineMasterBalance, current.Master,
            evidence, events, journal, passed);
    }

    private static async Task AttachLatestItemAsync(BankingDbContext db, LearningRun run,
        CancellationToken token)
    {
        if (run.MissionId.StartsWith("ach-", StringComparison.Ordinal))
        {
            var item = await db.AchEntries.Where(x => x.OriginatingBankId == run.BankId
                    && x.CreatedDate >= run.StartedDate)
                .OrderByDescending(x => x.CreatedDate).Select(x => new { x.Id }).FirstOrDefaultAsync(token);
            if (item is not null) { run.ItemType = "ACH"; run.ItemId = item.Id; }
            return;
        }
        if (run.MissionId.StartsWith("check-", StringComparison.Ordinal))
        {
            var item = await db.CheckDeposits.Where(x => x.DepositoryBankId == run.BankId
                    && x.CreatedDate >= run.StartedDate)
                .OrderByDescending(x => x.CreatedDate).Select(x => new { x.Id }).FirstOrDefaultAsync(token);
            if (item is not null) { run.ItemType = "Check"; run.ItemId = item.Id; }
            return;
        }
        if (run.MissionId == "nonvalue-request")
        {
            var item = await db.MessageExchanges.Where(x => x.BankId == run.BankId
                    && x.CreatedDate >= run.StartedDate)
                .OrderByDescending(x => x.CreatedDate).Select(x => new { x.Id }).FirstOrDefaultAsync(token);
            if (item is not null) { run.ItemType = "Message"; run.ItemId = item.Id; }
            return;
        }
        if (run.MissionId == "compare-rails") return;

        var wireQuery = db.WireTransfers.Where(x => x.BankId == run.BankId);
        if (run.MissionId != "iso-message-chain")
            wireQuery = wireQuery.Where(x => x.CreatedDate >= run.StartedDate);
        var wire = await wireQuery
            .OrderByDescending(x => x.CreatedDate).Select(x => new { x.Id }).FirstOrDefaultAsync(token);
        if (wire is not null) { run.ItemType = "Wire"; run.ItemId = wire.Id; }
    }

    private static async Task EvaluateWireAsync(BankingDbContext db, LearningRun run, Guid id,
        List<LearningEvidenceViewModel> evidence, List<LearningEventViewModel> events,
        List<LearningJournalLineViewModel> journal, CancellationToken token)
    {
        var wire = await db.WireTransfers.AsNoTracking().SingleAsync(x => x.Id == id && x.BankId == run.BankId, token);
        var wireEvents = await db.WireEvents.Where(x => x.WireTransferId == id).OrderBy(x => x.CreatedDate).AsNoTracking().ToListAsync(token);
        var messages = await db.IsoMessages.Where(x => x.WireTransferId == id).AsNoTracking().ToListAsync(token);
        var deliveries = await db.MessageDeliveries.Where(x => x.WireTransferId == id).AsNoTracking().ToListAsync(token);
        var lines = await db.LedgerEntries.Where(x => x.WireTransferId == id).OrderBy(x => x.CreatedDate).AsNoTracking().ToListAsync(token);
        journal.AddRange(lines.Select(x => new LearningJournalLineViewModel(x.AccountCode, x.AccountName, x.Debit, x.Credit)));
        events.AddRange(wireEvents.Select((x, i) => new LearningEventViewModel(i + 1, x.EventType,
            Owner(x.EventType), x.Description, x.CreatedDate)));

        var expectedRail = Enum.TryParse<PaymentRail>(run.Rail, out var rail) ? rail : (PaymentRail?)null;
        Add(evidence, "rail", "Rail selection", expectedRail?.ToString() ?? "Mission-appropriate rail", wire.Rail.ToString(),
            expectedRail is null || wire.Rail == expectedRail, "Business acceptance");
        Add(evidence, "instruction", "Instruction recorded", "Bank-scoped instruction", wire.Status.ToString(), true, "Business acceptance");
        Add(evidence, "transport", "Transport", "Delivery is distinct from acceptance",
            deliveries.Count == 0 ? "No delivery yet" : string.Join(", ", deliveries.Select(x => x.Status).Distinct()),
            deliveries.Any(x => x.Status == DeliveryStatus.Delivered), "Transport delivery");
        var final = wire.Status is WireStatus.Settled or WireStatus.Completed or WireStatus.Rejected or WireStatus.Returned;
        Add(evidence, "business", "Final business status", "Accepted/settled or explicit rejection", wire.Status.ToString(), final, "Business acceptance");
        Add(evidence, "message", "ISO evidence", "Instruction and correlated status messages",
            messages.Count == 0 ? "None" : string.Join(", ", messages.Select(x => x.MessageType).Distinct()),
            messages.Any(x => x.MessageType.StartsWith("pacs.00", StringComparison.OrdinalIgnoreCase)), "Transport delivery");
        var rejects = wire.Status == WireStatus.Rejected;
        var balanced = lines.Count > 0 && lines.Sum(x => x.Debit) == lines.Sum(x => x.Credit);
        Add(evidence, "settlement", "Settlement", rejects ? "No settlement on rejection" : "Final settlement",
            rejects ? (lines.Count == 0 ? "No value journal" : "Unexpected value journal") : wire.Status.ToString(),
            rejects ? lines.Count == 0 : wire.Status is WireStatus.Settled or WireStatus.Completed, "Settlement");
        Add(evidence, "accounting", "Accounting", rejects ? "Hold released; no posting" : "Balanced correlated journal",
            lines.Count == 0 ? "No journal" : $"Debits {lines.Sum(x => x.Debit):C}; credits {lines.Sum(x => x.Credit):C}",
            rejects ? lines.Count == 0 : balanced, "Accounting posting");
        if (run.MissionId == "fednow-finality")
        {
            var receiverAccepted = messages.Any(x => x.MessageType == "pacs.002"
                && (x.XmlPayload.Contains("ACCP", StringComparison.Ordinal)
                    || x.XmlPayload.Contains("ACWP", StringComparison.Ordinal)));
            Add(evidence, "receiver", "Receiver acceptance", "ACCP or ACWP before final settlement",
                receiverAccepted ? "Receiver acceptance status found" : "No receiver acceptance status yet",
                receiverAccepted, "Business acceptance");
        }
        if (run.MissionId == "liquidity-transfer")
            Add(evidence, "transfer-type", "Institution liquidity", "pacs.009 without a customer source account",
                wire.TransferType.ToString(), wire.TransferType == WireTransferType.FinancialInstitutionCreditTransfer
                    && wire.FromAccountId is null, "Business acceptance");
        if (run.MissionId == "swift-route")
        {
            var steps = await db.PaymentRouteSteps.Where(x => x.PaymentRoute.PaymentId == id)
                .OrderBy(x => x.StepNumber).AsNoTracking().ToListAsync(token);
            Add(evidence, "route", "Correspondent route", "Persisted ordered route with completed legs",
                steps.Count == 0 ? "No route" : string.Join(" → ", steps.Select(x => $"{x.StepNumber}:{x.Status}")),
                steps.Count > 0 && steps.All(x => x.CompletedDate is not null), "Transport delivery");
        }
        if (run.MissionId == "idempotent-replay")
        {
            var journals = lines.Select(x => x.JournalId).Distinct().Count();
            Add(evidence, "idempotency", "Duplicate-safe business effect", "At most one value journal for this payment",
                $"{deliveries.Sum(x => Math.Max(1, x.Attempts))} delivery attempt(s); {journals} journal(s)",
                journals <= 1 && final, "Accounting posting");
        }
    }

    private static async Task EvaluateAchAsync(BankingDbContext db, LearningRun run, Guid id,
        List<LearningEvidenceViewModel> evidence, List<LearningEventViewModel> events,
        List<LearningJournalLineViewModel> journal, CancellationToken token)
    {
        var entry = await db.AchEntries.AsNoTracking().SingleAsync(x => x.Id == id && x.OriginatingBankId == run.BankId, token);
        var lines = await db.AchLedgerEntries.Where(x => x.AchEntryId == id).AsNoTracking().ToListAsync(token);
        var returned = await db.AchReturns.AnyAsync(x => x.AchEntryId == id, token);
        var noc = await db.AchNotificationsOfChange.AnyAsync(x => x.AchEntryId == id, token);
        journal.AddRange(lines.Select(x => new LearningJournalLineViewModel(x.AccountCode, x.AccountName, x.Debit, x.Credit)));
        events.Add(new(1, "Entry", "ODFI", $"{entry.SecCode} {entry.TransactionCode} entry recorded.", entry.CreatedDate));
        if (entry.AchBatchId is not null) events.Add(new(2, "Batch", "ACH service", "Entry grouped into an effective-date batch.", entry.CreatedDate));
        Add(evidence, "entry", "Entry validation", "Validated ACH entry", entry.Status.ToString(), entry.Status != AchEntryStatus.Entered, "Business acceptance");
        Add(evidence, "batch", "Batch/file boundary", "Trace and batch/file assignment", entry.TraceNumber ?? "No trace assigned",
            entry.AchBatchId is not null && entry.TraceNumber is not null, "Transport delivery");
        var expectedException = run.MissionId == "ach-return";
        Add(evidence, "decision", "RDFI outcome", expectedException ? "Return or notification of change" : "Settlement/posting",
            returned ? "Return received" : noc ? "Notification of change received" : entry.Status.ToString(),
            expectedException ? returned || noc || entry.Status is AchEntryStatus.Returned or AchEntryStatus.NocReceived
                : entry.Status is AchEntryStatus.Settled or AchEntryStatus.Posted, "Settlement");
        var balanced = lines.Count > 0 && lines.Sum(x => x.Debit) == lines.Sum(x => x.Credit);
        Add(evidence, "accounting", "ACH accounting", "Balanced journal for value movement",
            lines.Count == 0 ? "No journal yet" : $"Debits {lines.Sum(x => x.Debit):C}; credits {lines.Sum(x => x.Credit):C}",
            expectedException ? returned || noc : balanced, "Accounting posting");
    }

    private static async Task EvaluateCheckAsync(BankingDbContext db, LearningRun run, Guid id,
        List<LearningEvidenceViewModel> evidence, List<LearningEventViewModel> events,
        List<LearningJournalLineViewModel> journal, CancellationToken token)
    {
        var deposit = await db.CheckDeposits.AsNoTracking().SingleAsync(x => x.Id == id && x.DepositoryBankId == run.BankId, token);
        var checkEvents = await db.CheckEvents.Where(x => x.CheckDepositId == id).OrderBy(x => x.CreatedDate).AsNoTracking().ToListAsync(token);
        var lines = await db.CheckLedgerEntries.Where(x => x.CheckDepositId == id).AsNoTracking().ToListAsync(token);
        journal.AddRange(lines.Select(x => new LearningJournalLineViewModel(x.AccountCode, x.AccountName, x.Debit, x.Credit)));
        events.AddRange(checkEvents.Select((x, i) => new LearningEventViewModel(i + 1, x.EventType, "Check workflow", x.Description, x.CreatedDate)));
        Add(evidence, "capture", "Capture", "MICR plus front/back image evidence", deposit.Status.ToString(), checkEvents.Count > 0, "Business acceptance");
        Add(evidence, "cash-letter", "Image cash letter", "Created and sent separately from capture",
            deposit.CheckCashLetterId is null ? "Not created" : "Cash letter linked", deposit.CheckCashLetterId is not null, "Transport delivery");
        var duplicateMission = run.MissionId == "check-duplicate";
        var terminal = deposit.Status is CheckDepositStatus.Settled or CheckDepositStatus.Returned or CheckDepositStatus.Rejected;
        Add(evidence, "paying-bank", "Paying-bank decision", duplicateMission ? "Duplicate return" : "Settlement or explicit return",
            deposit.ReturnReason ?? deposit.Status.ToString(), terminal && (!duplicateMission || deposit.Status == CheckDepositStatus.Returned), "Settlement");
        var balanced = lines.Count > 0 && lines.Sum(x => x.Debit) == lines.Sum(x => x.Credit);
        Add(evidence, "accounting", "Check accounting", duplicateMission ? "No duplicate value posting" : "Balanced clearing journal",
            lines.Count == 0 ? "No journal" : $"Debits {lines.Sum(x => x.Debit):C}; credits {lines.Sum(x => x.Credit):C}",
            duplicateMission ? deposit.Status == CheckDepositStatus.Returned : balanced, "Accounting posting");
    }

    private static async Task EvaluateMessageAsync(BankingDbContext db, Guid bankId, Guid id,
        List<LearningEvidenceViewModel> evidence, List<LearningEventViewModel> events,
        CancellationToken token)
    {
        var exchange = await db.MessageExchanges.Include(x => x.Messages).AsNoTracking()
            .SingleAsync(x => x.Id == id && x.BankId == bankId, token);
        events.AddRange(exchange.Messages.OrderBy(x => x.CreatedDate).Select((x, i) =>
            new LearningEventViewModel(i + 1, x.MessageType, x.Direction.ToString(), "Non-value message persisted.", x.CreatedDate)));
        Add(evidence, "request", "Request message", "Persisted request", string.Join(", ", exchange.Messages.Select(x => x.MessageType)), exchange.Messages.Count > 0, "Transport delivery");
        Add(evidence, "response", "Correlated response", "Request and response without value movement", exchange.Status.ToString(), exchange.Messages.Count >= 2, "Business acceptance");
        Add(evidence, "settlement", "Value movement", "No settlement journal", "Non-value conversation", true, "Settlement");
    }

    private static void Add(List<LearningEvidenceViewModel> items, string code, string label,
        string expected, string observed, bool passed, string layer) =>
        items.Add(new(code, label, expected, observed, passed, layer));

    private static string Owner(string eventType) => eventType.Contains("Appro", StringComparison.OrdinalIgnoreCase) ? "Approver"
        : eventType.Contains("Sett", StringComparison.OrdinalIgnoreCase) ? "Rail / settlement"
        : eventType.Contains("Post", StringComparison.OrdinalIgnoreCase) ? "Accounting"
        : eventType.Contains("Creat", StringComparison.OrdinalIgnoreCase) ? "Maker" : "Payment service";

    private static async Task<(decimal Customer, decimal Held, decimal Master)> CurrentBalancesAsync(
        BankingDbContext db, Guid bankId, CancellationToken token)
    {
        var accounts = await db.Accounts.Where(x => x.Customer.BankId == bankId)
            .Select(x => new { x.Balance, x.HeldBalance }).AsNoTracking().ToListAsync(token);
        var master = await db.Banks.Where(x => x.Id == bankId).Select(x => x.MasterAccountBalance).SingleAsync(token);
        return (accounts.Sum(x => x.Balance), accounts.Sum(x => x.HeldBalance), master);
    }
}
