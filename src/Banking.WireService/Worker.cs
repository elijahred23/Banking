using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.WireService;

public sealed class Worker(
    IMessageBus bus,
    IDbContextFactory<BankingDbContext> dbFactory,
    IIsoMessageService iso,
    ICbprPlusMessageService cbpr,
    IPaymentRouteResolver routeResolver,
    ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.ConsumeAsync<WireCreated>(Queues.WireCreated, ProcessAsync, stoppingToken);

    private async Task ProcessAsync(WireCreated command, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var wire = await db.WireTransfers.SingleOrDefaultAsync(x => x.Id == command.WireId, cancellationToken);
        if (wire is null || wire.Status != WireStatus.Created) return; // idempotent delivery

        var account = wire.FromAccountId is null ? null :
            await db.Accounts.SingleOrDefaultAsync(x => x.Id == wire.FromAccountId, cancellationToken);
        var blocked = wire.SenderName.Contains("blocked", StringComparison.OrdinalIgnoreCase)
            || wire.ReceiverName.Contains("blocked", StringComparison.OrdinalIgnoreCase);
        var rejection = blocked ? "OFAC simulation matched a blocked name."
            : wire.Amount <= 0 ? "Amount must be positive."
            : account is null ? "Originating account was not found."
            : account.AvailableBalance < wire.Amount ? "Insufficient available customer funds."
            : null;
        if (rejection is not null)
        {
            wire.Status = WireStatus.Rejected;
            db.WireEvents.Add(Event(wire.Id, "Rejected", rejection));
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Wire {WireId} rejected: {Reason}", wire.Id, rejection);
            return;
        }

        var sender = await db.Banks.SingleAsync(x => x.Id == wire.SenderBankId, cancellationToken);
        var receiver = await db.Banks.SingleAsync(x => x.Id == wire.ReceiverBankId, cancellationToken);
        PaymentRoute? paymentRoute = null;
        if (wire.Rail == PaymentRail.SwiftCbprPlus)
        {
            ResolvedPaymentRoute resolved;
            try
            {
                resolved = await routeResolver.ResolveRouteAsync(wire.SenderBankId,
                    wire.ReceiverBankId, "USD", CorrespondentRails.Swift, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                wire.Status = WireStatus.Rejected;
                db.WireEvents.Add(Event(wire.Id, "Rejected", ex.Message));
                await db.SaveChangesAsync(cancellationToken);
                logger.LogWarning("Wire {WireId} rejected: {Reason}", wire.Id, ex.Message);
                return;
            }

            var names = await db.Banks.Where(x => resolved.Steps.Select(y => y.FromBankId)
                    .Concat(resolved.Steps.Select(y => y.ToBankId)).Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
            paymentRoute = new PaymentRoute
            {
                PaymentId = wire.Id, Rail = CorrespondentRails.Swift, CurrencyCode = "USD",
                OriginBankId = wire.SenderBankId, DestinationBankId = wire.ReceiverBankId,
                RouteStatus = PaymentRouteStatuses.Selected,
                Steps = resolved.Steps.Select(x => new PaymentRouteStep
                {
                    StepNumber = x.StepNumber, FromBankId = x.FromBankId, ToBankId = x.ToBankId,
                    StepType = x.StepType, Status = PaymentRouteStepStatuses.Pending,
                    Uetr = wire.CorrelationId
                }).ToList()
            };
            db.PaymentRoutes.Add(paymentRoute);
            db.WireEvents.Add(Event(wire.Id, PaymentEventTypes.RouteSelected,
                $"Correspondent route selected: {string.Join(" → ", resolved.Steps
                    .Select(x => names[x.FromBankId]).Append(names[resolved.Steps[^1].ToBankId]))}."));
        }

        account!.HeldBalance += wire.Amount;
        wire.Status = WireStatus.Validated;
        db.WireEvents.Add(Event(wire.Id, "Validated",
            $"Validation passed; {wire.Amount:C} placed on hold. Ledger balance is unchanged."));
        await db.SaveChangesAsync(cancellationToken);
        await bus.PublishAsync(Queues.WireValidated, new { wire.Id, wire.CorrelationId }, cancellationToken);

        var receiverAccount = await db.Accounts.Include(x => x.Customer)
            .FirstOrDefaultAsync(x => x.Customer.BankId == receiver.Id
                && x.AccountNumber == wire.BeneficiaryAccountNumber,
                cancellationToken);
        var xml = wire.Rail switch
        {
            PaymentRail.FedNow => iso.CreateFedNowPacs008(wire, sender, receiver,
                account.AccountNumber, wire.BeneficiaryAccountNumber),
            PaymentRail.SwiftCbprPlus => iso.CreateCbprPlusPacs008(wire, sender, receiver,
                account.AccountNumber, wire.BeneficiaryAccountNumber),
            _ => iso.CreatePacs008(wire, sender, receiver, account.AccountNumber,
                wire.BeneficiaryAccountNumber)
        };
        if (wire.Scenario == ProcessingScenario.MalformedIso) xml = xml[..^12];
        var validation = iso.Validate(xml);
        var cbprValidation = wire.Rail == PaymentRail.SwiftCbprPlus
            ? cbpr.ValidateCustomerCreditTransfer(xml)
            : null;
        if (!validation.IsValid || cbprValidation is { IsValid: false })
        {
            account.HeldBalance -= wire.Amount;
            wire.Status = WireStatus.Rejected;
            if (paymentRoute is not null) paymentRoute.RouteStatus = PaymentRouteStatuses.Failed;
            db.IsoMessages.Add(new IsoMessage { WireTransferId = wire.Id, MessageType = "pacs.008",
                Direction = MessageDirection.Outbound, XmlPayload = xml });
            db.WireEvents.Add(Event(wire.Id, "Rejected",
                $"ISO profile validation failed; funds hold released. {string.Join(" ",
                    validation.Errors.Concat(cbprValidation?.Errors ?? []))}"));
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        db.IsoMessages.Add(new IsoMessage { WireTransferId = wire.Id, MessageType = "pacs.008",
            Direction = MessageDirection.Outbound, XmlPayload = xml });
        wire.Status = WireStatus.ReadyForFed;
        db.WireEvents.Add(Event(wire.Id, "IsoGenerated",
            $"pacs.008 generated with head.001 business header and UETR; {wire.Rail} lab profile validation passed."));
        await db.SaveChangesAsync(cancellationToken);
        await bus.PublishAsync(Queues.WireIsoGenerated, new { wire.Id, MessageType = "pacs.008" }, cancellationToken);
        await bus.PublishAsync(Queues.WireReadyForFed,
            new WireReadyForFed(wire.Id, wire.CorrelationId, sender.Id, receiver.Id, wire.Amount, xml,
                wire.Scenario),
            cancellationToken);
        logger.LogInformation("Wire {WireId} is ready for {Rail}", wire.Id, wire.Rail);
    }

    private static WireEvent Event(Guid wireId, string type, string description) =>
        new() { WireTransferId = wireId, EventType = type, Description = description };
}
