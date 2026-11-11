using System.Text.Json;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed class StatementReconciliationService(IDbContextFactory<BankingDbContext> factory)
{
    public Task<Guid> ReconcileAsync(Guid bankId, Guid statementId, CancellationToken ct) => StatementTransactions.Run(factory, async db => {
        var statement = await db.AccountStatements.Include(x => x.Entries).Include(x => x.Account).AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == statementId && (x.IssuerBankId == bankId
                || x.RecipientBankId == bankId && db.StatementDeliveries.Any(d => d.StatementId == x.Id && d.Status == StatementDeliveryStatus.Delivered)), ct)
            ?? throw new KeyNotFoundException("Statement was not found in this bank's reporting center.");
        if (statement.Kind == StatementKind.Notification) throw new ArgumentException("Reconcile a full intraday report or daily statement, not a single-entry notification.");
        var account = statement.Account;
        var own = await db.CorrespondentLedgerEntries.AsNoTracking().Where(x => x.AccountId == account.Id
            && x.BankId == account.RespondentBankId && x.IsAccountLine).ToListAsync(ct);
        var issuer = await db.CorrespondentLedgerEntries.AsNoTracking().Where(x => x.AccountId == account.Id
            && x.BankId == account.IssuerBankId && x.IsAccountLine).ToListAsync(ct);
        var inclusive = statement.Kind == StatementKind.Intraday;
        bool BeforeEnd(DateTimeOffset at) => at < statement.PeriodEnd || inclusive && at == statement.PeriodEnd;
        var findings = StatementPolicy.Match(statement.Entries, own, statement.PeriodStart, statement.PeriodEnd, inclusive).ToList();
        foreach (var entry in own.Where(x => x.BookingAt >= statement.PeriodStart && BeforeEnd(x.BookingAt)
            && !statement.Entries.Any(s => s.PostingId == x.PostingId)))
        {
            var elsewhere = issuer.SingleOrDefault(x => x.PostingId == entry.PostingId
                && (x.BookingAt < statement.PeriodStart || !BeforeEnd(x.BookingAt)));
            if (elsewhere is null) continue;
            findings.RemoveAll(x => x.Reference == entry.Reference && x.Type is "Missing debit" or "Missing credit");
            findings.Add(new("Timing difference", entry.Reference, 0, "Respondent booked this posting in the current period; the issuer booked it in a different period."));
        }
        var actual = account.RespondentBalance - own.Where(x => !BeforeEnd(x.BookingAt)).Sum(x => x.Debit - x.Credit);
        var difference = actual - statement.ClosingBalance;
        var currentLedger = account.OpeningBalance + own.Sum(x => x.Debit - x.Credit);
        if (currentLedger != account.RespondentBalance)
            findings.Add(new("Balance mismatch", account.AccountNumber, account.RespondentBalance - currentLedger, "Respondent due-from position disagrees with its ledger."));
        decimal timingEffect = 0;
        foreach (var item in issuer.Where(x => BeforeEnd(x.BookingAt)))
        {
            var mirror = own.Where(x => x.PostingId == item.PostingId).ToList();
            if (mirror.Count == 1 && !BeforeEnd(mirror[0].BookingAt)
                && mirror[0].Debit - mirror[0].Credit == item.Credit - item.Debit)
                timingEffect -= item.Credit - item.Debit;
        }
        foreach (var item in own.Where(x => BeforeEnd(x.BookingAt)))
        {
            var source = issuer.Where(x => x.PostingId == item.PostingId).ToList();
            if (source.Count == 1 && !BeforeEnd(source[0].BookingAt)
                && item.Debit - item.Credit == source[0].Credit - source[0].Debit)
                timingEffect += item.Debit - item.Credit;
        }
        if (timingEffect != 0 && !findings.Any(x => x.Type == "Timing difference"))
            findings.Add(new("Timing difference", statement.StatementNumber, timingEffect, "Known posting in another reporting period carries as an outstanding timing item."));
        if (difference != timingEffect)
            findings.Add(new("Balance mismatch", statement.StatementNumber, difference - timingEffect, "Closing difference is not fully explained by matched timing items."));
        if (statement.GenerationDifference != 0)
            findings.Add(new("Balance mismatch", statement.StatementNumber, statement.GenerationDifference, "Issuer generation validation failed; this statement is held."));
        var trueBreaks = findings.Any(x => x.Type != "Timing difference");
        var result = new StatementReconciliation { StatementId = statement.Id, BankId = account.RespondentBankId,
            ExpectedBalance = statement.ClosingBalance, ActualBalance = actual, Difference = difference,
            Status = trueBreaks ? StatementReconciliationStatus.Exception : findings.Count > 0 ? StatementReconciliationStatus.TimingDifference : StatementReconciliationStatus.Reconciled,
            FindingsJson = JsonSerializer.Serialize(findings), CreatedAt = DateTimeOffset.UtcNow };
        db.StatementReconciliations.Add(result);
        var reference = "STMT:" + statement.Id.ToString("N");
        var openCases = await db.OperationsCases.Where(x => x.BankId == account.RespondentBankId
            && x.Rail == "Statements" && x.Reference == reference && x.Status != OperationsCaseStatus.Resolved).ToListAsync(ct);
        if (trueBreaks && openCases.Count == 0)
            db.OperationsCases.Add(new OperationsCase { BankId = account.RespondentBankId, Rail = "Statements",
                CaseType = "Statement reconciliation", Reference = reference,
                Summary = "Unexplained correspondent statement differences require review.",
                Notes = string.Join(" ", findings.Select(x => x.Type)).Substring(0, Math.Min(1900, string.Join(" ", findings.Select(x => x.Type)).Length)),
                DueDate = DateTimeOffset.UtcNow.AddDays(1) });
        else if (!trueBreaks)
            foreach (var item in openCases) { item.Status = OperationsCaseStatus.Resolved; item.Resolution = "Reconciliation rerun has no unexplained differences."; item.ResolvedDate = DateTimeOffset.UtcNow; }
        return result.Id;
    }, ct);

    public Task DeliverAsync(StatementReady message, CancellationToken ct) => StatementTransactions.Run(factory, async db => {
        var delivery = await db.StatementDeliveries.Include(x => x.Statement).SingleAsync(x => x.StatementId == message.StatementId
            && x.RecipientBankId == message.RecipientBankId, ct);
        if (delivery.Statement.RecipientBankId != message.RecipientBankId || delivery.Statement.GenerationDifference != 0)
            throw new InvalidOperationException("Statement delivery recipient or generation validation failed.");
        if (delivery.Status == StatementDeliveryStatus.Delivered) return false;
        delivery.AttemptCount++;
        delivery.Status = StatementDeliveryStatus.Delivered;
        delivery.DeliveredAt = await StatementTransactions.Now(db, ct);
        return true;
    }, ct);
}
