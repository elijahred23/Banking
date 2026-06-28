namespace Banking.Domain;

public enum FedNowMessageCategory { Value, NonValue, System, AccountReporting }

public sealed record FedNowMessageRule(
    string MessageType,
    FedNowMessageCategory Category,
    string Purpose,
    bool RequiresReceiptAcknowledgement = false,
    IReadOnlyList<string>? ResponseMessageTypes = null);

/// <summary>
/// Public FedNow lab profile. Exact field cardinalities and private transport/signing
/// requirements are defined by the Federal Reserve MyStandards specifications.
/// </summary>
public static class FedNowProfile
{
    public const decimal CustomerCreditTransferLimit = 10_000_000m;
    public const string BusinessService = "fednow";

    public static IReadOnlyCollection<FedNowMessageRule> Messages { get; } =
    [
        new("pacs.008", FedNowMessageCategory.Value, "Customer credit transfer", ResponseMessageTypes: ["pacs.002"]),
        new("pacs.004", FedNowMessageCategory.Value, "Payment return", ResponseMessageTypes: ["pacs.002"]),
        new("pacs.009", FedNowMessageCategory.Value, "Liquidity management transfer", ResponseMessageTypes: ["pacs.002"]),
        new("pacs.002", FedNowMessageCategory.NonValue, "Payment status report"),
        new("pacs.028", FedNowMessageCategory.NonValue, "Payment status request", true, ["pacs.002", "pain.014"]),
        new("camt.056", FedNowMessageCategory.NonValue, "Return request", true, ["camt.029"]),
        new("camt.029", FedNowMessageCategory.NonValue, "Investigation or request response", true),
        new("pain.013", FedNowMessageCategory.NonValue, "Request for payment", true, ["pain.014"]),
        new("pain.014", FedNowMessageCategory.NonValue, "Request for payment response", true),
        new("camt.055", FedNowMessageCategory.NonValue, "Request for payment cancellation", true, ["camt.029"]),
        new("camt.026", FedNowMessageCategory.NonValue, "Information request", true, ["camt.029", "camt.028"]),
        new("camt.028", FedNowMessageCategory.NonValue, "Additional payment information", true),
        new("admi.002", FedNowMessageCategory.System, "Message reject"),
        new("admi.007", FedNowMessageCategory.System, "Receipt acknowledgement"),
        new("admi.004", FedNowMessageCategory.System, "Participant or FedNow broadcast", ResponseMessageTypes: ["admi.011"]),
        new("admi.011", FedNowMessageCategory.System, "FedNow system response"),
        new("admi.006", FedNowMessageCategory.System, "Retrieval request"),
        new("admi.998", FedNowMessageCategory.System, "FedNow participant file"),
        new("camt.060", FedNowMessageCategory.AccountReporting, "Account reporting request", ResponseMessageTypes: ["camt.052"]),
        new("camt.052", FedNowMessageCategory.AccountReporting, "Account balance or activity report"),
        new("camt.054", FedNowMessageCategory.AccountReporting, "Account debit or credit notification")
    ];

    public static bool TryGetRule(string messageType, out FedNowMessageRule rule)
    {
        var baseType = messageType.Length >= 8 ? messageType[..8] : messageType;
        rule = Messages.FirstOrDefault(x => x.MessageType.Equals(baseType,
            StringComparison.OrdinalIgnoreCase))!;
        return rule is not null;
    }
}

public sealed record FedNowValidationResult(bool IsValid, string MessageType,
    IReadOnlyList<string> Errors);

public sealed record FedNowPaymentContext(
    FedNowValidationResult MessageValidation,
    bool SenderEnabled,
    bool ReceiverEnabled,
    bool ReceiverOnline,
    bool BeneficiaryAccountExists,
    decimal SenderLiquidity,
    decimal Amount,
    ProcessingScenario Scenario);

public static class FedNowPaymentDecision
{
    public static string? RejectionReason(FedNowPaymentContext context) =>
        !context.MessageValidation.IsValid ? string.Join(" ", context.MessageValidation.Errors)
        : !context.SenderEnabled ? "Sender is not enabled to send FedNow payments."
        : !context.ReceiverEnabled ? "Receiver is not enabled to receive FedNow payments."
        : !context.ReceiverOnline ? "Receiver is signed off of the FedNow Service."
        : !context.BeneficiaryAccountExists
            ? "Receiver rejected the payment because the beneficiary account was not found."
        : context.SenderLiquidity < context.Amount ? "Sender has insufficient master-account liquidity."
        : context.Scenario == ProcessingScenario.FedRejects ? "FedNow rejection learning scenario."
        : null;
}

public interface IFedNowMessageService
{
    FedNowValidationResult Validate(string xml, decimal? amount = null);
    bool RequiresReceiptAcknowledgement(string messageType);
    IReadOnlyList<string> GetResponseMessageTypes(string messageType);
}

public sealed class FedNowMessageService(IIsoMessageService iso) : IFedNowMessageService
{
    public FedNowValidationResult Validate(string xml, decimal? amount = null)
    {
        var result = iso.Validate(xml);
        var errors = result.Errors.ToList();
        if (result.MessageType != "unknown" && !FedNowProfile.TryGetRule(result.MessageType, out _))
            errors.Add($"{result.MessageType} is not in the FedNow message profile.");
        if (amount is <= 0)
            errors.Add("FedNow transfer amount must be positive.");
        if (result.MessageType == "pacs.008" && amount > FedNowProfile.CustomerCreditTransferLimit)
            errors.Add($"FedNow customer credit transfers cannot exceed {FedNowProfile.CustomerCreditTransferLimit:C0}.");
        return new(errors.Count == 0, result.MessageType, errors);
    }

    public bool RequiresReceiptAcknowledgement(string messageType) =>
        FedNowProfile.TryGetRule(messageType, out var rule) && rule.RequiresReceiptAcknowledgement;

    public IReadOnlyList<string> GetResponseMessageTypes(string messageType) =>
        FedNowProfile.TryGetRule(messageType, out var rule)
            ? rule.ResponseMessageTypes ?? []
            : [];
}
