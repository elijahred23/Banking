namespace Banking.Domain;

public sealed record WireScreeningResult(bool RequiresReview, string? Reason,
    int RiskScore = 0, IReadOnlyList<string>? Indicators = null);

public interface IWireComplianceService
{
    WireScreeningResult Screen(WireTransfer wire, Bank sender, Bank receiver);
}

public sealed class WireComplianceService : IWireComplianceService
{
    private static readonly string[] ReviewTerms = ["BLOCKED", "SANCTIONED", "WATCHLIST"];

    public WireScreeningResult Screen(WireTransfer wire, Bank sender, Bank receiver)
    {
        var values = new[] { wire.SenderName, wire.ReceiverName, sender.Name, receiver.Name,
            wire.CustomerReference ?? string.Empty };
        var indicators = new List<string>();
        var hit = ReviewTerms.FirstOrDefault(term => values.Any(value =>
            value.Contains(term, StringComparison.OrdinalIgnoreCase)));
        if (hit is not null) indicators.Add($"Exact synthetic watchlist term: {hit}");
        if (wire.Amount >= 1_000_000m) indicators.Add("High-value payment");
        if (wire.Rail == PaymentRail.SwiftCbprPlus) indicators.Add("Cross-border routing");
        if (Similar(wire.ReceiverName, "SANCTIONED PARTY")) indicators.Add("Fuzzy synthetic-name match");
        var score = indicators.Sum(x => x.StartsWith("Exact", StringComparison.Ordinal) ? 80
            : x.StartsWith("Fuzzy", StringComparison.Ordinal) ? 60 : 15);
        return score < 50
            ? new(false, indicators.Count == 0 ? null : string.Join("; ", indicators), score, indicators)
            : new(true, $"Synthetic teaching-data alert ({score}/100): {string.Join("; ", indicators)}. Human disposition required.",
                Math.Min(100, score), indicators);
    }

    private static bool Similar(string left, string right)
    {
        var a = new string(left.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var b = new string(right.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (a.Length < 5 || b.Length < 5) return false;
        var common = a.Intersect(b).Count();
        return common >= Math.Min(a.Distinct().Count(), b.Distinct().Count()) * 0.8;
    }
}
