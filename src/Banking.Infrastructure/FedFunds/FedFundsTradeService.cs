using System.Data;
using System.Security.Cryptography;
using System.Text;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Banking.Infrastructure;

public sealed record FedFundsActor(Guid BankId, string OperatorId);
public sealed record ProposeFedFundsTradeRequest(Guid CounterpartyBankId, bool Borrow, decimal Principal,
    decimal AnnualRate, DateOnly ValueDate);

public interface IFedFundsTradeService
{
    Task<Guid> ProposeTradeAsync(FedFundsActor actor, ProposeFedFundsTradeRequest request, CancellationToken ct);
    Task AcceptTradeAsync(FedFundsActor actor, Guid tradeId, bool accept, CancellationToken ct);
    Task ApproveTradeAsync(FedFundsActor actor, Guid tradeId, bool approve, CancellationToken ct);
    Task RequestFundingAsync(FedFundsActor actor, Guid tradeId, CancellationToken ct);
}

public sealed partial class FedFundsTradeService(IDbContextFactory<BankingDbContext> factory,
    IOptions<FedFundsOptions> options) : IFedFundsTradeService
{
    public async Task<Guid> ProposeTradeAsync(FedFundsActor actor, ProposeFedFundsTradeRequest request, CancellationToken ct) =>
        await Transaction(async db => {
            await Eligible(db, actor.BankId, ct);
            await Eligible(db, request.CounterpartyBankId, ct);
            var today = await Today(db, ct);
            var trade = new FedFundsTrade { LenderBankId = request.Borrow ? request.CounterpartyBankId : actor.BankId,
                BorrowerBankId = request.Borrow ? actor.BankId : request.CounterpartyBankId,
                ProposalBankId = actor.BankId, CreatedBy = actor.OperatorId, Principal = request.Principal,
                AnnualRate = request.AnnualRate, ValueDate = request.ValueDate,
                MaturityDate = FedFundsPolicy.NextBusinessDay(request.ValueDate, options.Value.Holidays) };
            FedFundsPolicy.Validate(trade, options.Value.Holidays);
            if (trade.ValueDate < today || trade.ValueDate > today.AddDays(31))
                throw new InvalidOperationException("Value date must be today or within the next 31 simulated days.");
            db.FedFundsTrades.Add(trade);
            Audit(trade, "Proposed", actor.OperatorId, "Unsecured bilateral USD overnight loan proposed. Agreed rate is independent of the synthetic reference.");
            return trade.Id;
        }, ct);

    public Task AcceptTradeAsync(FedFundsActor actor, Guid tradeId, bool accept, CancellationToken ct) =>
        Transaction(async db => {
            var trade = await Load(db, actor.BankId, tradeId, ct);
            if (trade.ProposalBankId == actor.BankId) throw new InvalidOperationException("Only the counterparty can accept the proposal.");
            if (trade.Status != FedFundsTradeStatus.Proposed) throw new InvalidOperationException("Proposal has already been answered.");
            trade.AcceptedBy = actor.OperatorId;
            FedFundsPolicy.Transition(trade, accept ? FedFundsTradeStatus.PendingApprovals : FedFundsTradeStatus.Rejected);
            Audit(trade, accept ? "Accepted" : "Rejected", actor.OperatorId, "Counterparty decision recorded; neither reserves nor accounting move on acceptance.");
            return true;
        }, ct);

    public Task ApproveTradeAsync(FedFundsActor actor, Guid tradeId, bool approve, CancellationToken ct) =>
        Transaction(async db => {
            var trade = await Load(db, actor.BankId, tradeId, ct);
            if (trade.Status != FedFundsTradeStatus.PendingApprovals) throw new InvalidOperationException("Trade is not awaiting approvals.");
            var maker = trade.ProposalBankId == actor.BankId ? trade.CreatedBy : trade.AcceptedBy;
            if (string.Equals(maker, actor.OperatorId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Each bank requires an approver different from its maker.");
            if (trade.Approvals.Any(x => x.BankId == actor.BankId)) return true;
            trade.Approvals.Add(new FedFundsApproval { BankId = actor.BankId, OperatorId = actor.OperatorId, Approved = approve });
            Audit(trade, approve ? "Approved" : "ApprovalRejected", actor.OperatorId, $"Decision for bank {actor.BankId}.");
            if (!approve) FedFundsPolicy.Transition(trade, FedFundsTradeStatus.Rejected);
            else if (trade.Approvals.Count(x => x.Approved) == 2)
            {
                var line = await Credit(db, trade, ct);
                FedFundsPolicy.Reserve(line, trade.Principal);
                trade.CreditReserved = true;
                trade.Obligations.Add(new FedFundsPaymentObligation { Type = FedFundsObligationType.Funding,
                    Amount = trade.Principal, DueDate = trade.ValueDate });
                FedFundsPolicy.Transition(trade, FedFundsTradeStatus.FundingPending);
                Audit(trade, "CreditReserved", actor.OperatorId, "Both banks approved. Exposure is reserved atomically; funding still requires final Fedwire settlement.");
            }
            return true;
        }, ct);

    public Task SetCreditLineAsync(FedFundsActor actor, Guid borrowerId, decimal limit, bool active, CancellationToken ct) =>
        Transaction(async db => {
            await Eligible(db, actor.BankId, ct);
            await Eligible(db, borrowerId, ct);
            if (actor.BankId == borrowerId || limit < 0 || decimal.Round(limit, 4) != limit)
                throw new InvalidOperationException("Invalid lender counterparty limit.");
            var line = await db.FedFundsCreditLines.SingleOrDefaultAsync(x => x.LenderBankId == actor.BankId && x.BorrowerBankId == borrowerId, ct);
            if (line is null) db.FedFundsCreditLines.Add(line = new FedFundsCreditLine { LenderBankId = actor.BankId, BorrowerBankId = borrowerId });
            if (limit < line.ReservedAmount) throw new InvalidOperationException("The limit cannot be reduced below committed exposure.");
            line.ApprovedLimit = limit;
            line.IsActive = active;
            return true;
        }, ct);

    public Task PublishQuoteAsync(FedFundsActor actor, FedFundsQuoteSide side, decimal min, decimal max,
        decimal rate, CancellationToken ct) => Transaction(async db => {
            await Eligible(db, actor.BankId, ct);
            if (!Enum.IsDefined(side) || min <= 0 || max < min || rate < 0 || rate > 100)
                throw new InvalidOperationException("Invalid quote terms.");
            var now = (await db.SimulationStates.SingleAsync(x => x.Name == "default", ct)).CurrentUtc;
            db.FedFundsQuotes.Add(new FedFundsQuote { BankId = actor.BankId, Side = side, MinAmount = min, MaxAmount = max,
                Rate = rate, ExpiresAt = now.AddDays(1) });
            return true;
        }, ct);

    public Task RequestFundingAsync(FedFundsActor actor, Guid tradeId, CancellationToken ct) => Transaction(async db => {
        var trade = await Load(db, actor.BankId, tradeId, ct);
        if (actor.BankId != trade.LenderBankId) throw new InvalidOperationException("Only the lender can request funding.");
        if (trade.Status == FedFundsTradeStatus.FundingException)
        {
            var obligation = trade.Obligations.Single(x => x.Type == FedFundsObligationType.Funding);
            obligation.Status = FedFundsObligationStatus.Pending;
            FedFundsPolicy.Transition(trade, FedFundsTradeStatus.FundingPending);
        }
        if (trade.Status != FedFundsTradeStatus.FundingPending) throw new InvalidOperationException("Trade is not awaiting funding.");
        await SubmitFunding(db, trade, await Today(db, ct), actor.OperatorId, ct);
        return true;
    }, ct);

    public Task RetryRepaymentAsync(FedFundsActor actor, Guid tradeId, CancellationToken ct) => Transaction(async db => {
        var trade = await Load(db, actor.BankId, tradeId, ct);
        if (actor.BankId != trade.BorrowerBankId || trade.Status is not (FedFundsTradeStatus.Overdue or FedFundsTradeStatus.MaturityDue))
            throw new InvalidOperationException("Only the borrower can retry a due repayment.");
        foreach (var obligation in trade.Obligations.Where(x => x.Type != FedFundsObligationType.Funding && x.Status == FedFundsObligationStatus.Failed))
            obligation.Status = FedFundsObligationStatus.Pending;
        await SubmitRepayments(db, trade, await Today(db, ct), actor.OperatorId, ct);
        return true;
    }, ct);

    public Task CancelAsync(FedFundsActor actor, Guid tradeId, CancellationToken ct) => Transaction(async db => {
        var trade = await Load(db, actor.BankId, tradeId, ct);
        // A final settlement or a submitted instruction must never be cancelled as an agreement edit.
        if (trade.Status is not (FedFundsTradeStatus.Proposed or FedFundsTradeStatus.PendingApprovals or FedFundsTradeStatus.FundingException))
            throw new InvalidOperationException("Only an unfunded proposal or failed funding agreement can be cancelled.");
        await ReleaseCredit(db, trade, ct);
        FedFundsPolicy.Transition(trade, FedFundsTradeStatus.Cancelled);
        Audit(trade, "Cancelled", actor.OperatorId, "Unfunded agreement cancelled; any reserved credit released. Prior rejected instructions remain immutable.");
        return true;
    }, ct);

    private async Task<T> Transaction<T>(Func<BankingDbContext, Task<T>> action, CancellationToken ct)
    {
        await using var probe = await factory.CreateDbContextAsync(ct);
        return await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () => {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var result = await action(db);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        });
    }
    private static async Task<FedFundsTrade> Load(BankingDbContext db, Guid bankId, Guid id, CancellationToken ct) =>
        await Trades(db).SingleOrDefaultAsync(x => x.Id == id && (x.LenderBankId == bankId || x.BorrowerBankId == bankId), ct)
            ?? throw new InvalidOperationException("Trade was not found at this bank.");
    private static IQueryable<FedFundsTrade> Trades(BankingDbContext db) => db.FedFundsTrades
        .Include(x => x.Approvals).Include(x => x.Obligations).Include(x => x.Accruals).Include(x => x.Events).AsSplitQuery();
    private static async Task<Bank> Eligible(BankingDbContext db, Guid bankId, CancellationToken ct)
    {
        var bank = await db.Banks.SingleAsync(x => x.Id == bankId, ct);
        if (bank.CountryCode != "US" || string.IsNullOrWhiteSpace(bank.FedParticipantId))
            throw new InvalidOperationException("Only simulated US depository participants are eligible.");
        return bank;
    }
    private static async Task<DateOnly> Today(BankingDbContext db, CancellationToken ct) => DateOnly.FromDateTime(
        (await db.SimulationStates.SingleAsync(x => x.Name == "default", ct)).CurrentUtc.UtcDateTime);
    private static async Task<FedFundsCreditLine> Credit(BankingDbContext db, FedFundsTrade trade, CancellationToken ct) =>
        await db.FedFundsCreditLines.SingleOrDefaultAsync(x => x.LenderBankId == trade.LenderBankId && x.BorrowerBankId == trade.BorrowerBankId, ct)
        ?? throw new InvalidOperationException("The lender must establish a counterparty credit line first.");
    private static async Task ReleaseCredit(BankingDbContext db, FedFundsTrade trade, CancellationToken ct)
    {
        if (!trade.CreditReserved) return;
        var line = await Credit(db, trade, ct);
        line.ReservedAmount -= trade.Principal;
        trade.CreditReserved = false;
    }
    private static void Audit(FedFundsTrade trade, string type, string actor, string details) =>
        trade.Events.Add(new FedFundsEvent { EventType = type, ActorId = actor, Details = details });
    private static Guid StableId(Guid obligation, int attempt, string kind) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"fedfunds:{obligation:N}:{attempt}:{kind}"))[..16]);
}
