using Banking.Domain;

namespace Banking.Web.Models;

public sealed record TrafficDashboardViewModel(Bank Bank, SimulationState? State,
    IReadOnlyList<Account> Accounts, IReadOnlyList<WireTransfer> Wires,
    IReadOnlyDictionary<Guid, string> BankNames, DateTimeOffset CapturedAt);
