using System.Data;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

/// <summary>Receiving-bank posting is independent of final network settlement.</summary>
public sealed class IncomingWirePostingService(IDbContextFactory<BankingDbContext> factory)
{
    public async Task<string?> PostAsync(Guid bankId, Guid wireId, string operatorName, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var wire = await db.WireTransfers.SingleOrDefaultAsync(x => x.Id == wireId
                && x.BankId == bankId && x.ReceiverBankId == bankId
                && x.Direction == WireDirection.Incoming && x.Rail == PaymentRail.Fedwire, token);
            if (wire is null) return "Incoming wire was not found at this bank.";
            if (wire.Status == WireStatus.Completed) return null;
            if (wire.Status != WireStatus.Received || wire.TransferType != WireTransferType.CustomerCreditTransfer)
                return "This wire is not awaiting beneficiary posting.";
            if (!await db.FedSettlements.AnyAsync(x => x.CorrelationId == wire.CorrelationId && x.StatusCode == "ACSC", token))
                return "Final network settlement is required before beneficiary posting.";
            var account = await db.Accounts.Include(x => x.Customer).SingleOrDefaultAsync(x =>
                x.Customer.BankId == bankId && x.AccountNumber == wire.BeneficiaryAccountNumber, token);
            if (account is null || account.Status != AccountStatus.Open)
            {
                var reason = account is null ? "Beneficiary account was not found at this bank."
                    : "Beneficiary account is not open.";
                db.WireEvents.Add(new WireEvent { WireTransferId = wire.Id, EventType = "BeneficiaryException",
                    Description = $"{reason} Reviewed by {operatorName}. Network settlement remains final." });
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return reason + " The settled payment remains in review.";
            }
            IncomingWirePostingPolicy.Post(wire, account, bankId);
            BalanceReceipts.Customer(db, bankId, wire.CorrelationId, wire.Rail, account, wire.Amount);
            var journal = Guid.NewGuid();
            db.LedgerEntries.AddBalanced(
                new LedgerEntry { JournalId = journal, WireTransferId = wire.Id,
                    AccountCode = $"FEDMASTER:{bankId:N}", AccountName = "Settlement cash",
                    Debit = wire.Amount, Description = "Debit settled incoming cash" },
                new LedgerEntry { JournalId = journal, WireTransferId = wire.Id,
                    AccountCode = $"CUSTOMER:{account.AccountNumber}", AccountName = "Customer deposits",
                    Credit = wire.Amount, Description = "Credit beneficiary deposit liability" });
            db.WireEvents.Add(new WireEvent { WireTransferId = wire.Id, EventType = "Posted",
                Description = $"Beneficiary credit posted by {operatorName}; network settlement remains final." });
            db.OutboxMessages.Add(Outbox.Message(Queues.WireIncomingReceived,
                new { wire.Id, wire.CorrelationId }, bankId));
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return null;
        });
    }
}
