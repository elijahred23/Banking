using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.MessageManager;

public sealed class Worker(IMessageBus bus, IDbContextFactory<BankingDbContext> dbFactory,
    ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        bus.ConsumeAsync<WireReadyForFed>(Queues.WireReadyForFed, SendToFedAsync, stoppingToken),
        bus.ConsumeAsync<FedEnvelope>(Queues.FedInbound, ReceiveFromFedAsync, stoppingToken),
        bus.ConsumeAsync<FedEnvelope>(Queues.FedNowInbound, ReceiveFromFedAsync, stoppingToken));

    private async Task SendToFedAsync(WireReadyForFed message, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var wire = await db.WireTransfers.SingleOrDefaultAsync(x => x.Id == message.WireId, token);
        if (wire is null || wire.Status != WireStatus.ReadyForFed) return;
        wire.Status = WireStatus.SentToFed;
        var destination = wire.Rail == PaymentRail.FedNow ? Queues.FedNowOutbound : Queues.FedOutbound;
        db.WireEvents.Add(Event(wire.Id, "SentToFed",
            $"Message Manager delivered pacs.008 to {destination}."));
        var delivery = new MessageDelivery { WireTransferId = wire.Id, Destination = destination,
            Status = DeliveryStatus.Pending, Attempts = 1 };
        db.MessageDeliveries.Add(delivery);
        await db.SaveChangesAsync(token);
        try
        {
            await bus.PublishAsync(destination,
                new FedEnvelope(FedMessageKind.Payment, wire.Id, wire.CorrelationId,
                    wire.SenderBankId, wire.ReceiverBankId, wire.Amount, message.Pacs008Xml,
                    Scenario: message.Scenario, Rail: wire.Rail), token);
            delivery.Status = DeliveryStatus.Sent;
            delivery.UpdatedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
            await bus.PublishAsync(Queues.WireSent, new { wire.Id, wire.CorrelationId }, token);
        }
        catch (Exception ex)
        {
            delivery.Status = DeliveryStatus.Failed;
            delivery.LastError = ex.Message[..Math.Min(ex.Message.Length, 500)];
            delivery.UpdatedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
            throw;
        }
    }

    private async Task ReceiveFromFedAsync(FedEnvelope message, CancellationToken token)
    {
        if (message.Kind == FedMessageKind.Status) await ApplyStatusAsync(message, token);
        else await CreateIncomingWireAsync(message, token);
    }

    private async Task ApplyStatusAsync(FedEnvelope message, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var wire = await db.WireTransfers.Include(x => x.IsoMessages).Include(x => x.Events)
            .AsSplitQuery().SingleOrDefaultAsync(x => x.Id == message.WireId, token);
        if (wire is null || wire.IsoMessages.Any(x => x.MessageType == "pacs.002"
            && x.XmlPayload == message.XmlPayload)) return;
        if (wire.Status is WireStatus.Settled or WireStatus.Rejected) return;
        wire.Imad = message.Imad;
        wire.Omad = message.Omad;
        db.IsoMessages.Add(new IsoMessage { WireTransferId = wire.Id, MessageType = "pacs.002",
            Direction = MessageDirection.Inbound, XmlPayload = message.XmlPayload });
        var destination = wire.Rail == PaymentRail.FedNow ? Queues.FedNowOutbound : Queues.FedOutbound;
        var delivery = await db.MessageDeliveries
            .OrderByDescending(x => x.UpdatedDate)
            .FirstOrDefaultAsync(x => x.WireTransferId == wire.Id && x.Destination == destination, token);
        if (delivery is not null)
        {
            delivery.Status = DeliveryStatus.Delivered;
            delivery.UpdatedDate = DateTimeOffset.UtcNow;
        }
        if (message.StatusCode is "PDNG" or "ACCP" or "ACWP")
        {
            wire.Status = WireStatus.PendingAtFed;
            db.WireEvents.Add(Event(wire.Id, "PendingAtFed",
                $"{wire.Rail} reported {message.StatusCode}. Customer funds remain held while processing continues."));
        }
        else if (message.StatusCode == "ACSC")
        {
            wire.Status = WireStatus.Settled;
            var reference = wire.Rail == PaymentRail.FedNow
                ? $"network reference {message.Imad}"
                : $"IMAD {message.Imad}";
            db.WireEvents.Add(Event(wire.Id, "AcceptedByFed",
                $"{wire.Rail} returned ACSC and settled the payment; {reference}."));
            db.WireEvents.Add(Event(wire.Id, "Settled", "Sender master account debited and receiver master account credited."));
            if (wire.FromAccountId is Guid accountId)
            {
                var account = await db.Accounts.SingleAsync(x => x.Id == accountId, token);
                account.HeldBalance = Math.Max(0, account.HeldBalance - wire.Amount);
                account.Balance -= wire.Amount;
                AddOutgoingJournal(db, wire, account);
                db.WireEvents.Add(Event(wire.Id, "Posted",
                    $"Customer debit posted; {wire.Amount:C} hold removed."));
            }
        }
        else
        {
            wire.Status = WireStatus.Rejected;
            db.WireEvents.Add(Event(wire.Id, "RejectedByFed", $"{wire.Rail} rejected the payment instruction."));
            if (wire.FromAccountId is Guid accountId)
            {
                var account = await db.Accounts.SingleAsync(x => x.Id == accountId, token);
                account.HeldBalance = Math.Max(0, account.HeldBalance - wire.Amount);
                db.WireEvents.Add(Event(wire.Id, "HoldReleased",
                    $"Fed rejection released the {wire.Amount:C} customer funds hold; no debit posted."));
            }
        }
        await db.SaveChangesAsync(token);
        await bus.PublishAsync(Queues.WireStatusReceived, new { wire.Id, message.StatusCode }, token);
    }

    private async Task CreateIncomingWireAsync(FedEnvelope message, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        if (await db.WireTransfers.AnyAsync(x => x.BankId == message.ReceiverBankId
            && x.CorrelationId == message.CorrelationId && x.Direction == WireDirection.Incoming, token)) return;
        var outgoing = await db.WireTransfers.AsNoTracking().SingleAsync(x => x.Id == message.WireId, token);
        var incoming = new WireTransfer
        {
            CorrelationId = message.CorrelationId, BankId = message.ReceiverBankId,
            SenderBankId = message.SenderBankId, ReceiverBankId = message.ReceiverBankId,
            Direction = WireDirection.Incoming, Amount = message.Amount, Status = WireStatus.Received,
            SenderName = outgoing.SenderName, ReceiverName = outgoing.ReceiverName,
            BeneficiaryAccountNumber = outgoing.BeneficiaryAccountNumber, Scenario = outgoing.Scenario,
            Rail = outgoing.Rail,
            ToAccountId = outgoing.ToAccountId, Imad = message.Imad, Omad = message.Omad,
            IsoMessages = [new IsoMessage { MessageType = "pacs.008", Direction = MessageDirection.Inbound,
                XmlPayload = message.XmlPayload }],
            Events = [Event(Guid.Empty, "ReceivedFromFed",
                $"Original pacs.008 received from {outgoing.Rail}; reference {message.Imad}.")]
        };
        var account = await db.Accounts.Include(x => x.Customer).FirstOrDefaultAsync(x =>
            x.Customer.BankId == message.ReceiverBankId
            && x.AccountNumber == outgoing.BeneficiaryAccountNumber, token);
        if (account is not null)
        {
            incoming.ToAccountId = account.Id;
            account.Balance += incoming.Amount;
            AddIncomingJournal(db, incoming, account);
            incoming.Status = WireStatus.Completed;
            incoming.Events.Add(Event(Guid.Empty, "Posted", $"Funds posted to account ending {account.AccountNumber[^4..]}."));
            incoming.Events.Add(Event(Guid.Empty, "Completed", "Incoming wire processing completed."));
        }
        db.WireTransfers.Add(incoming);
        outgoing = await db.WireTransfers.SingleAsync(x => x.Id == message.WireId, token);
        db.WireEvents.Add(Event(outgoing.Id, "Delivered",
            $"Original pacs.008 delivered to the receiving bank through {outgoing.Rail}."));
        await db.SaveChangesAsync(token);
        await bus.PublishAsync(Queues.WireIncomingReceived, new { incoming.Id, incoming.CorrelationId }, token);
        logger.LogInformation("Created incoming wire {WireId} for correlation {CorrelationId}", incoming.Id,
            incoming.CorrelationId);
    }

    private static WireEvent Event(Guid wireId, string type, string description) =>
        new() { WireTransferId = wireId, EventType = type, Description = description };

    private static void AddOutgoingJournal(BankingDbContext db, WireTransfer wire, Account account)
    {
        var journal = Guid.NewGuid();
        db.LedgerEntries.AddRange(
            Entry(journal, wire.Id, $"CUSTOMER:{account.AccountNumber}", "Customer deposits",
                wire.Amount, 0, "Debit customer deposit liability"),
            Entry(journal, wire.Id, $"FEDMASTER:{wire.SenderBankId:N}", "Fed master account",
                0, wire.Amount, "Credit settlement cash for outgoing payment"));
    }

    private static void AddIncomingJournal(BankingDbContext db, WireTransfer wire, Account account)
    {
        var journal = Guid.NewGuid();
        db.LedgerEntries.AddRange(
            Entry(journal, wire.Id, $"FEDMASTER:{wire.ReceiverBankId:N}", "Fed master account",
                wire.Amount, 0, "Debit settlement cash for incoming payment"),
            Entry(journal, wire.Id, $"CUSTOMER:{account.AccountNumber}", "Customer deposits",
                0, wire.Amount, "Credit beneficiary deposit liability"));
    }

    private static LedgerEntry Entry(Guid journal, Guid wireId, string code, string name,
        decimal debit, decimal credit, string description) => new()
    {
        JournalId = journal, WireTransferId = wireId, AccountCode = code, AccountName = name,
        Debit = debit, Credit = credit, Description = description
    };
}
