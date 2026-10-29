using Banking.Domain;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class OperationalJobTests
{
    [Fact]
    public void AllTwelveJobKindsHaveDescriptionsAndValidSchedules()
    {
        var jobs = Enum.GetValues<BankReportKind>().Where(ReportSchedulePolicy.IsOperational).ToList();
        Assert.Equal(12, jobs.Count);
        foreach (var kind in jobs)
        {
            Assert.True(kind.ToString().Length <= 24);
            Assert.False(string.IsNullOrWhiteSpace(ReportSchedulePolicy.Title(kind)));
            Assert.False(string.IsNullOrWhiteSpace(ReportSchedulePolicy.Description(kind)));
            Assert.True(Enum.IsDefined(ReportSchedulePolicy.DefaultFrequency(kind)));
        }
    }
    [Fact]
    public void StuckDetectionUsesLastWireEventAndSkipsFutureAchAndApprovals()
    {
        var now = DateTimeOffset.UtcNow;
        var wire = Wire(); wire.Status = WireStatus.SentToFed;
        Assert.True(OperationalJobPolicy.IsStuck(wire, now.AddMinutes(-16), now));
        Assert.False(OperationalJobPolicy.IsStuck(wire, now.AddMinutes(-1), now));
        wire.Status = WireStatus.PendingApproval;
        Assert.False(OperationalJobPolicy.IsStuck(wire, now.AddDays(-1), now));
        var ach = new AchEntry { Status = AchEntryStatus.Batched, CreatedDate = now.AddHours(-3), EffectiveEntryDate = new(2026, 11, 2) };
        Assert.False(OperationalJobPolicy.IsStuck(ach, now, new(2026, 10, 30)));
        Assert.True(OperationalJobPolicy.IsStuck(ach, now, new(2026, 11, 2)));
    }
    [Fact]
    public void DuplicateFingerprintKeepsRailAccountDirectionAndEffectiveDateDistinct()
    {
        var first = Wire(); var second = Wire();
        Assert.Equal(OperationalJobPolicy.DuplicateKey(first), OperationalJobPolicy.DuplicateKey(second));
        second.Rail = PaymentRail.FedNow;
        Assert.NotEqual(OperationalJobPolicy.DuplicateKey(first), OperationalJobPolicy.DuplicateKey(second));
        var credit = new AchEntry { Amount = 20, TransactionCode = AchTransactionCode.CheckingCredit, EffectiveEntryDate = new(2026, 10, 29), ReceivingAccountNumber = "123" };
        var debit = new AchEntry { Amount = 20, TransactionCode = AchTransactionCode.CheckingDebit, EffectiveEntryDate = credit.EffectiveEntryDate, ReceivingAccountNumber = "123" };
        Assert.NotEqual(OperationalJobPolicy.DuplicateKey(credit), OperationalJobPolicy.DuplicateKey(debit));
        debit.TransactionCode = credit.TransactionCode;
        Assert.Equal(OperationalJobPolicy.DuplicateKey(credit), OperationalJobPolicy.DuplicateKey(debit));
        debit.EffectiveEntryDate = debit.EffectiveEntryDate.AddDays(1);
        Assert.NotEqual(OperationalJobPolicy.DuplicateKey(credit), OperationalJobPolicy.DuplicateKey(debit));
    }
    private static WireTransfer Wire() => new() { SenderName = "Sender", ReceiverName = "Receiver", BeneficiaryAccountNumber = "123", Amount = 20 };
}
