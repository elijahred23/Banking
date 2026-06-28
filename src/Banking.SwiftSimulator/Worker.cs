using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.SwiftSimulator;

/// <summary>
/// Learning-only FINplus/serial-correspondent simulation. SWIFT transports messages;
/// it does not itself settle customer payments.
/// </summary>
public sealed class Worker(
    IMessageBus bus,
    IDbContextFactory<BankingDbContext> dbFactory,
    IIsoMessageService iso,
    ICbprPlusMessageService cbpr,
    ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.ConsumeAsync<FedEnvelope>(Queues.SwiftOutbound, ProcessAsync, stoppingToken);

    private async Task ProcessAsync(FedEnvelope payment, CancellationToken token)
    {
        if (payment.Kind != FedMessageKind.Payment || payment.Rail != PaymentRail.SwiftCbprPlus) return;

        await using var db = await dbFactory.CreateDbContextAsync(token);
        var sender = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == payment.SenderBankId, token);
        var receiver = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == payment.ReceiverBankId, token);
        var outgoing = await db.WireTransfers.AsNoTracking().SingleAsync(x => x.Id == payment.WireId, token);
        var beneficiaryExists = await db.Accounts.AnyAsync(x =>
            x.Customer.BankId == payment.ReceiverBankId
            && x.AccountNumber == outgoing.BeneficiaryAccountNumber, token);
        var validation = cbpr.ValidateCustomerCreditTransfer(payment.XmlPayload);
        var rejection = !validation.IsValid ? string.Join(" ", validation.Errors)
            : !sender.SwiftEnabled ? "Sending institution is not enabled for the SWIFT lab."
            : !receiver.SwiftEnabled ? "Receiving institution is not enabled for the SWIFT lab."
            : !beneficiaryExists ? "Beneficiary account was not found."
            : payment.Scenario == ProcessingScenario.FedRejects ? "Network rejection learning scenario."
            : null;

        var reference = $"SWIFT-{DateTime.UtcNow:yyyyMMdd}-{payment.CorrelationId:N}"[..35];
        if (rejection is not null)
        {
            await PublishStatusAsync(payment, sender, receiver, "RJCT", rejection, reference, token);
            return;
        }

        await PublishStatusAsync(payment, sender, receiver, "ACSP",
            "Accepted for serial correspondent processing", reference, token);
        await PublishStatusAsync(payment, sender, receiver, "ACCC",
            "Beneficiary account credited after correspondent settlement simulation", reference, token);
        await bus.PublishAsync(Queues.SwiftInbound, payment with
        {
            Kind = FedMessageKind.Payment,
            Imad = reference,
            StatusCode = "ACCC"
        }, token);
        logger.LogInformation("CBPR+ payment {CorrelationId} completed with reference {Reference}",
            payment.CorrelationId, reference);
    }

    private async Task PublishStatusAsync(FedEnvelope payment, Bank sender, Bank receiver,
        string status, string reason, string reference, CancellationToken token)
    {
        var xml = iso.CreateCbprPlusPacs002(payment.CorrelationId, status, reason, reference,
            receiver.Bic, sender.Bic);
        await bus.PublishAsync(Queues.SwiftInbound, payment with
        {
            Kind = FedMessageKind.Status,
            XmlPayload = xml,
            Imad = reference,
            StatusCode = status
        }, token);
    }
}
