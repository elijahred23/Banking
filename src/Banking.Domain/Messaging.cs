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
}

public sealed record WireCreated(Guid WireId);
public sealed record WireReadyForFed(Guid WireId, Guid CorrelationId, Guid SenderBankId,
    Guid ReceiverBankId, decimal Amount, string Pacs008Xml);
public enum FedMessageKind { Payment, Status }
public sealed record FedEnvelope(FedMessageKind Kind, Guid WireId, Guid CorrelationId,
    Guid SenderBankId, Guid ReceiverBankId, decimal Amount, string XmlPayload,
    string? Imad = null, string? Omad = null, string? StatusCode = null);

public interface IMessageBus
{
    Task PublishAsync<T>(string queue, T message, CancellationToken cancellationToken = default);
    Task ConsumeAsync<T>(string queue, Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken);
}
