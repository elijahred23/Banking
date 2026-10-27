using Banking.Domain;

namespace Banking.Web.Models;

public sealed record AchTrafficDashboardViewModel(Bank Bank, SimulationState? State,
    IReadOnlyList<Account> Accounts, IReadOnlyList<AchEntry> Entries,
    IReadOnlyDictionary<Guid, AchTrafficPosting> Postings, DateTimeOffset LabTime, DateTimeOffset CapturedAt);
public sealed record AchTrafficPosting(decimal Debits, decimal Credits, DateTimeOffset PostedAt);
