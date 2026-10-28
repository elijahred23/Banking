using Banking.Domain;

namespace Banking.Web.Models;

public sealed record StudioOperationItem(Guid Id, string Label, string Status, decimal Amount,
    string Bucket, string Controller, DateTimeOffset CreatedAt);

// Presentation categories summarize recorded states; they never authorize or transition payments.
public sealed record StudioOperationsViewModel(IReadOnlyList<StudioOperationItem> Items, string Scope)
{
    public static string Bucket(WireStatus status) => status switch
    {
        WireStatus.PendingComplianceReview or WireStatus.Rejected or WireStatus.Returned => "review",
        WireStatus.Settled or WireStatus.Completed => "settled",
        _ => "processing"
    };

    public static string Bucket(AchEntryStatus status) => status switch
    {
        AchEntryStatus.Rejected or AchEntryStatus.Returned or AchEntryStatus.NocReceived => "review",
        AchEntryStatus.Settled or AchEntryStatus.Posted => "settled",
        _ => "processing"
    };

    public static StudioOperationsViewModel From(TrafficDashboardViewModel model) => new(
        model.Wires.Select(x => new StudioOperationItem(x.Id, $"{x.SenderName} → {x.ReceiverName}",
            x.Status.ToString(), x.Amount, Bucket(x.Status), "Wires", x.CreatedDate)).ToList(), "latest 40 generated wires visible at this bank");

    public static StudioOperationsViewModel From(AchTrafficDashboardViewModel model) => new(
        model.Entries.Select(x => new StudioOperationItem(x.Id, $"{x.CompanyName} → {x.ReceiverName}",
            x.Status.ToString(), x.Amount, Bucket(x.Status), "Ach", x.CreatedDate)).ToList(), "latest 80 generated entries visible at this bank");
}
