namespace Banking.Domain;

public sealed record AchProcessingWindow(string Name, TimeOnly Cutoff, bool SameDay,
    DateOnly SettlementDate);

public static class AchOperatingPolicy
{
    private static readonly (string Name, TimeOnly Cutoff, bool SameDay)[] Windows =
    [
        ("Morning", new TimeOnly(10, 30), true),
        ("Afternoon", new TimeOnly(14, 45), true),
        ("Evening", new TimeOnly(17, 0), false)
    ];

    public static IReadOnlyList<AchProcessingWindow> For(DateTimeOffset simulatedNow) => Windows
        .Select(x => new AchProcessingWindow(x.Name, x.Cutoff, x.SameDay,
            x.SameDay ? DateOnly.FromDateTime(simulatedNow.UtcDateTime)
                : AchBusinessCalendar.NextBankingDay(DateOnly.FromDateTime(simulatedNow.UtcDateTime))))
        .ToList();

    public static DateOnly ReturnDeadline(DateOnly settlementDate, int bankingDays)
    {
        var result = settlementDate;
        for (var i = 0; i < bankingDays; i++) result = AchBusinessCalendar.NextBankingDay(result);
        return result;
    }
}

public sealed record CheckAvailabilitySchedule(decimal ImmediateAmount,
    decimal DeferredAmount, DateOnly AvailableDate, string Explanation);

public static class CheckAvailabilityPolicy
{
    public static CheckAvailabilitySchedule Standard(decimal amount, DateOnly depositDate)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        var immediate = Math.Min(amount, 225m);
        return new(immediate, amount - immediate, AchBusinessCalendar.NextBankingDay(depositDate),
            "Teaching schedule only: provisional credit remains subject to collection and return.");
    }
}

public static class FedwireOperatingPolicy
{
    public static bool IsTeachingWindow(DateTimeOffset now) =>
        AchBusinessCalendar.IsBankingDay(DateOnly.FromDateTime(now.UtcDateTime))
        && TimeOnly.FromDateTime(now.UtcDateTime) < new TimeOnly(23, 59);
}

public sealed record FedNowTimeoutDecision(bool TimedOut, string NextAction);

public static class FedNowOperationsPolicy
{
    public static FedNowTimeoutDecision EvaluateReceiverTimeout(DateTimeOffset sent,
        DateTimeOffset now, TimeSpan threshold) => now - sent >= threshold
        ? new(true, "Open a correlated investigation; do not infer settlement or send a duplicate payment.")
        : new(false, "Continue monitoring the correlated receiver status.");
}

public enum SwiftChargeBearer { Our, Shared, Beneficiary }
public enum SwiftSettlementMethod { Serial, Cover }

public sealed record SwiftSettlementQuote(string SourceCurrency, string TargetCurrency,
    decimal SourceAmount, decimal ExchangeRate, decimal Fee, SwiftChargeBearer Charges,
    SwiftSettlementMethod Method)
{
    public decimal ConvertedAmount => decimal.Round(SourceAmount * ExchangeRate, 2);
    public decimal BeneficiaryAmount => Charges == SwiftChargeBearer.Beneficiary
        ? ConvertedAmount - Fee : ConvertedAmount;
    public decimal DebtorTotal => Charges == SwiftChargeBearer.Our
        ? SourceAmount + Fee : SourceAmount;
}

public static class SwiftSettlementPolicy
{
    public static void Validate(SwiftSettlementQuote quote)
    {
        if (quote.SourceAmount <= 0 || quote.ExchangeRate <= 0 || quote.Fee < 0
            || quote.BeneficiaryAmount < 0)
            throw new InvalidOperationException("FX amount, rate, and correspondent fees must produce positive settlement values.");
    }
}
