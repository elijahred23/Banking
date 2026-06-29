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
        if (payment.Kind != FedMessageKind.Payment || payment.Rail != PaymentRail.Fedwire) return;
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
                var validXml = iso.Validate(payment.XmlPayload).IsValid;
                var accepted = validXml && payment.Amount > 0 && sender.MasterAccountBalance >= payment.Amount
                    && payment.Scenario != ProcessingScenario.FedRejects;
                if (accepted)
                {
                    sender.MasterAccountBalance -= payment.Amount;
                    receiver.MasterAccountBalance += payment.Amount;
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
        });
        if (payment.Scenario == ProcessingScenario.PendingThenAccepted
            && settlement!.StatusCode == "ACSC")
        {
            await PublishStatusAsync(payment, settlement, "PDNG", "Payment is pending Fed processing", token);
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        await PublishResultsAsync(payment, settlement!, token);
        logger.LogInformation("Fed processed {CorrelationId} with {Status}; IMAD {Imad}",
            payment.CorrelationId, settlement!.StatusCode, settlement.Imad);
    }

    private async Task PublishResultsAsync(FedEnvelope payment, FedSettlement settlement, CancellationToken token)
    {
        var accepted = settlement.StatusCode == "ACSC";
        await PublishStatusAsync(payment, settlement, settlement.StatusCode,
            accepted ? "AcceptedSettlementCompleted" : "Fed validation, scenario, or liquidity rejection", token);
        if (accepted)
        {
            await bus.PublishAsync(Queues.FedInbound, payment with
            {
                Kind = FedMessageKind.Payment, Imad = settlement.Imad, Omad = settlement.Omad,
                StatusCode = settlement.StatusCode
            }, token);
        }
    }

    private async Task PublishStatusAsync(FedEnvelope payment, FedSettlement settlement,
        string statusCode, string reason, CancellationToken token)
    {
        var originalDefinition = payment.XmlPayload.Contains("pacs.009.001.08", StringComparison.Ordinal)
            ? "pacs.009.001.08" : "pacs.008.001.08";
        var statusXml = iso.CreatePacs002(payment.CorrelationId, statusCode, reason, settlement.Imad,
            originalDefinition);
        await bus.PublishAsync(Queues.FedInbound, payment with
        {
            Kind = FedMessageKind.Status, XmlPayload = statusXml, Imad = settlement.Imad,
            Omad = settlement.Omad, StatusCode = statusCode
        }, token);
    }
}
