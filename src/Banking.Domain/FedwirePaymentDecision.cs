namespace Banking.Domain;

/// <summary>Acceptance rules for the lab's simulated Fedwire operator.</summary>
public static class FedwirePaymentDecision
{
    public static bool IsAccepted(bool validXml, Bank sender, decimal amount, ProcessingScenario scenario) =>
        validXml && PaymentRiskPolicy.WithinFedwireLiquidity(sender, amount)
        && scenario != ProcessingScenario.FedRejects;
}
