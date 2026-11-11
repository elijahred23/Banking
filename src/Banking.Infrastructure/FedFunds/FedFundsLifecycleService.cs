using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public interface IFedFundsAccrualService
{
    Task ProcessAsync(CancellationToken ct);
}

public sealed partial class FedFundsTradeService : IFedFundsAccrualService
{
    // Recovery reads durable obligations, settlement and bank accounting; no broker acknowledgement is treated as cash.
    public Task ProcessAsync(CancellationToken ct) => ProcessScopeAsync(null, ct);
    public Task ProcessBankAsync(Guid bankId, CancellationToken ct) => ProcessScopeAsync(bankId, ct);
    private async Task ProcessScopeAsync(Guid? bankId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var ids = await db.FedFundsTrades.Where(x => (bankId == null || x.LenderBankId == bankId || x.BorrowerBankId == bankId)
            && (x.Status == FedFundsTradeStatus.FundingPending
            || x.Status == FedFundsTradeStatus.Active || x.Status == FedFundsTradeStatus.MaturityDue
            || x.Status == FedFundsTradeStatus.Overdue)).Select(x => x.Id).ToListAsync(ct);
        foreach (var id in ids)
            await Transaction(async context => {
                var trade = await Trades(context).SingleAsync(x => x.Id == id, ct);
                var today = await Today(context, ct);
                await ReconcilePayments(context, trade, ct);
                if (trade.Status == FedFundsTradeStatus.FundingPending)
                    await SubmitFunding(context, trade, today, "treasury-worker", ct);
                if (trade.Status is FedFundsTradeStatus.Active or FedFundsTradeStatus.MaturityDue or FedFundsTradeStatus.Overdue)
                {
                    await Accrue(context, trade, today, ct);
                    if (today >= trade.MaturityDate && trade.Status == FedFundsTradeStatus.Active)
                    {
                        FedFundsPolicy.Transition(trade, FedFundsTradeStatus.MaturityDue);
                        trade.Obligations.Add(new FedFundsPaymentObligation { Type = FedFundsObligationType.Principal,
                            Amount = trade.Principal, DueDate = trade.MaturityDate });
                        var interest = trade.Accruals.Sum(x => x.Amount);
                        trade.Obligations.Add(new FedFundsPaymentObligation { Type = FedFundsObligationType.Interest,
                            Amount = interest, DueDate = trade.MaturityDate,
                            Status = interest == 0 ? FedFundsObligationStatus.Settled : FedFundsObligationStatus.Pending });
                        Audit(trade, "MaturityDue", "treasury-worker", "Principal and ACT/360 interest are independent repayment obligations.");
                    }
                    if (today > trade.MaturityDate && trade.Status == FedFundsTradeStatus.MaturityDue)
                    {
                        FedFundsPolicy.Transition(trade, FedFundsTradeStatus.Overdue);
                        Audit(trade, "Overdue", "treasury-worker", "Maturity passed without confirmed repayment. Existing submitted instructions remain in progress; no duplicate instruction created.");
                    }
                    if (trade.Status is FedFundsTradeStatus.MaturityDue or FedFundsTradeStatus.Overdue)
                        await SubmitRepayments(context, trade, today, "treasury-worker", ct);
                }
                return true;
            }, ct);
    }

