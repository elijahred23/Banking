using System.ComponentModel.DataAnnotations;
using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Banking.Web.Models;

public sealed class CreateFedFundsViewModel
{
    [Required] public Guid CounterpartyBankId { get; set; }
    public bool Borrow { get; set; } = true;
    [Range(typeof(decimal), "0.0001", "999999999999999")] public decimal Principal { get; set; } = 750_000;
    [Required, Range(typeof(decimal), "0", "100")] public decimal? AnnualRate { get; set; }
    [DataType(DataType.Date)] public DateOnly ValueDate { get; set; }
    public IReadOnlyList<SelectListItem> Banks { get; set; } = [];
}
public sealed class FedFundsQuoteViewModel
{
    public FedFundsQuoteSide Side { get; set; }
    [Range(typeof(decimal), "0.0001", "999999999999999")] public decimal MinAmount { get; set; }
    [Range(typeof(decimal), "0.0001", "999999999999999")] public decimal MaxAmount { get; set; }
    [Range(typeof(decimal), "0", "100")] public decimal Rate { get; set; }
}
public sealed class FedFundsCreditViewModel
{
    public Guid BorrowerBankId { get; set; }
    [Range(typeof(decimal), "0", "999999999999999")] public decimal ApprovedLimit { get; set; }
    public bool IsActive { get; set; } = true;
}
public sealed record FedFundsDashboardViewModel(Bank Bank, DateOnly BusinessDate, decimal ReferenceRate,
    IReadOnlyList<FedFundsPosition> Positions, IReadOnlyList<FedFundsCreditLine> CreditLines,
    IReadOnlyDictionary<Guid, string> BankNames, FedFundsLiquidity Liquidity);
public sealed record FedFundsTradeDeskViewModel(Guid BankId, DateTimeOffset Now, decimal ReferenceRate,
    IReadOnlyList<FedFundsQuote> Quotes, IReadOnlyDictionary<Guid, string> BankNames);
public sealed record FedFundsDetailsViewModel(Guid BankId, FedFundsTrade Trade,
    IReadOnlyDictionary<Guid, string> BankNames, IReadOnlyList<LedgerEntry> Journals,
    IReadOnlyDictionary<Guid, Guid> OwnWires);
