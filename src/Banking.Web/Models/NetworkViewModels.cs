using System.ComponentModel.DataAnnotations;

namespace Banking.Web.Models;

public sealed class ProvisionBankViewModel
{
    [Required, StringLength(120)] public string Name { get; set; } = "";
    [Required, RegularExpression(@"99\d{7}", ErrorMessage = "Use a synthetic nine-digit routing identifier beginning with 99.")]
    public string RoutingNumber { get; set; } = "";
    [Required, StringLength(12), RegularExpression("[A-Z0-9]+")]
    public string ParticipantId { get; set; } = "";
    [Required, RegularExpression("[A-Z]{4}US[A-Z0-9]{2}([A-Z0-9]{3})?", ErrorMessage = "Use a synthetic US BIC.")]
    public string Bic { get; set; } = "";
    [Range(typeof(decimal), "0", "1000000000")] public decimal MasterBalance { get; set; } = 1_000_000;
}

public sealed record NetworkParticipantViewModel(string Name, string RoutingNumber, string ParticipantId, decimal Balance);
public sealed record NetworkSettlementViewModel(Guid CorrelationId, string Sender, string Receiver,
    decimal Amount, string Status, string Imad, string Omad);
public sealed record NetworkOverviewViewModel(IReadOnlyList<NetworkParticipantViewModel> Banks,
    IReadOnlyList<NetworkSettlementViewModel> Settlements);
