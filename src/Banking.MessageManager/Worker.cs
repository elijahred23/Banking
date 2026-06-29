using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.MessageManager;

public sealed class Worker(IMessageBus bus, IDbContextFactory<BankingDbContext> dbFactory,
    ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        bus.ConsumeAsync<WireReadyForFed>(Queues.WireReadyForFed, SendToFedAsync, stoppingToken),
        bus.ConsumeAsync<AchFileReady>(Queues.AchBatchReady, SendAchFileAsync, stoppingToken),
        bus.ConsumeAsync<AchOperatorResult>(Queues.AchInbound, ReceiveAchResultAsync, stoppingToken),
        bus.ConsumeAsync<AchReturnFile>(Queues.AchReturnInbound, ReceiveAchReturnAsync, stoppingToken),
        bus.ConsumeAsync<AchNocFile>(Queues.AchNocInbound, ReceiveAchNocAsync, stoppingToken),
        bus.ConsumeAsync<FedEnvelope>(Queues.FedInbound, ReceiveFromFedAsync, stoppingToken),
        bus.ConsumeAsync<FedEnvelope>(Queues.FedNowInbound, ReceiveFromFedAsync, stoppingToken),
        bus.ConsumeAsync<FedEnvelope>(Queues.SwiftInbound, ReceiveFromFedAsync, stoppingToken));

    private async Task SendAchFileAsync(AchFileReady message, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var file = await db.AchFiles.Include(x => x.Batches).ThenInclude(x => x.Entries)
            .SingleOrDefaultAsync(x => x.Id == message.FileId, token);
        if (file is null || file.Status != "Ready") return;
        file.Status = "SentToOperator";
        foreach (var entry in file.Batches.SelectMany(x => x.Entries)) entry.Status = AchEntryStatus.SentToOperator;
        await db.SaveChangesAsync(token);
        await bus.PublishAsync(Queues.AchOutbound,
            new AchFileEnvelope(file.Id, file.OriginatingBankId, file.RawNachaPayload), token);
    }

    private async Task ReceiveAchResultAsync(AchOperatorResult result, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var file = await db.AchFiles.Include(x => x.Batches).ThenInclude(x => x.Entries)
            .SingleOrDefaultAsync(x => x.Id == result.FileId, token);
        if (file is null || file.Status is "Settled" or "Rejected") return;
        if (!result.Accepted)
        {
            file.Status = "Rejected";
            foreach (var entry in file.Batches.SelectMany(x => x.Entries))
            {
                entry.Status = AchEntryStatus.Returned;
                entry.ReturnCode = "R99";
                if (IsCredit(entry.TransactionCode))
                {
                    var origin = await db.Accounts.SingleAsync(x => x.Id == entry.OriginatingAccountId, token);
                    origin.HeldBalance = Math.Max(0, origin.HeldBalance - entry.Amount);
                }
                db.AchReturns.Add(new AchReturn { AchEntryId = entry.Id, ReturnCode = "R99", Reason = result.Message[..Math.Min(240, result.Message.Length)] });
            }
            await db.SaveChangesAsync(token);
            return;
        }

        file.Status = "Settled";
        var settled = result.SettledEntryIds.ToHashSet();
        foreach (var entry in file.Batches.SelectMany(x => x.Entries).Where(x => settled.Contains(x.Id)))
        {
            if (entry.Status is AchEntryStatus.Posted or AchEntryStatus.Settled) continue;
            entry.Status = AchEntryStatus.Settled;
            var origin = await db.Accounts.SingleAsync(x => x.Id == entry.OriginatingAccountId, token);
            var originBank = await db.Banks.SingleAsync(x => x.Id == entry.OriginatingBankId, token);
            if (entry.Purpose == AchPaymentPurpose.TaxPaymentEftps)
            {
                origin.HeldBalance = Math.Max(0, origin.HeldBalance - entry.Amount);
                origin.Balance -= entry.Amount;
                originBank.MasterAccountBalance -= entry.Amount;
                AddEftpsJournal(db, entry, origin);
                entry.Status = AchEntryStatus.Posted;
                continue;
            }
            var receiver = await db.Accounts.Include(x => x.Customer).SingleOrDefaultAsync(x =>
                x.Customer.BankId == entry.ReceivingBankId && x.AccountNumber == entry.ReceivingAccountNumber, token);
            if (receiver is null) continue;
            var receiverBank = await db.Banks.SingleAsync(x => x.Id == receiver.Customer.BankId, token);
            if (IsCredit(entry.TransactionCode))
            {
                origin.HeldBalance = Math.Max(0, origin.HeldBalance - entry.Amount);
                origin.Balance -= entry.Amount;
                receiver.Balance += entry.Amount;
                originBank.MasterAccountBalance -= entry.Amount;
                receiverBank.MasterAccountBalance += entry.Amount;
                AddAchCreditJournal(db, entry, origin, receiver);
            }
            else
            {
                receiver.Balance -= entry.Amount;
                origin.Balance += entry.Amount;
                receiverBank.MasterAccountBalance -= entry.Amount;
                originBank.MasterAccountBalance += entry.Amount;
                AddAchDebitJournal(db, entry, origin, receiver);
            }
            entry.Status = AchEntryStatus.Posted;
        }
        await db.SaveChangesAsync(token);
        await bus.PublishAsync(Queues.AchSettled, new { result.FileId }, token);
    }

    private async Task ReceiveAchReturnAsync(AchReturnFile file, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        foreach (var item in file.Returns)
        {
            if (await db.AchReturns.AnyAsync(x => x.AchEntryId == item.EntryId && x.ReturnCode == item.ReturnCode, token)) continue;
            var entry = await db.AchEntries.SingleOrDefaultAsync(x => x.Id == item.EntryId, token);
            if (entry is null || entry.Status == AchEntryStatus.Posted) continue;
            entry.Status = AchEntryStatus.Returned;
            entry.ReturnCode = item.ReturnCode;
            if (IsCredit(entry.TransactionCode))
            {
                var origin = await db.Accounts.SingleAsync(x => x.Id == entry.OriginatingAccountId, token);
                origin.HeldBalance = Math.Max(0, origin.HeldBalance - entry.Amount);
            }
            db.AchReturns.Add(new AchReturn { AchEntryId = entry.Id, ReturnCode = item.ReturnCode, Reason = item.Reason });
        }
        await db.SaveChangesAsync(token);
    }

    private async Task ReceiveAchNocAsync(AchNocFile file, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        foreach (var item in file.Notifications)
        {
            if (await db.AchNotificationsOfChange.AnyAsync(x => x.AchEntryId == item.EntryId && x.ChangeCode == item.ChangeCode, token)) continue;
            var entry = await db.AchEntries.SingleOrDefaultAsync(x => x.Id == item.EntryId, token);
            if (entry is null) continue;
            entry.Status = AchEntryStatus.NocReceived;
            db.AchNotificationsOfChange.Add(new AchNotificationOfChange { AchEntryId = entry.Id,
                ChangeCode = item.ChangeCode, CorrectedData = item.CorrectedData, Description = item.Description });
        }
        await db.SaveChangesAsync(token);
    }

    private async Task SendToFedAsync(WireReadyForFed message, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var wire = await db.WireTransfers.SingleOrDefaultAsync(x => x.Id == message.WireId, token);
        if (wire is null || wire.Status != WireStatus.ReadyForFed) return;
        if (wire.Rail == PaymentRail.SwiftCbprPlus)
        {
            await SendNextSwiftRouteStepAsync(wire.Id, message.Pacs008Xml, message.Scenario, token);
            return;
        }
        wire.Status = WireStatus.SentToFed;
        var destination = DestinationFor(wire.Rail);
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

    private async Task SendNextSwiftRouteStepAsync(Guid wireId, string pacs008Xml,
        ProcessingScenario scenario, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var wire = await db.WireTransfers.SingleOrDefaultAsync(x => x.Id == wireId, token);
        var route = await db.PaymentRoutes.Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.PaymentId == wireId, token);
        if (wire is null || route is null || route.RouteStatus is PaymentRouteStatuses.Completed
            or PaymentRouteStatuses.Failed) return;
        if (route.Steps.Any(x => x.Status is PaymentRouteStepStatuses.Sent
            or PaymentRouteStepStatuses.Accepted)) return;

        var step = route.Steps.OrderBy(x => x.StepNumber)
            .FirstOrDefault(x => x.Status == PaymentRouteStepStatuses.Pending);
        if (step is null) return;
        var banks = await db.Banks.Where(x => x.Id == step.FromBankId || x.Id == step.ToBankId)
            .ToDictionaryAsync(x => x.Id, token);
        var messageId = Guid.NewGuid().ToString("N");
        step.Status = PaymentRouteStepStatuses.Sent;
        step.MessageId = messageId;
        route.RouteStatus = PaymentRouteStatuses.InProgress;
        wire.Status = WireStatus.SentToFed;
        db.WireEvents.Add(Event(wire.Id, PaymentEventTypes.RouteStepStarted,
            $"Route step {step.StepNumber}: {banks[step.FromBankId].Name} sent pacs.008 to " +
            $"{banks[step.ToBankId].Name} (message {messageId[..8]})."));
        var delivery = new MessageDelivery
        {
            WireTransferId = wire.Id,
            Destination = $"{Queues.SwiftOutbound} step {step.StepNumber}",
            Status = DeliveryStatus.Pending,
            Attempts = 1
        };
        db.MessageDeliveries.Add(delivery);
        await db.SaveChangesAsync(token);

        try
        {
            await bus.PublishAsync(Queues.SwiftOutbound,
                new FedEnvelope(FedMessageKind.Payment, wire.Id, wire.CorrelationId,
                    step.FromBankId, step.ToBankId, wire.Amount, pacs008Xml,
                    Scenario: scenario, Rail: wire.Rail, RouteStepId: step.Id,
                    DeliveryMessageId: messageId), token);
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
            step.Status = PaymentRouteStepStatuses.Pending;
            step.MessageId = null;
            route.RouteStatus = PaymentRouteStatuses.Selected;
            wire.Status = WireStatus.ReadyForFed;
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
        var delivery = await db.MessageDeliveries
            .OrderByDescending(x => x.UpdatedDate)
            .FirstOrDefaultAsync(x => x.WireTransferId == wire.Id
                && x.Status == DeliveryStatus.Sent, token);
        if (delivery is not null)
        {
            delivery.Status = DeliveryStatus.Delivered;
            delivery.UpdatedDate = DateTimeOffset.UtcNow;
        }
        PaymentRoute? route = null;
        PaymentRouteStep? routeStep = null;
        if (wire.Rail == PaymentRail.SwiftCbprPlus && message.RouteStepId is Guid routeStepId)
        {
            route = await db.PaymentRoutes.Include(x => x.Steps)
                .SingleAsync(x => x.PaymentId == wire.Id, token);
            routeStep = route.Steps.SingleOrDefault(x => x.Id == routeStepId);
            if (routeStep is null || routeStep.MessageId != message.DeliveryMessageId) return;
        }

        if (message.StatusCode is "PDNG" or "ACTC" or "ACCP" or "ACWP" or "ACSP")
        {
            wire.Status = WireStatus.PendingAtFed;
            if (routeStep is not null)
            {
                routeStep.Status = PaymentRouteStepStatuses.Accepted;
                db.WireEvents.Add(Event(wire.Id, PaymentEventTypes.RouteStepAccepted,
                    $"Correspondent route step {routeStep.StepNumber} was accepted with {message.StatusCode}."));
            }
            else db.WireEvents.Add(Event(wire.Id, "PendingAtFed",
                $"{wire.Rail} reported {message.StatusCode}. Customer funds remain held while processing continues."));
        }
        else if (message.StatusCode is "ACSC" or "ACCC")
        {
            if (routeStep is not null && route is not null)
            {
                routeStep.Status = PaymentRouteStepStatuses.Completed;
                routeStep.CompletedDate = DateTimeOffset.UtcNow;
                var isFinal = routeStep.ToBankId == route.DestinationBankId
                    && routeStep.StepNumber == route.Steps.Max(x => x.StepNumber);
                if (!isFinal)
                {
                    wire.Status = WireStatus.PendingAtFed;
                    db.WireEvents.Add(Event(wire.Id, PaymentEventTypes.IntermediaryForwarded,
                        $"Intermediary completed route step {routeStep.StepNumber}; the original UETR will continue to the next bank."));
                }
                else route.RouteStatus = PaymentRouteStatuses.Completed;
                if (!isFinal)
                {
                    await db.SaveChangesAsync(token);
                    await bus.PublishAsync(Queues.WireStatusReceived,
                        new { wire.Id, message.StatusCode }, token);
                    var original = wire.IsoMessages.First(x => x.MessageType == "pacs.008"
                        && x.Direction == MessageDirection.Outbound).XmlPayload;
                    await SendNextSwiftRouteStepAsync(wire.Id, original, wire.Scenario, token);
                    return;
                }
            }
            wire.Status = WireStatus.Settled;
            var reference = wire.Rail == PaymentRail.Fedwire
                ? $"IMAD {message.Imad}"
                : $"network reference {message.Imad}";
            db.WireEvents.Add(Event(wire.Id, "AcceptedByFed",
                $"{wire.Rail} returned {message.StatusCode} and completed the payment; {reference}."));
            db.WireEvents.Add(Event(wire.Id, "Settled", wire.Rail == PaymentRail.SwiftCbprPlus
                ? "Simulated correspondent positions debited and credited using the serial method."
                : "Sender master account debited and receiver master account credited."));
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
            if (routeStep is not null && route is not null)
            {
                routeStep.Status = PaymentRouteStepStatuses.Rejected;
                route.RouteStatus = PaymentRouteStatuses.Failed;
                db.WireEvents.Add(Event(wire.Id, PaymentEventTypes.RouteStepRejected,
                    $"Correspondent route step {routeStep.StepNumber} was rejected."));
            }
            db.WireEvents.Add(Event(wire.Id, "RejectedByFed", $"{wire.Rail} rejected the payment instruction."));
            if (wire.FromAccountId is Guid accountId)
            {
                var account = await db.Accounts.SingleAsync(x => x.Id == accountId, token);
                account.HeldBalance = Math.Max(0, account.HeldBalance - wire.Amount);
                db.WireEvents.Add(Event(wire.Id, "HoldReleased",
                    $"Network rejection released the {wire.Amount:C} customer funds hold; no debit posted."));
            }
        }
        await db.SaveChangesAsync(token);
        await bus.PublishAsync(Queues.WireStatusReceived, new { wire.Id, message.StatusCode }, token);
    }

    private async Task CreateIncomingWireAsync(FedEnvelope message, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var outgoing = await db.WireTransfers.AsNoTracking().SingleAsync(x => x.Id == message.WireId, token);
        if (outgoing.Rail == PaymentRail.SwiftCbprPlus
            && message.ReceiverBankId != outgoing.ReceiverBankId) return;
        if (await db.WireTransfers.AnyAsync(x => x.BankId == message.ReceiverBankId
            && x.CorrelationId == message.CorrelationId && x.Direction == WireDirection.Incoming, token)) return;
        var incoming = new WireTransfer
        {
            CorrelationId = message.CorrelationId, BankId = message.ReceiverBankId,
            SenderBankId = outgoing.SenderBankId, ReceiverBankId = message.ReceiverBankId,
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
        if (outgoing.Rail == PaymentRail.SwiftCbprPlus)
            db.WireEvents.Add(Event(outgoing.Id, PaymentEventTypes.BeneficiaryBankReceived,
                "The beneficiary bank received the routed payment instruction."));
        await db.SaveChangesAsync(token);
        await bus.PublishAsync(Queues.WireIncomingReceived, new { incoming.Id, incoming.CorrelationId }, token);
        logger.LogInformation("Created incoming wire {WireId} for correlation {CorrelationId}", incoming.Id,
            incoming.CorrelationId);
    }

    private static WireEvent Event(Guid wireId, string type, string description) =>
        new() { WireTransferId = wireId, EventType = type, Description = description };

    private static string DestinationFor(PaymentRail rail) => rail switch
    {
        PaymentRail.FedNow => Queues.FedNowOutbound,
        PaymentRail.SwiftCbprPlus => Queues.SwiftOutbound,
        _ => Queues.FedOutbound
    };

    private static void AddOutgoingJournal(BankingDbContext db, WireTransfer wire, Account account)
    {
        var journal = Guid.NewGuid();
        db.LedgerEntries.AddRange(
            Entry(journal, wire.Id, $"CUSTOMER:{account.AccountNumber}", "Customer deposits",
                wire.Amount, 0, "Debit customer deposit liability"),
            Entry(journal, wire.Id,
                wire.Rail == PaymentRail.SwiftCbprPlus
                    ? $"CORRESPONDENT:{wire.SenderBankId:N}" : $"FEDMASTER:{wire.SenderBankId:N}",
                wire.Rail == PaymentRail.SwiftCbprPlus ? "Correspondent position" : "Fed master account",
                0, wire.Amount, "Credit settlement cash for outgoing payment"));
    }

    private static void AddIncomingJournal(BankingDbContext db, WireTransfer wire, Account account)
    {
        var journal = Guid.NewGuid();
        db.LedgerEntries.AddRange(
            Entry(journal, wire.Id,
                wire.Rail == PaymentRail.SwiftCbprPlus
                    ? $"CORRESPONDENT:{wire.ReceiverBankId:N}" : $"FEDMASTER:{wire.ReceiverBankId:N}",
                wire.Rail == PaymentRail.SwiftCbprPlus ? "Correspondent position" : "Fed master account",
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

    private static bool IsCredit(AchTransactionCode code) => code is AchTransactionCode.CheckingCredit or AchTransactionCode.SavingsCredit;

    private static void AddAchCreditJournal(BankingDbContext db, AchEntry entry, Account origin, Account receiver)
    {
        var journal = Guid.NewGuid();
        db.AchLedgerEntries.AddRange(
            AchJournal(journal, entry.Id, $"CUSTOMER:{origin.AccountNumber}", "Customer deposits", entry.Amount, 0, "Debit originator deposit liability"),
            AchJournal(journal, entry.Id, $"ACH_CLEARING:{entry.OriginatingBankId:N}", "ACH clearing", 0, entry.Amount, "Credit outgoing ACH clearing"),
            AchJournal(journal, entry.Id, $"FEDACH_SETTLEMENT:{entry.ReceivingBankId:N}", "FedACH settlement", entry.Amount, 0, "Debit incoming FedACH settlement"),
            AchJournal(journal, entry.Id, $"CUSTOMER:{receiver.AccountNumber}", "Customer deposits", 0, entry.Amount, "Credit receiver deposit liability"));
    }

    private static void AddAchDebitJournal(BankingDbContext db, AchEntry entry, Account origin, Account receiver)
    {
        var journal = Guid.NewGuid();
        db.AchLedgerEntries.AddRange(
            AchJournal(journal, entry.Id, $"CUSTOMER:{receiver.AccountNumber}", "Customer deposits", entry.Amount, 0, "Debit receiver deposit liability"),
            AchJournal(journal, entry.Id, $"FEDACH_SETTLEMENT:{entry.ReceivingBankId:N}", "FedACH settlement", 0, entry.Amount, "Credit outgoing FedACH settlement"),
            AchJournal(journal, entry.Id, $"ACH_CLEARING:{entry.OriginatingBankId:N}", "ACH clearing", entry.Amount, 0, "Debit incoming ACH clearing"),
            AchJournal(journal, entry.Id, $"CUSTOMER:{origin.AccountNumber}", "Customer deposits", 0, entry.Amount, "Credit originator deposit liability"));
    }

    private static void AddEftpsJournal(BankingDbContext db, AchEntry entry, Account origin)
    {
        var journal = Guid.NewGuid();
        db.AchLedgerEntries.AddRange(
            AchJournal(journal, entry.Id, $"CUSTOMER:{origin.AccountNumber}", "Customer deposits", entry.Amount, 0, "Debit taxpayer deposit liability"),
            AchJournal(journal, entry.Id, $"ACH_CLEARING:{entry.OriginatingBankId:N}", "ACH clearing", 0, entry.Amount, "Credit simulated Treasury tax payment settlement"));
    }

    private static AchLedgerEntry AchJournal(Guid journal, Guid entryId, string code, string name,
        decimal debit, decimal credit, string description) => new()
    { JournalId = journal, AchEntryId = entryId, AccountCode = code, AccountName = name,
        Debit = debit, Credit = credit, Description = description };
}
