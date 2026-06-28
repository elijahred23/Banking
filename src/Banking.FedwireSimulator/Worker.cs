using System.Data;
using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.FedwireSimulator;

public sealed class Worker(IMessageBus bus, IDbContextFactory<BankingDbContext> dbFactory,
    IIsoMessageService iso, ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.ConsumeAsync<FedEnvelope>(Queues.FedOutbound, SettleAsync, stoppingToken);

    private async Task SettleAsync(FedEnvelope payment, CancellationToken token)
    {
        if (payment.Kind != FedMessageKind.Payment) return;
        await using var db = await dbFactory.CreateDbContextAsync(token);
        FedSettlement? settlement = null;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            settlement = await db.FedSettlements.AsNoTracking()
                .SingleOrDefaultAsync(x => x.CorrelationId == payment.CorrelationId, token);
            if (settlement is null)
            {
                var sender = await db.Banks.SingleAsync(x => x.Id == payment.SenderBankId, token);
                var receiver = await db.Banks.SingleAsync(x => x.Id == payment.ReceiverBankId, token);
                var sequence = Random.Shared.Next(100000, 999999);
                var imad = $"{DateTime.UtcNow:yyyyMMdd}{sender.FedParticipantId[..Math.Min(6, sender.FedParticipantId.Length)]}{sequence}";
                var omad = $"{DateTime.UtcNow:yyyyMMdd}FED{Random.Shared.Next(100000, 999999)}";
                var validXml = iso.IsWellFormed(payment.XmlPayload, out _);
                var accepted = validXml && payment.Amount > 0 && sender.MasterAccountBalance >= payment.Amount;
                if (accepted)
                {
                    sender.MasterAccountBalance -= payment.Amount;
                    receiver.MasterAccountBalance += payment.Amount;
                }
                settlement = new FedSettlement
                {
                    CorrelationId = payment.CorrelationId, Imad = imad, Omad = omad,
                    StatusCode = accepted ? "ACCP" : "RJCT"
                };
                db.FedSettlements.Add(settlement);
                await db.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
        });
        await PublishResultsAsync(payment, settlement!, token);
        logger.LogInformation("Fed processed {CorrelationId} with {Status}; IMAD {Imad}",
            payment.CorrelationId, settlement!.StatusCode, settlement.Imad);
    }

    private async Task PublishResultsAsync(FedEnvelope payment, FedSettlement settlement, CancellationToken token)
    {
        var accepted = settlement.StatusCode == "ACCP";
        var statusXml = iso.CreatePacs002(payment.CorrelationId, settlement.StatusCode,
            accepted ? "AcceptedSettlementCompleted" : "Fed validation or liquidity rejection",
            settlement.Imad);
        await bus.PublishAsync(Queues.FedInbound, payment with
        {
            Kind = FedMessageKind.Status, XmlPayload = statusXml, Imad = settlement.Imad,
            Omad = settlement.Omad, StatusCode = settlement.StatusCode
        }, token);
        if (accepted)
        {
            await bus.PublishAsync(Queues.FedInbound, payment with
            {
                Kind = FedMessageKind.Payment, Imad = settlement.Imad, Omad = settlement.Omad,
                StatusCode = settlement.StatusCode
            }, token);
        }
    }
}
