using System.ComponentModel.DataAnnotations;
using Banking.Domain;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Banking.Web.Models;

public sealed class CreateWireViewModel
{
    [Required] public Guid FromAccountId { get; set; }
    [Required] public Guid ReceiverBankId { get; set; }
    [Required, StringLength(120)] public string ReceiverName { get; set; } = "";
    [Range(typeof(decimal), "0.01", "999999999.99")] public decimal Amount { get; set; }
    public List<SelectListItem> Accounts { get; set; } = [];
    public List<SelectListItem> Banks { get; set; } = [];
}

public sealed record DashboardViewModel(Bank Bank, IReadOnlyList<WireTransfer> RecentWires,
    int IncomingCount, int OutgoingCount, decimal OutgoingTotal);

public sealed record WireIndexViewModel(Bank Bank, WireDirection? Direction,
    IReadOnlyList<WireTransfer> Wires);
