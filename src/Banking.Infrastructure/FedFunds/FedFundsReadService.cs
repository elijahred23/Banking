using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed record FedFundsPosition(Guid TradeId, string Counterparty, string Side, FedFundsTradeStatus Status,
    decimal Principal, decimal Rate, DateOnly Maturity, decimal OutstandingPrincipal, decimal AccruedInterest);
public sealed record FedFundsReconciliation(Guid TradeId, decimal ExpectedPrincipal, decimal JournalPrincipal,
    decimal ExpectedInterest, decimal JournalInterest, decimal NetworkCash, decimal JournalCash, bool JournalsBalanced)
{
    public bool IsClear => ExpectedPrincipal == JournalPrincipal && ExpectedInterest == JournalInterest
        && NetworkCash == JournalCash && JournalsBalanced;
}
public sealed class FedFundsReadService(IDbContextFactory<BankingDbContext> factory)
{
    public async Task<IReadOnlyList<FedFundsPosition>> PositionsAsync(Guid bankId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var trades = await db.FedFundsTrades.AsNoTracking().Where(x => x.LenderBankId == bankId || x.BorrowerBankId == bankId)
            .Include(x => x.Obligations).Include(x => x.Accruals).AsSplitQuery().OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        var names = await db.Banks.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        return trades.Select(t => new FedFundsPosition(t.Id, names[t.LenderBankId == bankId ? t.BorrowerBankId : t.LenderBankId],
            t.LenderBankId == bankId ? "Sold / lender" : "Purchased / borrower", t.Status, t.Principal, t.AnnualRate,
            t.MaturityDate, Outstanding(t), t.Accruals.Sum(x => x.Amount))).ToList();
    }
    public async Task<FedFundsLiquidity> LiquidityAsync(Guid bankId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var reserves = await db.Banks.Where(x => x.Id == bankId).Select(x => x.MasterAccountBalance).SingleAsync(ct);
        var trades = await db.FedFundsTrades.AsNoTracking().Where(x => x.LenderBankId == bankId || x.BorrowerBankId == bankId)
            .Include(x => x.Obligations).AsSplitQuery().ToListAsync(ct);
        var correlations = trades.SelectMany(x => x.Obligations).Select(x => x.CorrelationId).ToList();
        var settled = await db.FedSettlements.Where(x => correlations.Contains(x.CorrelationId) && x.StatusCode == "ACSC")
            .Select(x => x.CorrelationId).ToListAsync(ct);
        decimal inflows = 0, outflows = 0;
        foreach (var trade in trades)
        {
            if (trade.Status == FedFundsTradeStatus.FundingPending)
            {
                var funding = trade.Obligations.Single(x => x.Type == FedFundsObligationType.Funding);
                if (!settled.Contains(funding.CorrelationId))
                {
                    if (trade.LenderBankId == bankId) outflows += funding.Amount; else inflows += funding.Amount;
                }
            }
            if (trade.Status is not (FedFundsTradeStatus.Active or FedFundsTradeStatus.MaturityDue or FedFundsTradeStatus.Overdue)) continue;
            var repayments = trade.Obligations.Where(x => x.Type != FedFundsObligationType.Funding).ToList();
            var amount = repayments.Count == 0 ? trade.Principal + FedFundsInterestCalculator.Calculate(trade.Principal, trade.AnnualRate, trade.ValueDate, trade.MaturityDate)
                : repayments.Where(x => x.Status != FedFundsObligationStatus.Settled && !settled.Contains(x.CorrelationId)).Sum(x => x.Amount);
            if (trade.BorrowerBankId == bankId) outflows += amount; else inflows += amount;
        }
        return new(reserves, inflows, outflows);
    }

    private static decimal Outstanding(FedFundsTrade t) =>
        t.Obligations.Any(x => x.Type == FedFundsObligationType.Funding && x.Status == FedFundsObligationStatus.Settled)
        && !t.Obligations.Any(x => x.Type == FedFundsObligationType.Principal && x.Status == FedFundsObligationStatus.Settled) ? t.Principal : 0;

