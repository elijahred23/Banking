using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize]
public sealed class OperationsController(
    IDbContextFactory<BankingDbContext> dbFactory,
    IQueueOperationsMonitor queueMonitor) : Controller
{
    public async Task<IActionResult> Index(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);

        var wireStatuses = await db.WireTransfers.AsNoTracking()
            .Where(x => x.BankId == bankId).Select(x => x.Status).ToListAsync(token);
        var achStatuses = await db.AchEntries.AsNoTracking()
            .Where(x => x.OriginatingBankId == bankId || x.ReceivingBankId == bankId)
            .Select(x => x.Status).ToListAsync(token);
        var checkStatuses = await db.CheckDeposits.AsNoTracking()
            .Where(x => x.DepositoryBankId == bankId || x.PayingBankId == bankId)
            .Select(x => x.Status).ToListAsync(token);

        var payments = new PaymentStatusSummary(
            wireStatuses.Count(x => x is WireStatus.PendingApproval or WireStatus.Created)
                + achStatuses.Count(x => x == AchEntryStatus.Entered)
                + checkStatuses.Count(x => x == CheckDepositStatus.Captured),
            wireStatuses.Count(x => x is WireStatus.PendingComplianceReview or WireStatus.Validated or WireStatus.ReadyForFed or WireStatus.SentToFed
                or WireStatus.PendingAtFed or WireStatus.Received)
                + achStatuses.Count(x => x is AchEntryStatus.Validated or AchEntryStatus.Batched
                    or AchEntryStatus.SentToOperator or AchEntryStatus.NocReceived)
                + checkStatuses.Count(x => x is CheckDepositStatus.Validated
                    or CheckDepositStatus.ImageCashLetterCreated or CheckDepositStatus.SentToExchange
                    or CheckDepositStatus.PresentedToPayingBank),
            wireStatuses.Count(x => x is WireStatus.Settled or WireStatus.Completed)
                + achStatuses.Count(x => x is AchEntryStatus.Settled or AchEntryStatus.Posted)
                + checkStatuses.Count(x => x == CheckDepositStatus.Settled),
            wireStatuses.Count(x => x is WireStatus.Rejected or WireStatus.Returned)
                + achStatuses.Count(x => x == AchEntryStatus.Returned)
                + checkStatuses.Count(x => x is CheckDepositStatus.Returned or CheckDepositStatus.Rejected));

        var queueTask = queueMonitor.GetSnapshotAsync(token);
        // EF contexts do not support concurrent operations; keep SQL reads sequential while
        // the independent broker probe runs in parallel.
        var exceptions = await LoadExceptionsAsync(db, bankId, token);
        var movements = await LoadSettlementMovementsAsync(db, bankId, token);
        var journals = await LoadJournalsAsync(db, bankId, token);
        var queueSnapshot = await queueTask;
        var rails = BuildRailHealth(bank, queueSnapshot);
        return View(new OperationsDashboardViewModel(bank, payments, queueSnapshot,
            exceptions, movements, journals, rails, DateTimeOffset.UtcNow));
    }

    private static async Task<IReadOnlyList<OperationsExceptionViewModel>> LoadExceptionsAsync(
        BankingDbContext db, Guid bankId, CancellationToken token)
    {
        var wireItems = await db.WireTransfers.AsNoTracking()
            .Where(x => x.BankId == bankId && (x.Scenario == ProcessingScenario.MalformedIso
                || x.Status == WireStatus.PendingComplianceReview
                || x.Status == WireStatus.Rejected || x.Status == WireStatus.Returned))
            .OrderByDescending(x => x.CreatedDate).Take(10)
            .Select(x => new { x.CorrelationId, x.Scenario, x.Status, x.ComplianceReason, x.CreatedDate }).ToListAsync(token);
        var achItems = await db.AchEntries.AsNoTracking()
            .Where(x => (x.OriginatingBankId == bankId || x.ReceivingBankId == bankId)
                && (x.Scenario != AchProcessingScenario.Standard || x.Status == AchEntryStatus.Returned))
            .OrderByDescending(x => x.CreatedDate).Take(10)
            .Select(x => new { x.Id, x.Scenario, x.Status, x.ReturnCode, x.CreatedDate }).ToListAsync(token);
        var checkItems = await db.CheckDeposits.AsNoTracking()
            .Where(x => (x.DepositoryBankId == bankId || x.PayingBankId == bankId)
                && (x.Scenario != CheckProcessingScenario.Standard
                    || x.Status == CheckDepositStatus.Returned || x.Status == CheckDepositStatus.Rejected))
            .OrderByDescending(x => x.CreatedDate).Take(10)
            .Select(x => new { x.CorrelationId, x.Scenario, x.Status, x.ReturnReason, x.CreatedDate })
            .ToListAsync(token);

        var items = new List<OperationsExceptionViewModel>();
        items.AddRange(wireItems.Select(x => new OperationsExceptionViewModel("ISO 20022",
            x.Scenario == ProcessingScenario.MalformedIso ? "Malformed ISO"
                : x.Status == WireStatus.PendingComplianceReview ? "Compliance review"
                : x.Status == WireStatus.Returned ? "Payment returned" : "Payment rejected",
            x.CorrelationId.ToString("N")[..8], x.ComplianceReason ?? $"{x.Scenario} · {x.Status}", x.CreatedDate)));
        items.AddRange(achItems.Select(x => new OperationsExceptionViewModel("ACH / NACHA",
            x.Scenario == AchProcessingScenario.InsufficientFunds ? "Insufficient funds" : "NACHA exception",
            x.Id.ToString("N")[..8], $"{x.Scenario} · {x.ReturnCode ?? x.Status.ToString()}", x.CreatedDate)));
        items.AddRange(checkItems.Select(x => new OperationsExceptionViewModel("Check",
            x.Scenario == CheckProcessingScenario.DuplicatePresentment ? "Duplicate check" : "Check exception",
            x.CorrelationId.ToString("N")[..8], x.ReturnReason ?? $"{x.Scenario} · {x.Status}", x.CreatedDate)));
        return items.OrderByDescending(x => x.OccurredDate).Take(8).ToList();
    }

    private static async Task<IReadOnlyList<SettlementMovementViewModel>> LoadSettlementMovementsAsync(
        BankingDbContext db, Guid bankId, CancellationToken token)
    {
        var bankNames = await db.Banks.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, token);
        var wires = await db.WireTransfers.AsNoTracking()
            .Where(x => (x.SenderBankId == bankId || x.ReceiverBankId == bankId)
                && (x.Status == WireStatus.Settled || x.Status == WireStatus.Completed))
            .OrderByDescending(x => x.CreatedDate).Take(8).ToListAsync(token);
        var ach = await db.AchEntries.AsNoTracking()
            .Where(x => (x.OriginatingBankId == bankId || x.ReceivingBankId == bankId)
                && (x.Status == AchEntryStatus.Settled || x.Status == AchEntryStatus.Posted))
            .OrderByDescending(x => x.CreatedDate).Take(8).ToListAsync(token);
        var checks = await db.CheckDeposits.AsNoTracking()
            .Where(x => (x.DepositoryBankId == bankId || x.PayingBankId == bankId)
                && x.Status == CheckDepositStatus.Settled)
            .OrderByDescending(x => x.CreatedDate).Take(8).ToListAsync(token);

        var result = new List<SettlementMovementViewModel>();
        result.AddRange(wires.Select(x => new SettlementMovementViewModel(x.Rail.ToString(),
            x.CorrelationId.ToString("N")[..8], BankName(bankNames,
                x.SenderBankId == bankId ? x.ReceiverBankId : x.SenderBankId),
            x.SenderBankId == bankId ? -x.Amount : x.Amount, x.CreatedDate)));
        result.AddRange(ach.Select(x => new SettlementMovementViewModel("ACH", x.Id.ToString("N")[..8],
            x.OriginatingBankId == bankId ? BankName(bankNames, x.ReceivingBankId) : BankName(bankNames, x.OriginatingBankId),
            x.OriginatingBankId == bankId ? -x.Amount : x.Amount, x.CreatedDate)));
        result.AddRange(checks.Select(x => new SettlementMovementViewModel("Check",
            x.CorrelationId.ToString("N")[..8],
            x.DepositoryBankId == bankId ? BankName(bankNames, x.PayingBankId) : BankName(bankNames, x.DepositoryBankId),
            x.DepositoryBankId == bankId ? x.Amount : -x.Amount, x.CreatedDate)));
        return result.OrderByDescending(x => x.RecordedDate).Take(8).ToList();
    }

    private static async Task<IReadOnlyList<JournalSummaryViewModel>> LoadJournalsAsync(
        BankingDbContext db, Guid bankId, CancellationToken token)
    {
        var wireEntries = await db.LedgerEntries.AsNoTracking().Where(x => x.WireTransfer.BankId == bankId)
            .Select(x => new { x.JournalId, x.Debit, x.Credit, x.CreatedDate }).ToListAsync(token);
        var achEntries = await db.AchLedgerEntries.AsNoTracking()
            .Where(x => x.Entry.OriginatingBankId == bankId || x.Entry.ReceivingBankId == bankId)
            .Select(x => new { x.JournalId, x.Debit, x.Credit, x.CreatedDate }).ToListAsync(token);
        var checkEntries = await db.CheckLedgerEntries.AsNoTracking()
            .Where(x => x.CheckDeposit.DepositoryBankId == bankId || x.CheckDeposit.PayingBankId == bankId)
            .Select(x => new { x.JournalId, x.Debit, x.Credit, x.CreatedDate }).ToListAsync(token);

        var wires = wireEntries.GroupBy(x => x.JournalId).Select(x => new JournalSummaryViewModel(
            x.Key, "Wire", x.Count(), x.Sum(e => e.Debit), x.Sum(e => e.Credit), x.Max(e => e.CreatedDate)));
        var ach = achEntries.GroupBy(x => x.JournalId).Select(x => new JournalSummaryViewModel(
            x.Key, "ACH", x.Count(), x.Sum(e => e.Debit), x.Sum(e => e.Credit), x.Max(e => e.CreatedDate)));
        var checks = checkEntries.GroupBy(x => x.JournalId).Select(x => new JournalSummaryViewModel(
            x.Key, "Check", x.Count(), x.Sum(e => e.Debit), x.Sum(e => e.Credit), x.Max(e => e.CreatedDate)));
        return wires.Concat(ach).Concat(checks).OrderByDescending(x => x.CreatedDate).Take(8).ToList();
    }

    private static IReadOnlyList<RailHealthViewModel> BuildRailHealth(Bank bank, QueueMonitorSnapshot snapshot)
    {
        RailHealthViewModel Rail(string name, bool enabled, params string[] queues)
        {
            if (!enabled) return new(name, "Offline", "Disabled for this bank");
            if (!snapshot.BrokerAvailable) return new(name, "Degraded", "RabbitMQ unavailable");
            var found = snapshot.Queues.Where(x => queues.Contains(x.Name)).ToList();
            if (found.Any(x => !x.Exists)) return new(name, "Degraded", "One or more transport queues are not declared");
            if (found.Any(x => x.ConsumerCount == 0)) return new(name, "Degraded", "A transport queue has no consumer");
            return new(name, "Operational", "Transport queues connected");
        }

        return
        [
            Rail("Fedwire", true, Queues.FedOutbound, Queues.FedInbound),
            Rail("FedNow", bank.FedNowEnabled && bank.FedNowOnline,
                Queues.FedNowOutbound, Queues.FedNowInbound),
            Rail("ACH", true, Queues.AchOutbound, Queues.AchInbound),
            Rail("Check", true, Queues.CheckOutbound, Queues.CheckInbound),
            Rail("SWIFT", bank.SwiftEnabled, Queues.SwiftOutbound, Queues.SwiftInbound)
        ];
    }

    private static string BankName(IReadOnlyDictionary<Guid, string> banks, Guid? id) =>
        id is { } value && banks.TryGetValue(value, out var name) ? name : "External institution";
}
