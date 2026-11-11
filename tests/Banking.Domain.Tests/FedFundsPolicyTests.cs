using Banking.Domain;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class FedFundsPolicyTests
{
    [Theory]
    [InlineData(1, 93.75)]
    [InlineData(3, 281.25)]
    [InlineData(4, 375)]
    public void Actual_calendar_days_use_decimal_act_360(int days, decimal expected)
    {
        var from = new DateOnly(2026, 11, 12);
        Assert.Equal(expected, FedFundsInterestCalculator.Calculate(750_000, 4.50m, from, from.AddDays(days)));
    }
    [Fact]
    public void Weekend_and_configured_holiday_extend_overnight_term()
    {
        var friday = new DateOnly(2026, 11, 13);
        Assert.Equal(new DateOnly(2026, 11, 16), FedFundsPolicy.NextBusinessDay(friday, []));
        Assert.Equal(new DateOnly(2026, 11, 17), FedFundsPolicy.NextBusinessDay(friday, [new(2026, 11, 16)]));
    }
    [Fact]
    public void Invalid_terms_and_terminal_state_edits_are_rejected()
    {
        var trade = new FedFundsTrade { LenderBankId = Guid.NewGuid(), BorrowerBankId = Guid.NewGuid(),
            Principal = 750_000, AnnualRate = 4.5m, ValueDate = new(2026, 11, 12), MaturityDate = new(2026, 11, 13) };
        FedFundsPolicy.Validate(trade, []);
        trade.BorrowerBankId = trade.LenderBankId;
        Assert.Throws<InvalidOperationException>(() => FedFundsPolicy.Validate(trade, []));
        trade.Status = FedFundsTradeStatus.Repaid;
        Assert.Throws<InvalidOperationException>(() => FedFundsPolicy.Transition(trade, FedFundsTradeStatus.Active));
        trade.Status = FedFundsTradeStatus.Active;
        Assert.Throws<InvalidOperationException>(() => FedFundsPolicy.Transition(trade, FedFundsTradeStatus.Cancelled));
    }
    [Fact]
    public void Credit_reservation_blocks_overcommitment_and_suspension()
    {
        var line = new FedFundsCreditLine { ApprovedLimit = 1_000_000 };
        FedFundsPolicy.Reserve(line, 750_000);
        Assert.Throws<InvalidOperationException>(() => FedFundsPolicy.Reserve(line, 750_000));
        Assert.Equal(750_000m, line.ReservedAmount);
        line.IsActive = false;
        Assert.Throws<InvalidOperationException>(() => FedFundsPolicy.Reserve(line, 1));
    }
    [Fact]
    public void Institution_payment_xml_preserves_four_decimal_cash_and_simulated_value_date()
    {
        var sender = new Bank { Name = "Sender", RoutingNumber = "990000001", FedParticipantId = "LABA",
            Bic = "LABAUS44XXX", TownName = "Synthetic city", CountryCode = "US" };
        var receiver = new Bank { Name = "Receiver", RoutingNumber = "990000002", FedParticipantId = "LABB",
            Bic = "LABBUS44XXX", TownName = "Synthetic city", CountryCode = "US" };
        var wire = new WireTransfer { Amount = 93.7512m, SenderName = sender.Name, ReceiverName = receiver.Name,
            BeneficiaryAccountNumber = receiver.FedParticipantId, CreatedDate = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero) };
        var iso = new IsoMessageService();
        var xml = iso.CreatePacs009(wire, sender, receiver);
        Assert.Contains(">93.7512<", xml);
        Assert.Contains(">2026-11-13<", xml);
        Assert.True(iso.Validate(xml).IsValid);
    }

    [Fact]
    public void Daily_cumulative_deltas_preserve_total_interest()
    {
        var start = new DateOnly(2026, 11, 13);
        decimal total = 0;
        for (var i = 0; i < 3; i++) total += FedFundsInterestCalculator.Calculate(123_456.78m, 4.12345678m, start, start.AddDays(i + 1))
            - FedFundsInterestCalculator.Calculate(123_456.78m, 4.12345678m, start, start.AddDays(i));
        Assert.Equal(FedFundsInterestCalculator.Calculate(123_456.78m, 4.12345678m, start, start.AddDays(3)), total);
    }
}
