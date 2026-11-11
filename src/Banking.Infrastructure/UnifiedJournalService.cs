using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed class UnifiedJournalService(IDbContextFactory<BankingDbContext> dbFactory)
{
    public async Task<IReadOnlyList<UnifiedJournalLine>> ReadAsync(Guid bankId,
        CancellationToken token = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var wires = await db.LedgerEntries.AsNoTracking().Where(x => x.WireTransfer.BankId == bankId)
            .Select(x => new UnifiedJournalLine(x.JournalId, x.WireTransfer.Rail, x.WireTransferId,
                x.AccountCode, x.AccountName, x.Debit, x.Credit, x.CreatedDate)).ToListAsync(token);
        var ach = await db.AchLedgerEntries.AsNoTracking()
            .Where(x => x.Entry.OriginatingBankId == bankId || x.Entry.ReceivingBankId == bankId)
            .Select(x => new UnifiedJournalLine(x.JournalId, PaymentRail.Ach, x.AchEntryId,
                x.AccountCode, x.AccountName, x.Debit, x.Credit, x.CreatedDate)).ToListAsync(token);
        var checks = await db.CheckLedgerEntries.AsNoTracking()
            .Where(x => x.CheckDeposit.DepositoryBankId == bankId || x.CheckDeposit.PayingBankId == bankId)
            .Select(x => new UnifiedJournalLine(x.JournalId, PaymentRail.Check, x.CheckDepositId,
                x.AccountCode, x.AccountName, x.Debit, x.Credit, x.CreatedDate)).ToListAsync(token);
        var correspondent = await db.CorrespondentLedgerEntries.AsNoTracking().Where(x => x.BankId == bankId)
            .Select(x => new UnifiedJournalLine(x.JournalId, PaymentRail.CorrespondentBook, x.PostingId,
                x.AccountCode, x.AccountCode, x.Debit, x.Credit, x.BookingAt)).ToListAsync(token);
        return wires.Concat(ach).Concat(checks).Concat(correspondent).OrderBy(x => x.PostedDate).ToList();
    }
}
