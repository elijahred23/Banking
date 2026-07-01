using System.ComponentModel.DataAnnotations;
using Banking.Domain;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Banking.Web.Models;

public sealed class CreateWireViewModel
{
    public WireTransferType TransferType { get; set; }
    public Guid? FromAccountId { get; set; }
    [Required] public Guid ReceiverBankId { get; set; }
    [StringLength(120)] public string? ReceiverName { get; set; }
    [StringLength(34, MinimumLength = 4)] public string? BeneficiaryAccountNumber { get; set; }
    [Range(typeof(decimal), "0.01", "999999999.99")] public decimal Amount { get; set; }
    public PaymentRail Rail { get; set; }
    public ProcessingScenario Scenario { get; set; }
    [Required, StringLength(35, MinimumLength = 3)] public string CustomerReference { get; set; }
        = $"PAY-{Guid.NewGuid():N}"[..20];
    public List<SelectListItem> Accounts { get; set; } = [];
    public List<SelectListItem> Banks { get; set; } = [];
    public List<SelectListItem> Rails { get; set; } = [];
    public string SenderBankName { get; set; } = "";
    public decimal SenderMasterAccountBalance { get; set; }
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
    TimeSpan ProcessingDuration,
    IReadOnlyList<WireIsoMessageDefinition> SupportedMessages);

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

public sealed class MessageWorkflowsViewModel
{
    public required Bank Bank { get; init; }
    public RequestForPaymentViewModel RequestForPayment { get; init; } = new();
    public AccountReportRequestViewModel AccountReport { get; init; } = new();
    public SystemEventViewModel SystemEvent { get; init; } = new();
    public List<SelectListItem> Accounts { get; init; } = [];
    public List<SelectListItem> Banks { get; init; } = [];
    public List<SelectListItem> FedNowBanks { get; init; } = [];
    public IReadOnlyList<MessageExchange> Exchanges { get; init; } = [];
}

public sealed class RequestForPaymentViewModel
{
    [Required] public Guid CreditorAccountId { get; set; }
    [Required] public Guid DebtorBankId { get; set; }
    [Required, StringLength(120)] public string DebtorName { get; set; } = "";
    [Required, StringLength(34, MinimumLength = 4)] public string DebtorAccount { get; set; } = "";
    [Range(typeof(decimal), "0.01", "999999999.99")] public decimal Amount { get; set; }
    [Required, StringLength(140)] public string Remittance { get; set; } = "";
    public PaymentRail Rail { get; set; } = PaymentRail.Fedwire;
}

public sealed class AccountReportRequestViewModel
{
    public PaymentRail Rail { get; set; } = PaymentRail.FedNow;
    [Required] public string ReportType { get; set; } = "Account balance";
    public DateOnly BusinessDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
}

public sealed class SystemEventViewModel
{
    [Required] public Guid RecipientBankId { get; set; }
    [Required, StringLength(35)] public string EventCode { get; set; } = "PARTICIPANT_NOTICE";
    [Required, StringLength(500, MinimumLength = 10)] public string Details { get; set; } = "";
}
