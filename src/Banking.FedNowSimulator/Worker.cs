using System.Data;
using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.FedNowSimulator;

public sealed class Worker(
    IMessageBus bus,
    IDbContextFactory<BankingDbContext> dbFactory,
    IIsoMessageService iso,
    IFedNowMessageService fedNow,
    ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.ConsumeAsync<FedEnvelope>(Queues.FedNowOutbound, ProcessAsync, stoppingToken);

    private async Task ProcessAsync(FedEnvelope payment, CancellationToken token)
    {
        if (payment.Kind != FedMessageKind.Payment || payment.Rail != PaymentRail.FedNow) return;

        await using var db = await dbFactory.CreateDbContextAsync(token);
        var existing = await db.FedSettlements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CorrelationId == payment.CorrelationId, token);
        if (existing is not null)
        {
            await PublishResultsAsync(payment, existing, token);
            return;
        }

        var sender = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == payment.SenderBankId, token);
        var receiver = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == payment.ReceiverBankId, token);
        var outgoing = await db.WireTransfers.AsNoTracking().SingleAsync(x => x.Id == payment.WireId, token);
        var receiverAccountExists = await db.Accounts.AnyAsync(x =>
            x.Customer.BankId == payment.ReceiverBankId
            && x.AccountNumber == outgoing.BeneficiaryAccountNumber, token);

        var validation = fedNow.Validate(payment.XmlPayload, payment.Amount);
        var rejection = FedNowPaymentDecision.RejectionReason(new FedNowPaymentContext(
            validation,
            sender.FedNowEnabled && sender.FedNowSendEnabled,
            receiver.FedNowEnabled && receiver.FedNowReceiveEnabled,
            receiver.FedNowOnline,
            receiverAccountExists,
            sender.MasterAccountBalance,
            payment.Amount,
            payment.Scenario));

        var networkReference = $"FN{DateTime.UtcNow:yyyyMMddHHmmss}{Random.Shared.Next(100000, 999999)}";
        if (rejection is null)
        {
            await PublishStatusAsync(payment, "ACCP", "Receiver accepted the payment for processing",
                networkReference, token);
            if (payment.Scenario == ProcessingScenario.PendingThenAccepted)
            {
                await PublishStatusAsync(payment, "ACWP", "Receiver accepted without posting pending review",
                    networkReference, token);
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }

        FedSettlement settlement = null!;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var stored = await db.FedSettlements
                .SingleOrDefaultAsync(x => x.CorrelationId == payment.CorrelationId, token);
            var created = stored is null;
            settlement = stored ?? new FedSettlement
                {
                    CorrelationId = payment.CorrelationId,
                    Imad = networkReference,
                    Omad = networkReference,
                    StatusCode = rejection is null ? "ACSC" : "RJCT"
                };
            if (created)
            {
                var debitBank = await db.Banks.SingleAsync(x => x.Id == payment.SenderBankId, token);
                var creditBank = await db.Banks.SingleAsync(x => x.Id == payment.ReceiverBankId, token);
                if (rejection is null)
                {
                    debitBank.MasterAccountBalance -= payment.Amount;
                    creditBank.MasterAccountBalance += payment.Amount;
                }
                db.FedSettlements.Add(settlement);
            }
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });

        await PublishResultsAsync(payment, settlement, token, rejection);
        logger.LogInformation("FedNow processed {CorrelationId} with {Status}; reference {Reference}",
            payment.CorrelationId, settlement.StatusCode, settlement.Imad);
    }

    private async Task PublishResultsAsync(FedEnvelope payment, FedSettlement settlement,
        CancellationToken token, string? rejection = null)
    {
        var accepted = settlement.StatusCode == "ACSC";
        await PublishStatusAsync(payment, settlement.StatusCode,
            accepted ? "AcceptedSettlementCompleted" : rejection ?? "FedNow rejected the payment",
            settlement.Imad, token);
        if (accepted)
        {
            await bus.PublishAsync(Queues.FedNowInbound, payment with
            {
                Kind = FedMessageKind.Payment,
                Imad = settlement.Imad,
                Omad = settlement.Omad,
                StatusCode = settlement.StatusCode
            }, token);
        }
    }

    private async Task PublishStatusAsync(FedEnvelope payment, string statusCode, string reason,
        string networkReference, CancellationToken token)
    {
        var statusXml = iso.CreateFedNowPacs002(payment.CorrelationId, statusCode, reason,
            networkReference, "FEDNOW", "PARTICIPANT");
        await bus.PublishAsync(Queues.FedNowInbound, payment with
        {
            Kind = FedMessageKind.Status,
            XmlPayload = statusXml,
            Imad = networkReference,
            Omad = networkReference,
            StatusCode = statusCode
        }, token);
    }
}
