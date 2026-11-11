using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed record BankNetworkReconciliationRow(Guid CorrelationId, string Direction, decimal Amount,
    string SettlementReference, string Delivery, string Posting, string Accounting);

/// <summary>Only network facts involving the requesting bank and its own journals are disclosed.</summary>
public sealed class BankNetworkReconciliationService(IDbContextFactory<BankingDbContext> factory)
{
    public async Task<IReadOnlyList<BankNetworkReconciliationRow>> BuildAsync(Guid bankId, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var payments = await db.WireTransfers.AsNoTracking().Where(x => x.Direction == WireDirection.Outgoing
            && x.Rail == PaymentRail.Fedwire && (x.SenderBankId == bankId || x.ReceiverBankId == bankId))
            .Join(db.FedSettlements.Where(x => x.StatusCode == "ACSC"), x => x.CorrelationId, x => x.CorrelationId,
                (wire, settlement) => new { wire.CorrelationId, wire.Amount, wire.SenderBankId, settlement.Imad })
            .ToListAsync(token);
        var ownWires = await db.WireTransfers.AsNoTracking().Where(x => x.BankId == bankId && x.Rail == PaymentRail.Fedwire)
            .Select(x => new { x.Id, x.CorrelationId, x.Direction, x.Status }).ToListAsync(token);
        var obligationJournals = await db.FedFundsPaymentObligations.AsNoTracking()
            .Where(x => x.Trade.LenderBankId == bankId || x.Trade.BorrowerBankId == bankId)
            .Select(x => x.Trade.LenderBankId == bankId ? x.LenderJournalId : x.BorrowerJournalId).ToListAsync(token);
        var accrualJournals = await db.FedFundsAccruals.AsNoTracking()
            .Where(x => x.Trade.LenderBankId == bankId || x.Trade.BorrowerBankId == bankId)
            .Select(x => x.Trade.LenderBankId == bankId ? x.LenderJournalId : x.BorrowerJournalId).ToListAsync(token);
        var treasuryJournals = obligationJournals.Where(x => x.HasValue).Select(x => x!.Value).Concat(accrualJournals).ToList();
        var entries = await db.LedgerEntries.AsNoTracking().Where(x => x.WireTransfer.BankId == bankId
            && x.WireTransfer.Rail == PaymentRail.Fedwire && !treasuryJournals.Contains(x.JournalId))
            .Select(x => new { x.WireTransferId, x.JournalId, x.Debit, x.Credit }).ToListAsync(token);
        return payments.Select(payment =>
        {
            var outgoing = payment.SenderBankId == bankId;
            var direction = outgoing ? WireDirection.Outgoing : WireDirection.Incoming;
            var wires = ownWires.Where(x => x.CorrelationId == payment.CorrelationId && x.Direction == direction).ToList();
            var wire = wires.FirstOrDefault();
            var lines = entries.Where(x => wires.Any(w => w.Id == x.WireTransferId)).ToList();
            var accounting = lines.Count == 0 ? "Missing journal" :
                lines.Select(x => x.JournalId).Distinct().Count() != 1 ? "Review multiple journals" :
                !JournalControl.IsBalanced(lines.Select(x => new JournalLine(x.Debit, x.Credit))) ? "Unbalanced journal" :
                lines.Sum(x => x.Debit) != payment.Amount ? "Amount mismatch" : "Balanced";
            return new BankNetworkReconciliationRow(payment.CorrelationId, direction.ToString(), payment.Amount,
                payment.Imad, outgoing ? "See outgoing delivery timeline" : wires.Count == 0 ? "Awaiting delivery" : "Received",
                wire is null ? "Awaiting delivery" : wire.Status == WireStatus.Received ? "Awaiting beneficiary posting / review" : wire.Status.ToString(), accounting);
        }).OrderBy(x => x.Direction).ThenBy(x => x.CorrelationId).ToList();
    }
}
