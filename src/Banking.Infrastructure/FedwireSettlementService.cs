using System.Data;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public interface IFedwireSettlementService
{
    Task<FedSettlement> SettleAsync(FedEnvelope payment, CancellationToken token);
}

/// <summary>Stores a single simulated settlement and its balance receipts per correlation ID.</summary>
public sealed class FedwireSettlementService(IDbContextFactory<BankingDbContext> dbFactory,
    IIsoMessageService iso) : IFedwireSettlementService
{
    public async Task<FedSettlement> SettleAsync(FedEnvelope payment, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var settlement = await db.FedSettlements.AsNoTracking()
                .SingleOrDefaultAsync(x => x.CorrelationId == payment.CorrelationId, token);
            if (settlement is null)
            {
                var sender = await db.Banks.SingleAsync(x => x.Id == payment.SenderBankId, token);
                var receiver = await db.Banks.SingleAsync(x => x.Id == payment.ReceiverBankId, token);
                var sequence = Random.Shared.Next(100000, 999999);
                var imad = $"{DateTime.UtcNow:yyyyMMdd}{sender.FedParticipantId[..Math.Min(6, sender.FedParticipantId.Length)]}{sequence}";
                var omad = $"{DateTime.UtcNow:yyyyMMdd}FED{Random.Shared.Next(100000, 999999)}";
                var validXml = iso.Validate(payment.XmlPayload).IsValid;
                var accepted = FedwirePaymentDecision.IsAccepted(validXml, sender, payment.Amount, payment.Scenario);
                if (accepted)
                {
                    sender.MasterAccountBalance -= payment.Amount;
                    receiver.MasterAccountBalance += payment.Amount;
                    BalanceReceipts.Bank(db, payment.CorrelationId, payment.Rail, sender, -payment.Amount);
                    BalanceReceipts.Bank(db, payment.CorrelationId, payment.Rail, receiver, payment.Amount);
                }
                settlement = new FedSettlement
                {
                    CorrelationId = payment.CorrelationId, Imad = imad, Omad = omad,
                    StatusCode = accepted ? "ACSC" : "RJCT"
                };
                db.FedSettlements.Add(settlement);
                await db.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
            return settlement;
        });
    }
}
