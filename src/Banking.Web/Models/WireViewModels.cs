using System.ComponentModel.DataAnnotations;
using Banking.Domain;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Banking.Web.Models;

public sealed class CreateWireViewModel
{
    [Required] public Guid FromAccountId { get; set; }
    [Required] public Guid ReceiverBankId { get; set; }
    [Required, StringLength(120)] public string ReceiverName { get; set; } = "";
    [Required, StringLength(34, MinimumLength = 4)] public string BeneficiaryAccountNumber { get; set; } = "";
    [Range(typeof(decimal), "0.01", "999999999.99")] public decimal Amount { get; set; }
    public PaymentRail Rail { get; set; }
    public ProcessingScenario Scenario { get; set; }
    public List<SelectListItem> Accounts { get; set; } = [];
    public List<SelectListItem> Banks { get; set; } = [];
    public List<SelectListItem> Rails { get; set; } = [];
}

public sealed record DashboardViewModel(Bank Bank, IReadOnlyList<WireTransfer> RecentWires,
    int IncomingCount, int OutgoingCount, decimal OutgoingTotal);

public sealed record WireIndexViewModel(Bank Bank, WireDirection? Direction,
    IReadOnlyList<WireTransfer> Wires);

public sealed record WireInstructionsViewModel(
    Bank Bank,
    IReadOnlyList<WireInstructionAccountViewModel> SourceAccounts,
    IReadOnlyList<WireInstructionDestinationViewModel> DomesticDestinations,
    IReadOnlyList<WireInstructionDestinationViewModel> FedNowDestinations,
    IReadOnlyList<WireInstructionDestinationViewModel> InternationalDestinations);

public sealed record WireInstructionAccountViewModel(
    string CustomerName,
    string AccountNumber,
    decimal AvailableBalance);

public sealed record WireInstructionDestinationViewModel(
    string BankName,
    string CustomerName,
    string AccountNumber,
    string RoutingNumber,
    string Bic,
    string CountryCode);

public sealed record WireDetailsViewModel(
    WireTransfer Wire,
    PaymentRoute? Route,
    IReadOnlyList<MessageDelivery> Deliveries,
    IReadOnlyList<ProcessingStageViewModel> Stages,
    IReadOnlyList<IsoMessageViewModel> Messages,
    IReadOnlyList<LedgerEntry> LedgerEntries,
    IReadOnlyList<WireCase> Cases,
    bool CanRequestReturn,
    bool CanInvestigate,
    string? FailureReason,
    TimeSpan ProcessingDuration);

public sealed record ProcessingStageViewModel(
    WireEvent Event,
    string Service,
    TimeSpan? DurationSincePrevious);

public sealed record IsoMessageViewModel(
    IsoMessage Message,
    string FormattedXml,
    bool IsWellFormed,
    bool HasExpectedNamespace,
    string ValidationMessage);
