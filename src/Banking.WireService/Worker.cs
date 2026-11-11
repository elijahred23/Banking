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
    IWireComplianceService compliance,
    ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        bus.ConsumeAsync<WireCreated>(Queues.WireCreated, ProcessAsync, stoppingToken),
        RecheckLiquidityAsync(stoppingToken));

    private async Task RecheckLiquidityAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var waiting = await db.WireTransfers.AsNoTracking()
                    .Where(x => x.Status == WireStatus.Created && db.WireEvents.Any(e =>
                        e.WireTransferId == x.Id && e.EventType == "AwaitingLiquidity"))
                    .OrderBy(x => x.CreatedDate).ThenBy(x => x.Id)
                    .Select(x => x.Id).ToListAsync(ct);
                foreach (var id in waiting)
                {
                    try { await ProcessAsync(new WireCreated(id), ct); }
                    catch (DbUpdateConcurrencyException)
                    {
                        // Account balances and wire status are optimistic concurrency tokens.
                        // A competing reservation won; reload on the next funding scan.
                        logger.LogInformation("Wire {WireId} will retry after a concurrent reservation", id);
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Liquidity scan failed; durable waiting wires remain eligible for retry");
            }
        }
    }

    private async Task ProcessAsync(WireCreated command, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var wire = await db.WireTransfers.SingleOrDefaultAsync(x => x.Id == command.WireId, cancellationToken);
        if (wire is null || wire.Status != WireStatus.Created) return; // idempotent delivery

        var account = wire.FromAccountId is null ? null :
            await db.Accounts.SingleOrDefaultAsync(x => x.Id == wire.FromAccountId, cancellationToken);
        var sender = await db.Banks.SingleAsync(x => x.Id == wire.SenderBankId, cancellationToken);
        var receiver = await db.Banks.SingleAsync(x => x.Id == wire.ReceiverBankId, cancellationToken);
        var institutionTransfer = wire.TransferType == WireTransferType.FinancialInstitutionCreditTransfer;
        var screening = wire.ComplianceStatus == "ClearedByOfficer"
            ? new WireScreeningResult(false, null)
            : compliance.Screen(wire, sender, receiver);
        if (screening.RequiresReview)
        {
            WireWorkflow.Transition(wire, WireStatus.PendingComplianceReview);
            wire.ComplianceStatus = "ReviewRequired";
            wire.ComplianceReason = screening.Reason;
            db.WireEvents.Add(Event(wire.Id, "ComplianceReviewRequired", screening.Reason!));
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        wire.ComplianceStatus ??= "Cleared";
        var rejection = wire.Amount <= 0 ? "Amount must be positive."
            : institutionTransfer && wire.Rail == PaymentRail.SwiftCbprPlus
                ? "pacs.009 is supported over Fedwire and FedNow only."
            : institutionTransfer && sender.MasterAccountBalance < wire.Amount
                ? "Insufficient master-account liquidity."
            : !institutionTransfer && account is null ? "Originating account was not found."
            : null;
        if (rejection is not null)
        {
            WireWorkflow.Transition(wire, WireStatus.Rejected);
            db.WireEvents.Add(Event(wire.Id, "Rejected", rejection));
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Wire {WireId} rejected: {Reason}", wire.Id, rejection);
            return;
        }

        if (!institutionTransfer && account!.AvailableBalance < wire.Amount)
        {
            if (!await db.WireEvents.AnyAsync(x => x.WireTransferId == wire.Id
                    && x.EventType == "AwaitingLiquidity", cancellationToken))
            {
                db.WireEvents.Add(Event(wire.Id, "AwaitingLiquidity",
                    "Awaiting available customer funds; no hold or accounting posted. Automatically rechecked every 15 seconds in creation order."));
                await db.SaveChangesAsync(cancellationToken);
            }
            return;
        }

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
                WireWorkflow.Transition(wire, WireStatus.Rejected);
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
                    .Select(x => names[x.FromBankId]).Append(names[resolved.Steps[^1].ToBankId]))}. Fewest active legs selected; configured priority breaks ties."));
        }

        if (!institutionTransfer) account!.HeldBalance += wire.Amount;
        WireWorkflow.Transition(wire, WireStatus.Validated);
        db.WireEvents.Add(Event(wire.Id, "Validated",
            institutionTransfer
                ? $"Validation passed; {wire.Amount:C} of master-account liquidity is available."
                : $"Validation passed; {wire.Amount:C} placed on hold. Ledger balance is unchanged."));

        var messageType = institutionTransfer ? "pacs.009" : "pacs.008";
        var customerAccount = account!;
        var xml = institutionTransfer ? wire.Rail switch
        {
            PaymentRail.FedNow => iso.CreateFedNowPacs009(wire, sender, receiver),
            _ => iso.CreatePacs009(wire, sender, receiver)
        } : wire.Rail switch
        {
            PaymentRail.FedNow => iso.CreateFedNowPacs008(wire, sender, receiver,
                customerAccount.AccountNumber, wire.BeneficiaryAccountNumber),
            PaymentRail.SwiftCbprPlus => iso.CreateCbprPlusPacs008(wire, sender, receiver,
                customerAccount.AccountNumber, wire.BeneficiaryAccountNumber),
            _ => iso.CreatePacs008(wire, sender, receiver, customerAccount.AccountNumber,
                wire.BeneficiaryAccountNumber)
        };
        if (wire.Scenario == ProcessingScenario.MalformedIso) xml = xml[..^12];
        var validation = iso.Validate(xml);
        var cbprValidation = wire.Rail == PaymentRail.SwiftCbprPlus
            ? cbpr.ValidateCustomerCreditTransfer(xml)
            : null;
        if (!validation.IsValid || cbprValidation is { IsValid: false })
        {
            if (!institutionTransfer) account!.HeldBalance -= wire.Amount;
            WireWorkflow.Transition(wire, WireStatus.Rejected);
            if (paymentRoute is not null) paymentRoute.RouteStatus = PaymentRouteStatuses.Failed;
            db.IsoMessages.Add(new IsoMessage { WireTransferId = wire.Id, MessageType = messageType,
                Direction = MessageDirection.Outbound, XmlPayload = xml });
            db.WireEvents.Add(Event(wire.Id, "Rejected",
                $"ISO profile validation failed; funds hold released. {string.Join(" ",
                    validation.Errors.Concat(cbprValidation?.Errors ?? []))}"));
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        db.IsoMessages.Add(new IsoMessage { WireTransferId = wire.Id, MessageType = messageType,
            Direction = MessageDirection.Outbound, XmlPayload = xml });
        WireWorkflow.Transition(wire, WireStatus.ReadyForFed);
        db.WireEvents.Add(Event(wire.Id, "IsoGenerated",
            $"{messageType} generated with head.001 business header and UETR; {wire.Rail} lab profile validation passed."));
        db.OutboxMessages.Add(Outbox.Message(Queues.WireValidated,
            new { wire.Id, wire.CorrelationId }, wire.BankId));
        db.OutboxMessages.Add(Outbox.Message(Queues.WireIsoGenerated,
            new { wire.Id, MessageType = messageType }, wire.BankId));
        db.OutboxMessages.Add(Outbox.Message(Queues.WireReadyForFed,
            new WireReadyForFed(wire.Id, wire.CorrelationId, sender.Id, receiver.Id, wire.Amount, xml,
                messageType, wire.Scenario), wire.BankId));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Wire {WireId} is ready for {Rail}", wire.Id, wire.Rail);
    }

    private static WireEvent Event(Guid wireId, string type, string description) =>
        new() { WireTransferId = wireId, EventType = type, Description = description };
}
