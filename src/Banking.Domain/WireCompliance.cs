namespace Banking.Domain;

public sealed record WireScreeningResult(bool RequiresReview, string? Reason);

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
        var hit = ReviewTerms.FirstOrDefault(term => values.Any(value =>
            value.Contains(term, StringComparison.OrdinalIgnoreCase)));
        return hit is null
            ? new(false, null)
            : new(true, $"Potential sanctions/watchlist term '{hit}' requires human disposition.");
    }
}
