using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed record PaymentReconciliationResult(
    int PendingOutboxMessages,
    int DeadLetterMessages,
    int IncompleteInboxMessages,
    int UnbalancedJournals,
    int HoldMismatches,
    int SettledWiresWithoutJournals)
{
    public bool IsClear => PendingOutboxMessages == 0 && DeadLetterMessages == 0
        && IncompleteInboxMessages == 0 && UnbalancedJournals == 0
        && HoldMismatches == 0 && SettledWiresWithoutJournals == 0;
}

public sealed class PaymentReconciliationService(IDbContextFactory<BankingDbContext> dbFactory)
{
    public async Task<PaymentReconciliationResult> ReconcileAsync(Guid bankId, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var pendingOutbox = await db.OutboxMessages.CountAsync(x => x.BankId == bankId
            && x.PublishedDate == null, token);
        var deadLetters = await db.DeadLetterMessages.CountAsync(x => x.BankId == bankId
            && x.ReplayedDate == null, token);
        var incompleteInbox = await db.ConsumerInboxMessages.CountAsync(x => x.BankId == bankId
            && x.CompletedDate == null, token);

        var wireLines = await db.LedgerEntries.AsNoTracking()
            .Where(x => x.WireTransfer.BankId == bankId)
            .Select(x => new { x.JournalId, x.Debit, x.Credit }).ToListAsync(token);
        var achLines = await db.AchLedgerEntries.AsNoTracking()
            .Where(x => x.Entry.OriginatingBankId == bankId || x.Entry.ReceivingBankId == bankId)
            .Select(x => new { x.JournalId, x.Debit, x.Credit }).ToListAsync(token);
        var checkLines = await db.CheckLedgerEntries.AsNoTracking()
            .Where(x => x.CheckDeposit.DepositoryBankId == bankId || x.CheckDeposit.PayingBankId == bankId)
            .Select(x => new { x.JournalId, x.Debit, x.Credit }).ToListAsync(token);
        var unbalanced = wireLines.GroupBy(x => x.JournalId)
            .Count(x => !JournalControl.IsBalanced(x.Select(y => new JournalLine(y.Debit, y.Credit))))
            + achLines.GroupBy(x => x.JournalId)
                .Count(x => !JournalControl.IsBalanced(x.Select(y => new JournalLine(y.Debit, y.Credit))))
            + checkLines.GroupBy(x => x.JournalId)
                .Count(x => !JournalControl.IsBalanced(x.Select(y => new JournalLine(y.Debit, y.Credit))));

        var accounts = await db.Accounts.AsNoTracking().Where(x => x.Customer.BankId == bankId)
            .Select(x => new { x.Id, x.HeldBalance }).ToListAsync(token);
        var wireHolds = await db.WireTransfers.AsNoTracking().Where(x => x.BankId == bankId
                && x.Direction == WireDirection.Outgoing && x.FromAccountId != null
                && x.TransferType == WireTransferType.CustomerCreditTransfer
                && (x.Status == WireStatus.Validated || x.Status == WireStatus.ReadyForFed
                    || x.Status == WireStatus.SentToFed || x.Status == WireStatus.PendingAtFed))
            .GroupBy(x => x.FromAccountId!.Value)
            .Select(x => new { AccountId = x.Key, Amount = x.Sum(y => y.Amount) }).ToListAsync(token);
        var achHolds = await db.AchEntries.AsNoTracking().Where(x => x.OriginatingBankId == bankId
                && (x.TransactionCode == AchTransactionCode.CheckingCredit
                    || x.TransactionCode == AchTransactionCode.SavingsCredit)
                && (x.Status == AchEntryStatus.Validated || x.Status == AchEntryStatus.Batched
                    || x.Status == AchEntryStatus.SentToOperator))
            .GroupBy(x => x.OriginatingAccountId)
            .Select(x => new { AccountId = x.Key, Amount = x.Sum(y => y.Amount) }).ToListAsync(token);
        var expectedHolds = wireHolds.Concat(achHolds).GroupBy(x => x.AccountId)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Amount));
        var holdMismatches = accounts.Count(x => x.HeldBalance
            != expectedHolds.GetValueOrDefault(x.Id));

        var settledWithoutJournal = await db.WireTransfers.CountAsync(x => x.BankId == bankId
            && (x.Status == WireStatus.Settled || x.Status == WireStatus.Completed)
            && !db.LedgerEntries.Any(y => y.WireTransferId == x.Id), token);

        return new PaymentReconciliationResult(pendingOutbox, deadLetters, incompleteInbox,
            unbalanced, holdMismatches, settledWithoutJournal);
    }
}
