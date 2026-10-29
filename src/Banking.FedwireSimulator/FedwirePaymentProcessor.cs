using Banking.Domain;
using Banking.Infrastructure;

namespace Banking.FedwireSimulator;

/// <summary>Orchestrates lab settlement and the selected delivery scenario.</summary>
public sealed class FedwirePaymentProcessor(IFedwireSettlementService settlements,
    FedwireResponsePublisher responses, ILogger<FedwirePaymentProcessor> logger)
{
    public async Task ProcessAsync(FedEnvelope payment, CancellationToken token)
    {
        if (payment.Kind != FedMessageKind.Payment || payment.Rail != PaymentRail.Fedwire) return;
        var settlement = await settlements.SettleAsync(payment, token);
        var scenario = ScenarioCatalog.For(payment.Scenario);
        if (payment.Scenario is ProcessingScenario.PendingThenAccepted or ProcessingScenario.DroppedDelivery
            && settlement.StatusCode == "ACSC")
        {
            await responses.PublishStatusAsync(payment, settlement, "PDNG", "Payment is pending Fed processing", token);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, scenario.DelaySeconds)), token);
        }
        if (payment.Scenario == ProcessingScenario.OutOfOrderDelivery && settlement.StatusCode == "ACSC")
        {
            await responses.PublishResultsAsync(payment, settlement, token);
            await responses.PublishStatusAsync(payment, settlement, "PDNG",
                "Delayed stale status delivered after final acceptance", token);
            return;
        }
        await responses.PublishResultsAsync(payment, settlement, token);
        if (payment.Scenario == ProcessingScenario.DuplicateDelivery && settlement.StatusCode == "ACSC")
            await responses.PublishResultsAsync(payment, settlement, token);
        logger.LogInformation("Fed processed {CorrelationId} with {Status}; IMAD {Imad}",
            payment.CorrelationId, settlement.StatusCode, settlement.Imad);
    }
}