    public async Task<IReadOnlyList<FedFundsReconciliation>> ReconcileAsync(Guid bankId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var trades = await db.FedFundsTrades.AsNoTracking().Where(x => x.LenderBankId == bankId || x.BorrowerBankId == bankId)
            .Include(x => x.Obligations).Include(x => x.Accruals).AsSplitQuery().ToListAsync(ct);
        var ownLines = await db.LedgerEntries.AsNoTracking().Where(x => x.WireTransfer.BankId == bankId)
            .Select(x => new { x.WireTransfer.CorrelationId, x.JournalId, x.AccountCode, x.Debit, x.Credit }).ToListAsync(ct);
        var network = await db.BalanceReceipts.AsNoTracking().Where(x => x.BankId == bankId && x.AccountCode == $"BANK:{bankId:N}")
            .Select(x => new { CorrelationId = x.PaymentKey, x.Delta }).ToListAsync(ct);
        var result = new List<FedFundsReconciliation>();
        foreach (var trade in trades)
        {
            var lender = trade.LenderBankId == bankId;
            // Include all immutable attempts, including prior rejections, not only the current obligation correlation.
            var obligationIds = trade.Obligations.Select(x => x.Id).ToList();
            var references = obligationIds.Select(x => $"FF-{x:N}"[..27]).ToList();
            var wireRefs = await db.WireTransfers.AsNoTracking().Where(x => x.Direction == WireDirection.Outgoing
                && x.CustomerReference != null && references.Contains(x.CustomerReference.Substring(0, 27)))
                .Select(x => x.CorrelationId).ToListAsync(ct);
            var lines = ownLines.Where(x => wireRefs.Contains(x.CorrelationId)).ToList();
            var journalPrincipal = lender ? lines.Where(x => x.AccountCode == "FF_SOLD").Sum(x => x.Debit - x.Credit)
                : lines.Where(x => x.AccountCode == "FF_PURCHASED").Sum(x => x.Credit - x.Debit);
            var journalInterest = lender ? lines.Where(x => x.AccountCode == "FF_INTEREST_RECEIVABLE").Sum(x => x.Debit - x.Credit)
                : lines.Where(x => x.AccountCode == "FF_INTEREST_PAYABLE").Sum(x => x.Credit - x.Debit);
            var expectedInterest = trade.Accruals.Sum(x => x.Amount) - trade.Obligations.Where(x =>
                x.Type == FedFundsObligationType.Interest && x.Status == FedFundsObligationStatus.Settled).Sum(x => x.Amount);
            var expectedTreasuryJournals = trade.Obligations.Select(x => lender ? x.LenderJournalId : x.BorrowerJournalId)
                .Where(x => x.HasValue).Select(x => x!.Value)
                .Concat(trade.Accruals.Where(x => x.Amount > 0).Select(x => lender ? x.LenderJournalId : x.BorrowerJournalId)).ToHashSet();
            var observedTreasuryJournals = lines.Where(x => x.AccountCode.StartsWith("FF_", StringComparison.Ordinal))
                .Select(x => x.JournalId).ToHashSet();
            result.Add(new(trade.Id, Outstanding(trade), journalPrincipal, expectedInterest, journalInterest,
                network.Where(x => wireRefs.Contains(x.CorrelationId)).Sum(x => x.Delta),
                lines.Where(x => x.AccountCode == $"FEDMASTER:{bankId:N}").Sum(x => x.Debit - x.Credit),
                expectedTreasuryJournals.SetEquals(observedTreasuryJournals)
                    && lines.GroupBy(x => x.JournalId).All(g => g.Count() == 2 && JournalControl.IsBalanced(g.Select(x => new JournalLine(x.Debit, x.Credit))))));
        }
        return result;
    }
}

public sealed record FedFundsLiquidity(decimal ReserveBalance, decimal ExpectedInflows, decimal ExpectedOutflows)
{
    public decimal ProjectedReserveBalance => ReserveBalance + ExpectedInflows - ExpectedOutflows;
}
