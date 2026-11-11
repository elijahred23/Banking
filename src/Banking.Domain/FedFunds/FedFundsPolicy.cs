namespace Banking.Domain;

public static class FedFundsInterestCalculator
{
    public static decimal Calculate(decimal principal, decimal annualRate, DateOnly from, DateOnly through)
    {
        if (principal <= 0 || annualRate < 0 || annualRate > 100 || through < from)
            throw new InvalidOperationException("Invalid ACT/360 interest inputs.");
        return decimal.Round(principal * annualRate / 100m * (through.DayNumber - from.DayNumber) / 360m,
            4, MidpointRounding.AwayFromZero);
    }
}
public static class FedFundsPolicy
{
    public static bool IsBusinessDay(DateOnly date, IReadOnlyCollection<DateOnly> holidays) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !holidays.Contains(date);
    public static DateOnly NextBusinessDay(DateOnly valueDate, IReadOnlyCollection<DateOnly> holidays)
    {
        var date = valueDate.AddDays(1);
        while (!IsBusinessDay(date, holidays)) date = date.AddDays(1);
        return date;
    }
    public static bool Owns(FedFundsTrade trade, Guid bankId) =>
        trade.LenderBankId == bankId || trade.BorrowerBankId == bankId;
    public static void Validate(FedFundsTrade trade, IReadOnlyCollection<DateOnly> holidays)
    {
        if (trade.LenderBankId == trade.BorrowerBankId || trade.Principal <= 0 || decimal.Round(trade.Principal, 4) != trade.Principal
            || trade.AnnualRate < 0 || trade.AnnualRate > 100
            || !IsBusinessDay(trade.ValueDate, holidays)
            || trade.MaturityDate != NextBusinessDay(trade.ValueDate, holidays))
            throw new InvalidOperationException("Overnight USD terms require distinct eligible banks, positive principal, a 0–100% rate, and next-business-day maturity.");
    }
    public static void Transition(FedFundsTrade trade, FedFundsTradeStatus next)
    {
        if (trade.Status == next) return;
        var allowed = trade.Status switch {
            FedFundsTradeStatus.Proposed => next is FedFundsTradeStatus.PendingApprovals or FedFundsTradeStatus.Rejected or FedFundsTradeStatus.Cancelled,
            FedFundsTradeStatus.PendingApprovals => next is FedFundsTradeStatus.FundingPending or FedFundsTradeStatus.Rejected or FedFundsTradeStatus.Cancelled,
            FedFundsTradeStatus.FundingPending => next is FedFundsTradeStatus.Active or FedFundsTradeStatus.FundingException,
            FedFundsTradeStatus.FundingException => next is FedFundsTradeStatus.FundingPending or FedFundsTradeStatus.Cancelled,
            FedFundsTradeStatus.Active => next is FedFundsTradeStatus.MaturityDue,
            FedFundsTradeStatus.MaturityDue => next is FedFundsTradeStatus.Repaid or FedFundsTradeStatus.Overdue,
            FedFundsTradeStatus.Overdue => next is FedFundsTradeStatus.Repaid,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"Trade cannot transition from {trade.Status} to {next}.");
        trade.Status = next;
    }
    public static void Reserve(FedFundsCreditLine line, decimal principal)
    {
        if (!line.IsActive || principal <= 0 || line.ReservedAmount + principal > line.ApprovedLimit)
            throw new InvalidOperationException("The lender's active counterparty credit limit is insufficient.");
        line.ReservedAmount += principal;
    }
}
public sealed class FedFundsOptions
{
    public decimal ReferenceRate { get; set; } = 4.50m;
    public DateOnly[] Holidays { get; set; } = [];
}
