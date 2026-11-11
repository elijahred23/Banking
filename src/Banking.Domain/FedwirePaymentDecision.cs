namespace Banking.Domain;

/// <summary>Acceptance rules for the lab's simulated Fedwire operator.</summary>
public static class FedwirePaymentDecision
{
    public static bool IsAccepted(bool validXml, Bank sender, Bank receiver, decimal amount, ProcessingScenario scenario) =>
        sender.Id != receiver.Id && sender.CountryCode == "US" && receiver.CountryCode == "US"
        && !string.IsNullOrWhiteSpace(sender.FedParticipantId) && !string.IsNullOrWhiteSpace(receiver.FedParticipantId)
        && IsAccepted(validXml, sender, amount, scenario);

    public static bool IsAccepted(bool validXml, Bank sender, decimal amount, ProcessingScenario scenario) =>
        validXml && PaymentRiskPolicy.WithinFedwireLiquidity(sender, amount)
        && scenario != ProcessingScenario.FedRejects;
}
