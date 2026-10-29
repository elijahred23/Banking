using Banking.Domain;

namespace Banking.FedwireSimulator;

/// <summary>Publishes simulated status messages and accepted payment deliveries.</summary>
public sealed class FedwireResponsePublisher(IMessageBus bus, IIsoMessageService iso)
{
    public async Task PublishResultsAsync(FedEnvelope payment, FedSettlement settlement, CancellationToken token)
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

    public async Task PublishStatusAsync(FedEnvelope payment, FedSettlement settlement,
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
