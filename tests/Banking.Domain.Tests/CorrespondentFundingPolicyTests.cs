using Banking.Domain;
using Xunit;
namespace Banking.Domain.Tests;
public sealed class CorrespondentFundingPolicyTests
{
    [Theory]
    [InlineData(CorrespondentFundingStrategy.Threshold, 900000, 150000, 250000, 1000000, 500000, 500000)]
    [InlineData(CorrespondentFundingStrategy.Threshold, 100000, 150000, 250000, 1000000, 500000, 100000)]
    [InlineData(CorrespondentFundingStrategy.Threshold, 900000, 250000, 250000, 1000000, 500000, 0)]
    [InlineData(CorrespondentFundingStrategy.Sweep, 600000, 0, 0, 250000, 500000, 350000)]
    [InlineData(CorrespondentFundingStrategy.Sweep, 200000, 0, 0, 250000, 500000, 0)]
    [InlineData(CorrespondentFundingStrategy.Fixed, 0, 0, 0, 0, 250000, 0)]
    public void Funding_respects_source_liquidity_trigger_retention_and_caps(CorrespondentFundingStrategy strategy,
        decimal source, decimal destination, decimal threshold, decimal target, decimal maximum, decimal expected) =>
        Assert.Equal(expected, CorrespondentFundingPolicy.Calculate(strategy, source, destination, threshold, target, maximum));
    [Fact]
    public void Coalesced_schedule_skips_weekend_and_keeps_UTC_time()
    {
        var friday = new DateTimeOffset(2026, 11, 13, 14, 0, 0, TimeSpan.Zero);
        Assert.Equal(friday.AddDays(3), CorrespondentFundingPolicy.NextWeekday(friday));
    }
}
