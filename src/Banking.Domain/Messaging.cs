namespace Banking.Domain;

public static class Queues
{
    public const string WireCreated = "wire.created";
    public const string WireValidated = "wire.validated";
    public const string WireIsoGenerated = "wire.iso.generated";
    public const string WireReadyForFed = "wire.ready.for.fed";
    public const string WireSent = "wire.sent";
    public const string WireStatusReceived = "wire.status.received";
    public const string WireIncomingReceived = "wire.incoming.received";
    public const string FedOutbound = "FED.OUTBOUND";
    public const string FedInbound = "FED.INBOUND";
    public const string FedNowOutbound = "FEDNOW.OUTBOUND";
    public const string FedNowInbound = "FEDNOW.INBOUND";
    public const string SwiftOutbound = "SWIFT.OUTBOUND";
    public const string SwiftInbound = "SWIFT.INBOUND";
    public const string AchEntryCreated = "ACH.ENTRY.CREATED";
    public const string AchBatchReady = "ACH.BATCH.READY";
    public const string AchOutbound = "ACH.OUTBOUND";
    public const string AchInbound = "ACH.INBOUND";
    public const string AchReturnInbound = "ACH.RETURN.INBOUND";
    public const string AchNocInbound = "ACH.NOC.INBOUND";
    public const string AchSettled = "ACH.SETTLED";
    public const string CheckDepositCreated = "CHECK.DEPOSIT.CREATED";
    public const string CheckCashLetterReady = "CHECK.CASHLETTER.READY";
    public const string CheckOutbound = "CHECK.OUTBOUND";
    public const string CheckInbound = "CHECK.INBOUND";
    public const string CheckReturnInbound = "CHECK.RETURN.INBOUND";
    public const string CheckSettled = "CHECK.SETTLED";
}

public sealed record WireCreated(Guid WireId);
public sealed record AchEntryCreated(Guid EntryId);
public sealed record AchFileReady(Guid FileId);
public sealed record AchFileEnvelope(Guid FileId, Guid OriginatingBankId, string NachaPayload);
public sealed record AchOperatorResult(Guid FileId, bool Accepted, string Message,
    IReadOnlyList<Guid> SettledEntryIds);
public sealed record AchReturnItem(Guid EntryId, string ReturnCode, string Reason);
public sealed record AchReturnFile(Guid FileId, IReadOnlyList<AchReturnItem> Returns);
public sealed record AchNocItem(Guid EntryId, string ChangeCode, string CorrectedData, string Description);
public sealed record AchNocFile(Guid FileId, IReadOnlyList<AchNocItem> Notifications);
public sealed record CheckDepositCreated(Guid CheckDepositId);
public sealed record CheckCashLetterReady(Guid CashLetterId);
public sealed record CheckCashLetterEnvelope(Guid CashLetterId, Guid DepositoryBankId,
    string X937Payload);
public sealed record CheckPresentmentItem(Guid CheckDepositId, Guid CorrelationId,
    Guid DepositoryBankId, Guid? PayingBankId, string PayingRoutingNumber,
    string PayingAccountNumber, string CheckNumber, decimal Amount, string RawMicrLine,
    CheckProcessingScenario Scenario);
public sealed record CheckOperatorResult(Guid CashLetterId, bool Accepted, string Message,
    IReadOnlyList<Guid> SettledCheckDepositIds);
public sealed record CheckReturnItem(Guid CheckDepositId, string ReturnCode, string Reason);
public sealed record CheckReturnFile(Guid CashLetterId, IReadOnlyList<CheckReturnItem> Returns);
public sealed record CheckSettlementNotice(Guid CashLetterId,
    IReadOnlyList<Guid> SettledCheckDepositIds);
public sealed record WireReadyForFed(Guid WireId, Guid CorrelationId, Guid SenderBankId,
    Guid ReceiverBankId, decimal Amount, string Pacs008Xml, ProcessingScenario Scenario);
public enum FedMessageKind { Payment, Status }
public sealed record FedEnvelope(FedMessageKind Kind, Guid WireId, Guid CorrelationId,
    Guid SenderBankId, Guid ReceiverBankId, decimal Amount, string XmlPayload,
    string? Imad = null, string? Omad = null, string? StatusCode = null,
    ProcessingScenario Scenario = ProcessingScenario.Standard,
    PaymentRail Rail = PaymentRail.Fedwire, Guid? RouteStepId = null,
    string? DeliveryMessageId = null);

public interface IMessageBus
{
    Task PublishAsync<T>(string queue, T message, CancellationToken cancellationToken = default);
    Task ConsumeAsync<T>(string queue, Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken);
}
