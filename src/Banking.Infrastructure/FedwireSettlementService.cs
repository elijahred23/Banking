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
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var instruction = await db.WireTransfers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == payment.WireId, token);
            if (payment.Kind != FedMessageKind.Payment || payment.Rail != PaymentRail.Fedwire
                || instruction is null || instruction.Direction != WireDirection.Outgoing
                || instruction.BankId != payment.SenderBankId || instruction.Rail != payment.Rail
                || instruction.CorrelationId != payment.CorrelationId || instruction.Amount != payment.Amount
                || instruction.SenderBankId != payment.SenderBankId || instruction.ReceiverBankId != payment.ReceiverBankId)
                throw new InvalidOperationException("Network instruction does not match its originating bank's payment.");
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
                var accepted = FedwirePaymentDecision.IsAccepted(validXml, sender, receiver, payment.Amount, payment.Scenario);
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
                var status = payment with { Kind = FedMessageKind.Status,
                    XmlPayload = iso.CreatePacs002(payment.CorrelationId, settlement.StatusCode,
                        accepted ? "AcceptedSettlementCompleted" : "Simulated network rejection", imad,
                        payment.XmlPayload.Contains("pacs.009.001.08", StringComparison.Ordinal)
                            ? "pacs.009.001.08" : "pacs.008.001.08"),
                    Imad = imad, Omad = omad, StatusCode = settlement.StatusCode };
                db.OutboxMessages.Add(Outbox.Message(Queues.FedInbound, status, sender.Id));
                if (accepted)
                    db.OutboxMessages.Add(Outbox.Message(Queues.FedInbound, payment with {
                        Kind = FedMessageKind.Payment, Imad = imad, Omad = omad,
                        StatusCode = settlement.StatusCode }, receiver.Id));
                db.FedSettlements.Add(settlement);
                await db.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
            return settlement;
        });
    }
}
