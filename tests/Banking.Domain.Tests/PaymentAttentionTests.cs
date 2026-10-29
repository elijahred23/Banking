using Banking.Domain;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class PaymentAttentionTests
{
    [Fact]
    public void ClockSkipsWeekendAndUsesEarliestEligibleDate()
    {
        var now = new DateTimeOffset(2026, 10, 30, 15, 0, 0, TimeSpan.Zero);
        var result = LabClockPolicy.Next(now, [new(2026, 11, 4), new(2026, 11, 2), new(2026, 10, 31)]);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), result.At);
        Assert.Equal(result.At, LabClockPolicy.Next(now, []).At);
    }

    [Fact]
    public void ClockNeverRewindsOrStopsOnCurrentDate()
    {
        var now = new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero),
            LabClockPolicy.Next(now, [new(2026, 11, 9), new(2026, 11, 10)]).At);
    }

    [Theory]
    [InlineData(AchTransactionCode.CheckingCredit, -100)]
    [InlineData(AchTransactionCode.CheckingDebit, 100)]
    [InlineData(AchTransactionCode.SavingsCredit, -100)]
    [InlineData(AchTransactionCode.SavingsDebit, 100)]
    public void SettlementMovementRespectsCreditAndDebitDirection(AchTransactionCode code, int expected)
    {
        var origin = Guid.NewGuid(); var receiver = Guid.NewGuid();
        var entry = new AchEntry { OriginatingBankId = origin, ReceivingBankId = receiver, Amount = 100, TransactionCode = code };
        Assert.Equal(expected, PaymentAttentionPolicy.AchSettlementMovement(origin, entry));
        Assert.Equal(-expected, PaymentAttentionPolicy.AchSettlementMovement(receiver, entry));
        Assert.Equal(0, PaymentAttentionPolicy.AchSettlementMovement(Guid.NewGuid(), entry));
        entry.ReceivingBankId = origin;
        Assert.Equal(0, PaymentAttentionPolicy.AchSettlementMovement(origin, entry));
    }

    [Fact]
    public void FutureBatchExplainsClockWithoutCallingDraftSubmitted()
    {
        var entry = new AchEntry { Status = AchEntryStatus.Batched, EffectiveEntryDate = new(2026, 11, 2) };
        var result = PaymentAttentionPolicy.For(entry, new(2026, 10, 30));
        Assert.Contains("future", result.Title);
        Assert.Contains("2026-11-02", result.Action);
        Assert.Contains("not submission", result.Funds);
        Assert.Contains("cutoff", PaymentAttentionPolicy.For(entry, new(2026, 11, 2)).Title);
    }

    [Fact]
    public void NocAndReturnHaveDifferentAccountingExplanations()
    {
        var correction = PaymentAttentionPolicy.For(new AchEntry { Status = AchEntryStatus.NocReceived });
        var returned = PaymentAttentionPolicy.For(new AchEntry { Status = AchEntryStatus.Returned });
        Assert.Contains("future", correction.Action);
        Assert.Contains("reversing", returned.Evidence);
        Assert.NotEqual(correction.Funds, returned.Funds);
    }

    [Fact]
    public void EveryRecordedStatusHasAnActionOwnerAndEvidence()
    {
        foreach (var status in Enum.GetValues<WireStatus>())
        {
            var wire = new WireTransfer { Status = status, SenderName = "Sender", ReceiverName = "Receiver", BeneficiaryAccountNumber = "123" };
            var attention = PaymentAttentionPolicy.For(wire);
            Assert.False(string.IsNullOrWhiteSpace(attention.Owner));
            Assert.False(string.IsNullOrWhiteSpace(attention.Evidence));
            Assert.False(string.IsNullOrWhiteSpace(attention.Action));
        }
        foreach (var status in Enum.GetValues<AchEntryStatus>())
            Assert.False(string.IsNullOrWhiteSpace(PaymentAttentionPolicy.For(new AchEntry { Status = status }).Action));
    }
}
