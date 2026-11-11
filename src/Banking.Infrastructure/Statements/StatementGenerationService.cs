using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed class StatementGenerationService(IDbContextFactory<BankingDbContext> factory)
{
    public Task<Guid> GenerateAsync(Guid bankId, Guid accountId, StatementKind kind, DateTimeOffset start,
        DateTimeOffset end, Guid? corrects, CancellationToken ct) => StatementTransactions.Run(factory, async db => {
            var account = await StatementTransactions.Account(db, bankId, accountId, false, ct);
            if ((kind != StatementKind.Intraday || corrects != null) && account.IssuerBankId != bankId) throw new ArgumentException("Only the issuer generates end-of-day statements or corrections.");
            if (kind == StatementKind.Notification || !Enum.IsDefined(kind)) throw new ArgumentException("Use the booked-entry notification workflow.");
            return await Generate(db, account, kind, start, end, corrects, null, ct);
        }, ct);

    internal static async Task<Guid> Generate(BankingDbContext db, CorrespondentAccount account, StatementKind kind,
        DateTimeOffset start, DateTimeOffset end, Guid? corrects, Guid? postingId, CancellationToken ct)
    {
        var now = await StatementTransactions.Now(db, ct);
        start = start.ToUniversalTime(); end = end.ToUniversalTime();
        if (start > end || start == end && kind != StatementKind.Intraday || end > now || end < account.CreatedAt || end - start > TimeSpan.FromDays(370))
            throw new ArgumentException("Choose a completed reporting interval of at most 370 days using the simulation clock.");
        var existing = await db.AccountStatements.Where(x => x.AccountId == account.Id && x.Kind == kind && x.PeriodEnd == end).ToListAsync(ct);
        if (postingId is Guid notification)
        {
            var duplicate = await db.AccountStatements.SingleOrDefaultAsync(x => x.AccountId == account.Id && x.NotificationPostingId == notification, ct);
            if (duplicate is not null) return duplicate.Id;
        }
        else if (corrects is null && existing.Count > 0 && kind != StatementKind.Intraday)
        {
            var prior = existing.OrderByDescending(x => x.Version).First();
            if (prior.PeriodStart != start) throw new ArgumentException("A statement with this period end already uses a different period start.");
            return prior.Id;
        }
        if (kind == StatementKind.Intraday && corrects is null && existing.Count > 0)
        {
            var prior = existing.OrderByDescending(x => x.Version).First();
            if (prior.PeriodStart != start) throw new ArgumentException("Use the same period start when refreshing this intraday cutoff.");
            var priorIds = await db.AccountStatementEntries.Where(x => x.StatementId == prior.Id).Select(x => x.LedgerEntryId).ToListAsync(ct);
            var currentIds = await db.CorrespondentLedgerEntries.Where(x => x.AccountId == account.Id && x.BankId == account.IssuerBankId
                && x.IsAccountLine && x.BookingAt >= start && x.BookingAt <= end).Select(x => x.Id).ToListAsync(ct);
            var laterDelta = await db.CorrespondentLedgerEntries.Where(x => x.AccountId == account.Id && x.BankId == account.IssuerBankId
                && x.IsAccountLine && x.BookingAt > end).SumAsync(x => (decimal?)(x.Credit - x.Debit), ct) ?? 0;
            if (priorIds.ToHashSet().SetEquals(currentIds) && prior.GenerationDifference == 0
                && prior.ClosingBalance == account.IssuerBalance - laterDelta) return prior.Id;
        }
        if (corrects is Guid previousId)
        {
            var previous = existing.SingleOrDefault(x => x.Id == previousId && x.PeriodStart == start)
                ?? throw new ArgumentException("Correction must reference the same account, message type, and period.");
            if (previous.Version != existing.Max(x => x.Version)) throw new ArgumentException("Correct the most recent version.");
        }
        var lines = await db.CorrespondentLedgerEntries.AsNoTracking().Where(x => x.AccountId == account.Id
            && x.BankId == account.IssuerBankId && x.IsAccountLine).OrderBy(x => x.BookingAt).ThenBy(x => x.Id).ToListAsync(ct);
        var selected = postingId is Guid posting ? lines.Where(x => x.PostingId == posting).ToList()
            : lines.Where(x => x.BookingAt >= start && (x.BookingAt < end || kind == StatementKind.Intraday && x.BookingAt == end)).ToList();
        if (postingId is not null && selected.Count != 1) throw new InvalidOperationException("Notification requires one finalized account entry.");
        var sequence = (await db.AccountStatements.Where(x => x.AccountId == account.Id).Select(x => (int?)x.SequenceNumber).MaxAsync(ct) ?? 0) + 1;
        var statement = new AccountStatement { AccountId = account.Id, IssuerBankId = account.IssuerBankId,
            RecipientBankId = account.RespondentBankId, Kind = kind, PeriodStart = start, PeriodEnd = end,
            CreatedAt = now, PreviousStatementId = corrects, NotificationPostingId = postingId,
            Version = existing.Count == 0 ? 1 : existing.Max(x => x.Version) + 1, SequenceNumber = sequence,
            AccountNumber = account.AccountNumber,
            IssuerName = await db.Banks.Where(x => x.Id == account.IssuerBankId).Select(x => x.Name).SingleAsync(ct),
            RecipientName = await db.Banks.Where(x => x.Id == account.RespondentBankId).Select(x => x.Name).SingleAsync(ct) };
        statement.StatementNumber = $"STMT-{account.Id:N}-{StatementPolicy.MessageType(kind)[5..]}-{sequence:D8}-V{statement.Version}";
        statement.Entries = selected.Select(x => new AccountStatementEntry { LedgerEntryId = x.Id,
            PostingId = x.PostingId, CorrelationId = x.CorrelationId, WireTransferId = x.WireTransferId,
            Amount = x.Debit + x.Credit, IsCredit = x.Credit > 0, BookingAt = x.BookingAt,
            ValueDate = x.ValueDate, Reference = x.Reference }).ToList();
        statement.OpeningBalance = account.OpeningBalance + lines.Where(x => x.BookingAt < start).Sum(x => x.Credit - x.Debit);
        statement.TotalCredits = statement.Entries.Where(x => x.IsCredit).Sum(x => x.Amount);
        statement.TotalDebits = statement.Entries.Where(x => !x.IsCredit).Sum(x => x.Amount);
        statement.ClosingBalance = StatementPolicy.Closing(statement.OpeningBalance, statement.Entries);
        var expectedClosing = account.IssuerBalance - lines.Where(x => x.BookingAt > end || kind != StatementKind.Intraday && x.BookingAt == end).Sum(x => x.Credit - x.Debit);
        if (kind != StatementKind.Notification) statement.GenerationDifference = expectedClosing - statement.ClosingBalance;
        statement.XmlPayload = CamtStatementBuilder.Build(statement);
        db.AccountStatements.Add(statement);
        if (statement.GenerationDifference == 0)
        {
            var delivery = new StatementDelivery { StatementId = statement.Id, RecipientBankId = account.RespondentBankId, CreatedAt = now };
            db.StatementDeliveries.Add(delivery);
            db.OutboxMessages.Add(Outbox.Message(StatementQueues.Delivery, new StatementReady(statement.Id, account.RespondentBankId), account.RespondentBankId));
        }
        else
        {
            db.StatementReconciliations.Add(new StatementReconciliation { StatementId = statement.Id, BankId = account.IssuerBankId,
                ExpectedBalance = statement.ClosingBalance, ActualBalance = expectedClosing, Difference = statement.GenerationDifference,
                Status = StatementReconciliationStatus.Exception, CreatedAt = DateTimeOffset.UtcNow,
                FindingsJson = System.Text.Json.JsonSerializer.Serialize(new[] { new StatementFinding("Balance mismatch", statement.StatementNumber,
                    statement.GenerationDifference, "Issuer account position disagrees with its finalized ledger. Snapshot held; no delivery queued.") }) });
        }
        if (statement.GenerationDifference != 0)
            db.OperationsCases.Add(new OperationsCase { BankId = account.IssuerBankId, Rail = "Statements",
                CaseType = "Statement generation", Reference = "STMT:" + statement.Id.ToString("N"),
                Summary = "Issuer ledger validation failed; statement held from delivery.", DueDate = DateTimeOffset.UtcNow.AddDays(1) });
        return statement.Id;
    }

    public Task NotifyAsync(CorrespondentPostingNotice notice, CancellationToken ct) => StatementTransactions.Run(factory, async db => {
        var account = await db.CorrespondentAccounts.SingleAsync(x => x.Id == notice.AccountId, ct);
        if (await db.AccountStatements.AnyAsync(x => x.AccountId == account.Id && x.NotificationPostingId == notice.PostingId, ct)) return true;
        var source = await db.CorrespondentLedgerEntries.SingleAsync(x => x.AccountId == account.Id && x.BankId == account.IssuerBankId
            && x.PostingId == notice.PostingId && x.IsAccountLine, ct);
        var now = await StatementTransactions.Now(db, ct);
        // Notification is selected by stable posting ID, not by an invented timestamp ordering.
        var end = now > source.BookingAt ? now : source.BookingAt.AddTicks(1);
        if (end > now) end = now;
        await Generate(db, account, StatementKind.Notification, source.BookingAt.AddTicks(-1), end, null, notice.PostingId, ct);
        return true;
    }, ct);
}
