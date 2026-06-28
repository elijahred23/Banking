using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.WireService;

public sealed class Worker(
    IMessageBus bus,
    IDbContextFactory<BankingDbContext> dbFactory,
    IIsoMessageService iso,
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

        account!.HeldBalance += wire.Amount;
        wire.Status = WireStatus.Validated;
        db.WireEvents.Add(Event(wire.Id, "Validated",
            $"Validation passed; {wire.Amount:C} placed on hold. Ledger balance is unchanged."));
        await db.SaveChangesAsync(cancellationToken);
        await bus.PublishAsync(Queues.WireValidated, new { wire.Id, wire.CorrelationId }, cancellationToken);

        var sender = await db.Banks.SingleAsync(x => x.Id == wire.SenderBankId, cancellationToken);
        var receiver = await db.Banks.SingleAsync(x => x.Id == wire.ReceiverBankId, cancellationToken);
        var receiverAccount = await db.Accounts.Include(x => x.Customer)
            .FirstOrDefaultAsync(x => x.Customer.BankId == receiver.Id
                && x.AccountNumber == wire.BeneficiaryAccountNumber,
                cancellationToken);
        var xml = wire.Rail == PaymentRail.FedNow
            ? iso.CreateFedNowPacs008(wire, sender, receiver, account.AccountNumber,
                wire.BeneficiaryAccountNumber)
            : iso.CreatePacs008(wire, sender, receiver, account.AccountNumber,
                wire.BeneficiaryAccountNumber);
        if (wire.Scenario == ProcessingScenario.MalformedIso) xml = xml[..^12];
        var validation = iso.Validate(xml);
        if (!validation.IsValid)
        {
            account.HeldBalance -= wire.Amount;
            wire.Status = WireStatus.Rejected;
            db.IsoMessages.Add(new IsoMessage { WireTransferId = wire.Id, MessageType = "pacs.008",
                Direction = MessageDirection.Outbound, XmlPayload = xml });
            db.WireEvents.Add(Event(wire.Id, "Rejected",
                $"ISO profile validation failed; funds hold released. {string.Join(" ", validation.Errors)}"));
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
        logger.LogInformation("Wire {WireId} is ready for Fed", wire.Id);
    }

    private static WireEvent Event(Guid wireId, string type, string description) =>
        new() { WireTransferId = wireId, EventType = type, Description = description };
}
