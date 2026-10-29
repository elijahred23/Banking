using System.Globalization;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed class BankReportBuilder(IDbContextFactory<BankingDbContext> factory,
    PaymentReconciliationService reconciliation, BankOperationalJobService? operationalJobs = null)
{
    public async Task<BankReportDocument> BuildAsync(BankReportRun run, CancellationToken token)
    {
        if (ReportSchedulePolicy.IsOperational(run.Kind))
            return await (operationalJobs ?? throw new InvalidOperationException("Operational job services are not configured.")).ExecuteAsync(run, token);
        await using var db = await factory.CreateDbContextAsync(token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == run.BankId, token);
        var lab = await db.SimulationStates.AsNoTracking().SingleAsync(x => x.Name == "default", token);
        var from = run.RequestedAt.AddHours(-run.LookbackHours);
        var metrics = new List<BankReportMetric>();
        var sections = new List<BankReportSection>();
        if (run.Kind == BankReportKind.Reconciliation)
        {
            var result = await reconciliation.ReconcileAsync(run.BankId, token);
            metrics.AddRange([
                Metric("Control outcome", result.IsClear ? "Clear" : "Review", "Live controls; not a reconciliation certificate"),
                Metric("Unbalanced journals", result.UnbalancedJournals), Metric("Hold mismatches", result.HoldMismatches),
                Metric("Missing wire journals", result.SettledWiresWithoutJournals), Metric("Missing ACH journals", result.SettledAchWithoutJournals),
                Metric("Unpublished messages", result.PendingOutboxMessages), Metric("Incomplete inbox", result.IncompleteInboxMessages),
                Metric("Dead letters", result.DeadLetterMessages)]);
            sections.Add(new("Control findings", "Expected versus observed at capture. Posting gaps may be transient. Journal-presence amounts are 1 = yes, 0 = no; hold amounts are currency.",
                result.Findings.Take(200).Select(x => new BankReportRow(x.Reference, x.Area,
                    $"Expected {x.Expected:N2}; observed {x.Observed:N2}. {x.Explanation}", null,
                    x.Area is "Ach" or "Wires" ? x.ItemId : null, x.Area is "Ach" or "Wires" ? x.Area : null)).ToList()));
        }
        else if (run.Kind == BankReportKind.Liquidity)
        {
            var accounts = await db.Accounts.AsNoTracking().Where(x => x.Customer.BankId == run.BankId)
                .Select(x => new { x.Id, x.AccountNumber, Customer = x.Customer.Name, x.Balance, x.HeldBalance,
                    x.UncollectedBalance, x.OverdraftLimit, x.Status }).ToListAsync(token);
            metrics.AddRange([
                Money("Bank settlement cash", bank.MasterAccountBalance), Money("Fedwire credit limit", bank.FedwireCreditLimit),
                Money("Customer ledger balances", accounts.Sum(x => x.Balance)), Money("Funds on hold", accounts.Sum(x => x.HeldBalance)),
                Money("Collected balances", accounts.Sum(x => x.Balance - x.UncollectedBalance)),
                Money("Available balances", accounts.Sum(x => x.Balance - x.HeldBalance - x.UncollectedBalance + x.OverdraftLimit)),
                Money("ACH exposure limit", bank.AchExposureLimit), Metric("Customer accounts", accounts.Count)]);
            sections.Add(new("Account availability", "Current lab balances, not historical closing balances. Account identifiers are masked. Up to 200 accounts are listed; totals include all accounts.",
                accounts.OrderBy(x => x.Customer).ThenBy(x => x.AccountNumber).Take(200).Select(x => new BankReportRow(
                    $"{x.Customer} · •••• {x.AccountNumber[^Math.Min(4, x.AccountNumber.Length)..]}", x.Status.ToString(),
                    $"Held {Currency(x.HeldBalance)}; collected {Currency(x.Balance - x.UncollectedBalance)}; available {Currency(x.Balance - x.HeldBalance - x.UncollectedBalance + x.OverdraftLimit)}", x.Balance)).ToList()));
        }
        else if (run.Kind is BankReportKind.PaymentActivity or BankReportKind.Exceptions)
        {
            var wires = db.WireTransfers.AsNoTracking().Where(x => x.BankId == run.BankId
                && x.CreatedDate >= from && x.CreatedDate < run.RequestedAt);
            var ach = db.AchEntries.AsNoTracking().Where(x => (x.OriginatingBankId == run.BankId
                    || x.ReceivingBankId == run.BankId && (x.Status == AchEntryStatus.SentToOperator
                        || x.Status == AchEntryStatus.Settled || x.Status == AchEntryStatus.Posted
                        || x.Status == AchEntryStatus.Returned || x.Status == AchEntryStatus.NocReceived))
                && x.CreatedDate >= from && x.CreatedDate < run.RequestedAt);
            if (run.Kind == BankReportKind.Exceptions)
            {
                wires = wires.Where(x => x.Status == WireStatus.Rejected || x.Status == WireStatus.Returned || x.Status == WireStatus.PendingComplianceReview);
                ach = ach.Where(x => x.Status == AchEntryStatus.Rejected || x.Status == AchEntryStatus.Returned || x.Status == AchEntryStatus.NocReceived);
                var cases = db.OperationsCases.AsNoTracking().Where(x => x.BankId == run.BankId && x.Status != OperationsCaseStatus.Resolved);
                metrics.Add(Metric("Open operations cases", await cases.CountAsync(token), "All open cases, regardless of creation date"));
                var rows = await cases.OrderBy(x => x.DueDate).Take(200).ToListAsync(token);
                sections.Add(new("Open operations cases", "All currently unresolved cases, up to 200 ordered by due time. Due times use wall-clock UTC.", rows.Select(x =>
                    new BankReportRow(x.Reference, $"{x.Status} · {x.Severity}", $"{x.Summary} Owner: {x.AssignedTo ?? "Unassigned"}. Due {x.DueDate:u}.")).ToList()));
            }
            var wireGroups = await wires.GroupBy(x => new { x.Rail, x.Direction, x.Status })
                .Select(x => new { x.Key.Rail, x.Key.Direction, x.Key.Status, Count = x.Count(), Amount = x.Sum(y => y.Amount) }).ToListAsync(token);
            var achGroups = await ach.GroupBy(x => new { Originating = x.OriginatingBankId == run.BankId, x.TransactionCode, x.Status })
                .Select(x => new { x.Key.Originating, x.Key.TransactionCode, x.Key.Status, Count = x.Count(), Amount = x.Sum(y => y.Amount) }).ToListAsync(token);
            metrics.AddRange([Metric("Wire records", wireGroups.Sum(x => x.Count)), Money("Wire face amount", wireGroups.Sum(x => x.Amount)),
                Metric("ACH entries", achGroups.Sum(x => x.Count)), Money("ACH face amount", achGroups.Sum(x => x.Amount))]);
            sections.Add(new("Activity by recorded state", "Full counts for instructions created in the reporting window. Amounts are face amounts, not net settlement movements. States are observed at capture, not reconstructed at the window end.",
                wireGroups.Select(x => new BankReportRow($"{x.Rail} · {x.Direction}", x.Status.ToString(), $"{x.Count} wire record(s)", x.Amount))
                .Concat(achGroups.Select(x => new BankReportRow($"ACH · {(x.Originating ? "Originating" : "Receiving")} · {x.TransactionCode}", x.Status.ToString(), $"{x.Count} entry/entries", x.Amount))).ToList()));
            var wireRows = await wires.OrderByDescending(x => x.CreatedDate).Take(100).ToListAsync(token);
            var achRows = await ach.OrderByDescending(x => x.CreatedDate).Take(100).ToListAsync(token);
            sections.Add(new("Payment evidence", "Latest 100 matching wire records and 100 ACH entries. Open a payment to inspect its current evidence; the saved report remains unchanged.",
                wireRows.Select(x => new BankReportRow(x.CustomerReference ?? x.CorrelationId.ToString("N")[..12], x.Status.ToString(),
                    $"{x.Rail} · {x.SenderName} → {x.ReceiverName}", x.Amount, x.Id, "Wires"))
                .Concat(achRows.Select(x => new BankReportRow(x.TraceNumber ?? x.Id.ToString("N")[..12], x.Status.ToString(),
                    $"ACH · {x.CompanyName} → {x.ReceiverName}", x.Amount, x.Id, "Ach"))).ToList()));
        }
        else throw new InvalidOperationException("Unsupported report type.");
        return new(1, bank.Id, bank.Name, run.Kind, from, run.RequestedAt, DateTimeOffset.UtcNow, lab.CurrentUtc,
            "Bank-scoped learning report. Activity uses instruction creation timestamps in the rolling UTC window; controls and balances are current observations. Queries run sequentially while processing may continue. This is not a certified operator statement or an atomic end-of-day snapshot.", metrics, sections);
    }
    private static BankReportMetric Metric(string label, int value, string hint = "At capture") => new(label, value.ToString("N0", CultureInfo.InvariantCulture), hint);
    private static BankReportMetric Metric(string label, string value, string hint) => new(label, value, hint);
    private static BankReportMetric Money(string label, decimal value) => new(label, Currency(value), "Simulated USD");
    private static string Currency(decimal value) => value.ToString("C", CultureInfo.GetCultureInfo("en-US"));
}