    private async Task SubmitFunding(BankingDbContext db, FedFundsTrade trade, DateOnly today, string actor, CancellationToken ct)
    {
        var obligation = trade.Obligations.Single(x => x.Type == FedFundsObligationType.Funding);
        if (obligation.Status != FedFundsObligationStatus.Pending || today < trade.ValueDate) return;
        var sender = await Eligible(db, trade.LenderBankId, ct);
        var line = await Credit(db, trade, ct);
        if (today > trade.ValueDate || !trade.CreditReserved || !line.IsActive || sender.MasterAccountBalance < trade.Principal)
        {
            obligation.Status = FedFundsObligationStatus.Failed;
            FedFundsPolicy.Transition(trade, FedFundsTradeStatus.FundingException);
            Audit(trade, "FundingException", actor, "Funding date, active credit line, or reserve liquidity check failed. No active loan or Treasury journal created; committed exposure remains until retry or cancellation.");
            return;
        }
        await Submit(db, trade, obligation, trade.LenderBankId, trade.BorrowerBankId, actor, ct);
    }
    private static async Task Submit(BankingDbContext db, FedFundsTrade trade, FedFundsPaymentObligation obligation,
        Guid senderId, Guid receiverId, string actor, CancellationToken ct)
    {
        if (obligation.Status != FedFundsObligationStatus.Pending || obligation.Amount == 0) return;
        var sender = await Eligible(db, senderId, ct);
        var receiver = await Eligible(db, receiverId, ct);
        obligation.Attempts++;
        obligation.CorrelationId = StableId(obligation.Id, obligation.Attempts, "correlation");
        var wire = new WireTransfer { Id = StableId(obligation.Id, obligation.Attempts, "wire"),
            CorrelationId = obligation.CorrelationId, BankId = senderId, SenderBankId = senderId, ReceiverBankId = receiverId,
            CreatedDate = (await db.SimulationStates.SingleAsync(x => x.Name == "default", ct)).CurrentUtc,
            Amount = obligation.Amount, Direction = WireDirection.Outgoing, Rail = PaymentRail.Fedwire,
            TransferType = WireTransferType.FinancialInstitutionCreditTransfer, Status = WireStatus.Created,
            SenderName = sender.Name, ReceiverName = receiver.Name, BeneficiaryAccountNumber = receiver.FedParticipantId,
            CreatedBy = $"fedfunds:{trade.Id:N}", ApprovedBy = "bilateral-treasury-approvals", ApprovedDate = DateTimeOffset.UtcNow,
            CustomerReference = $"FF-{obligation.Id:N}"[..27] + $"-{obligation.Attempts}",
            Events = [new WireEvent { EventType = "Created", Description = $"Fed funds {obligation.Type}; trade {trade.Id}; obligation {obligation.Id}; attempt {obligation.Attempts}." }] };
        db.WireTransfers.Add(wire);
        obligation.WireTransferId = wire.Id;
        obligation.Status = FedFundsObligationStatus.Submitted;
        db.OutboxMessages.Add(Outbox.Message(Queues.WireCreated, new WireCreated(wire.Id), senderId));
        Audit(trade, "PaymentSubmitted", actor, $"{obligation.Type} obligation {obligation.Id}: {obligation.Amount:F4}; correlation {obligation.CorrelationId}. No cash posting until final settlement.");
    }
    private static async Task SubmitRepayments(BankingDbContext db, FedFundsTrade trade, DateOnly today, string actor, CancellationToken ct)
    {
        if (today < trade.MaturityDate) return;
        var pending = trade.Obligations.Where(x => x.Type != FedFundsObligationType.Funding && x.Status == FedFundsObligationStatus.Pending).ToList();
        if (pending.Count == 0) return;
        var borrower = await Eligible(db, trade.BorrowerBankId, ct);
        if (borrower.MasterAccountBalance < pending.Sum(x => x.Amount))
        {
            foreach (var obligation in pending) obligation.Status = FedFundsObligationStatus.Failed;
            FedFundsPolicy.Transition(trade, FedFundsTradeStatus.Overdue);
            Audit(trade, "Overdue", actor, "Insufficient borrower reserves at maturity. Principal and interest liabilities remain outstanding; no original settlement is reversed.");
            return;
        }
        foreach (var obligation in pending)
            await Submit(db, trade, obligation, trade.BorrowerBankId, trade.LenderBankId, actor, ct);
    }
    private static async Task ReconcilePayments(BankingDbContext db, FedFundsTrade trade, CancellationToken ct)
    {
        foreach (var obligation in trade.Obligations.Where(x => x.Status == FedFundsObligationStatus.Submitted).ToList())
        {
            var wire = await db.WireTransfers.SingleAsync(x => x.Id == obligation.WireTransferId, ct);
            if (wire.Status == WireStatus.Rejected)
            {
                obligation.Status = FedFundsObligationStatus.Failed;
                FedFundsPolicy.Transition(trade, obligation.Type == FedFundsObligationType.Funding ? FedFundsTradeStatus.FundingException : FedFundsTradeStatus.Overdue);
                Audit(trade, "PaymentFailed", "treasury-worker", $"{obligation.Type} attempt {obligation.Attempts} failed. Obligation retained; explicit retry creates a new correlated attempt.");
                continue;
            }
            if (wire.Status != WireStatus.Settled || !await db.FedSettlements.AnyAsync(x => x.CorrelationId == obligation.CorrelationId && x.StatusCode == "ACSC", ct)) continue;
            var incoming = await db.WireTransfers.SingleOrDefaultAsync(x => x.CorrelationId == obligation.CorrelationId
                && x.BankId == wire.ReceiverBankId && x.Direction == WireDirection.Incoming && x.Status == WireStatus.Completed, ct);
            if (incoming is null || !await db.LedgerEntries.AnyAsync(x => x.WireTransferId == wire.Id, ct)
                || !await db.LedgerEntries.AnyAsync(x => x.WireTransferId == incoming.Id, ct)) continue;
            var lenderWire = obligation.Type == FedFundsObligationType.Funding ? wire : incoming;
            var borrowerWire = obligation.Type == FedFundsObligationType.Funding ? incoming : wire;
            obligation.LenderJournalId = Guid.NewGuid();
            obligation.BorrowerJournalId = Guid.NewGuid();
            var lenderClearing = $"FI_CLEARING:{trade.BorrowerBankId:N}";
            var borrowerClearing = $"FI_CLEARING:{trade.LenderBankId:N}";
            if (obligation.Type == FedFundsObligationType.Funding)
            {
                Journal(db, lenderWire.Id, obligation.LenderJournalId.Value, "FF_SOLD", lenderClearing, obligation.Amount, trade.Id, "Funding");
                Journal(db, borrowerWire.Id, obligation.BorrowerJournalId.Value, borrowerClearing, "FF_PURCHASED", obligation.Amount, trade.Id, "Funding");
                FedFundsPolicy.Transition(trade, FedFundsTradeStatus.Active);
            }
            else
            {
                var principal = obligation.Type == FedFundsObligationType.Principal;
                Journal(db, lenderWire.Id, obligation.LenderJournalId.Value, lenderClearing, principal ? "FF_SOLD" : "FF_INTEREST_RECEIVABLE", obligation.Amount, trade.Id, obligation.Type.ToString());
                Journal(db, borrowerWire.Id, obligation.BorrowerJournalId.Value, principal ? "FF_PURCHASED" : "FF_INTEREST_PAYABLE", borrowerClearing, obligation.Amount, trade.Id, obligation.Type.ToString());
            }
            obligation.Status = FedFundsObligationStatus.Settled;
            Audit(trade, "PaymentSettled", "treasury-worker", $"{obligation.Type} settled and both banks' journals confirmed. Correlation {obligation.CorrelationId}.");
        }
        var repayments = trade.Obligations.Where(x => x.Type != FedFundsObligationType.Funding).ToList();
        if (trade.Status is FedFundsTradeStatus.MaturityDue or FedFundsTradeStatus.Overdue
            && repayments.Count == 2 && repayments.All(x => x.Status == FedFundsObligationStatus.Settled))
        {
            FedFundsPolicy.Transition(trade, FedFundsTradeStatus.Repaid);
            await ReleaseCredit(db, trade, ct);
            Audit(trade, "Repaid", "treasury-worker", "Principal and interest settled and posted. Counterparty exposure released.");
        }
    }
    private static async Task Accrue(BankingDbContext db, FedFundsTrade trade, DateOnly through, CancellationToken ct)
    {
        var funding = trade.Obligations.Single(x => x.Type == FedFundsObligationType.Funding);
        var fundingWires = await db.WireTransfers.Where(x => x.CorrelationId == funding.CorrelationId).ToListAsync(ct);
        var lenderWire = fundingWires.Single(x => x.BankId == trade.LenderBankId && x.Direction == WireDirection.Outgoing);
        var borrowerWire = fundingWires.Single(x => x.BankId == trade.BorrowerBankId && x.Direction == WireDirection.Incoming);
        var end = through < trade.MaturityDate ? through : trade.MaturityDate;
        for (var day = trade.ValueDate; day < end; day = day.AddDays(1))
        {
            if (trade.Accruals.Any(x => x.AccrualDate == day)) continue;
            // Difference of rounded cumulative totals avoids rounding drift across repeated daily jobs.
            var amount = FedFundsInterestCalculator.Calculate(trade.Principal, trade.AnnualRate, trade.ValueDate, day.AddDays(1))
                - FedFundsInterestCalculator.Calculate(trade.Principal, trade.AnnualRate, trade.ValueDate, day);
            var accrual = new FedFundsAccrual { AccrualDate = day, Amount = amount,
                LenderJournalId = Guid.NewGuid(), BorrowerJournalId = Guid.NewGuid() };
            if (amount > 0)
            {
                Journal(db, lenderWire.Id, accrual.LenderJournalId, "FF_INTEREST_RECEIVABLE", "FF_INTEREST_INCOME", amount, trade.Id, $"Accrual {day}");
                Journal(db, borrowerWire.Id, accrual.BorrowerJournalId, "FF_INTEREST_EXPENSE", "FF_INTEREST_PAYABLE", amount, trade.Id, $"Accrual {day}");
            }
            trade.Accruals.Add(accrual);
            Audit(trade, "Accrued", "treasury-worker", $"ACT/360 {day}: {amount:F4}; no cash movement.");
        }
    }
    private static void Journal(BankingDbContext db, Guid wireId, Guid journalId, string debit, string credit,
        decimal amount, Guid tradeId, string purpose) => db.LedgerEntries.AddBalanced(
            new LedgerEntry { JournalId = journalId, WireTransferId = wireId, AccountCode = debit, AccountName = debit,
                Debit = amount, Description = $"Fed funds {tradeId}: {purpose}" },
            new LedgerEntry { JournalId = journalId, WireTransferId = wireId, AccountCode = credit, AccountName = credit,
                Credit = amount, Description = $"Fed funds {tradeId}: {purpose}" });
}
