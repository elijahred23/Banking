using System.Text.RegularExpressions;

namespace Banking.Domain;

public sealed record IsoMessageDefinition(
    string MessageType,
    string DefaultMessageDefinitionId,
    string Purpose,
    bool IsBusinessApplicationHeader = false);

/// <summary>
/// ISO 20022 message types supported by the lab. A caller may use the default
/// definition identifier or any versioned identifier for a supported base type.
/// Scheme-specific usage guidelines remain responsible for selecting a version.
/// </summary>
public static partial class IsoMessageCatalog
{
    private static readonly IReadOnlyDictionary<string, IsoMessageDefinition> Definitions =
        new IsoMessageDefinition[]
        {
            new("head.001", "head.001.001.02", "Business Application Header", true),
            new("admi.002", "admi.002.001.01", "Technical or business error"),
            new("admi.004", "admi.004.001.02", "System event or connection check"),
            new("admi.006", "admi.006.001.01", "Retrieval request"),
            new("admi.007", "admi.007.001.01", "Positive or negative acknowledgement"),
            new("admi.011", "admi.011.001.01", "Connection check response"),
            new("admi.998", "admi.998.001.02", "FedNow participant file"),
            new("pacs.002", "pacs.002.001.10", "FI-to-FI payment status report"),
            new("pacs.003", "pacs.003.001.08", "FI customer direct debit"),
            new("pacs.004", "pacs.004.001.09", "Payment return"),
            new("pacs.007", "pacs.007.001.09", "Payment reversal"),
            new("pacs.008", "pacs.008.001.08", "FI-to-FI customer credit transfer"),
            new("pacs.009", "pacs.009.001.08", "Financial institution credit transfer"),
            new("pacs.010", "pacs.010.001.03", "Financial institution direct debit"),
            new("pacs.028", "pacs.028.001.03", "Payment status request"),
            new("pain.001", "pain.001.001.09", "Customer credit transfer initiation"),
            new("pain.002", "pain.002.001.10", "Customer payment status report"),
            new("pain.007", "pain.007.001.09", "Customer payment reversal"),
            new("pain.008", "pain.008.001.08", "Customer direct debit initiation"),
            new("pain.013", "pain.013.001.07", "Drawdown request"),
            new("pain.014", "pain.014.001.07", "Drawdown request response"),
            new("camt.026", "camt.026.001.09", "Unable to apply"),
            new("camt.027", "camt.027.001.09", "Claim non-receipt"),
            new("camt.028", "camt.028.001.11", "Claim response"),
            new("camt.029", "camt.029.001.09", "Resolution of investigation"),
            new("camt.052", "camt.052.001.08", "Bank-to-customer account report"),
            new("camt.053", "camt.053.001.08", "Bank statement"),
            new("camt.054", "camt.054.001.08", "Bank debit or credit notification"),
            new("camt.055", "camt.055.001.08", "Customer payment cancellation request"),
            new("camt.056", "camt.056.001.08", "FI payment cancellation request"),
            new("camt.057", "camt.057.001.06", "Notification to receive"),
            new("camt.058", "camt.058.001.07", "Notification to receive cancellation"),
            new("camt.060", "camt.060.001.05", "Account reporting request"),
            new("camt.087", "camt.087.001.07", "Account reporting response"),
            new("camt.110", "camt.110.001.01", "Investigation request")
        }.ToDictionary(x => x.MessageType, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<IsoMessageDefinition> All { get; } = Definitions.Values.ToArray();

    public static bool TryResolve(string identifier, out IsoMessageDefinition definition,
        out string messageDefinitionId)
    {
        definition = null!;
        messageDefinitionId = string.Empty;
        if (string.IsNullOrWhiteSpace(identifier)) return false;

        var value = identifier.Trim();
        var match = MessageIdentifier().Match(value);
        if (!match.Success || !Definitions.TryGetValue(match.Groups["type"].Value, out definition!))
            return false;

        messageDefinitionId = match.Groups["version"].Success
            ? value.ToLowerInvariant()
            : definition.DefaultMessageDefinitionId;
        return true;
    }

    [GeneratedRegex("^(?<type>(?:head|admi|pacs|pain|camt)\\.\\d{3})(?<version>\\.\\d{3}\\.\\d{2})?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MessageIdentifier();
}
