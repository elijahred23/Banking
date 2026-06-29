namespace Banking.Domain;

public static class CorrespondentRails
{
    public const string Swift = "SWIFT";
}

public static class PaymentRouteStatuses
{
    public const string Selected = "Selected";
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public static class PaymentRouteStepStatuses
{
    public const string Pending = "Pending";
    public const string Sent = "Sent";
    public const string Accepted = "Accepted";
    public const string Rejected = "Rejected";
    public const string Completed = "Completed";
}

public static class PaymentEventTypes
{
    public const string RouteSelected = "RouteSelected";
    public const string RouteStepStarted = "RouteStepStarted";
    public const string RouteStepAccepted = "RouteStepAccepted";
    public const string RouteStepRejected = "RouteStepRejected";
    public const string IntermediaryForwarded = "IntermediaryForwarded";
    public const string BeneficiaryBankReceived = "BeneficiaryBankReceived";
}

public interface IPaymentRouteResolver
{
    Task<ResolvedPaymentRoute> ResolveRouteAsync(Guid originBankId, Guid destinationBankId,
        string currencyCode, string rail, CancellationToken cancellationToken = default);
}

public sealed record ResolvedPaymentRoute(Guid OriginBankId, Guid DestinationBankId,
    string CurrencyCode, string Rail, IReadOnlyList<ResolvedPaymentRouteStep> Steps);

public sealed record ResolvedPaymentRouteStep(int StepNumber, Guid FromBankId, Guid ToBankId,
    string StepType);
