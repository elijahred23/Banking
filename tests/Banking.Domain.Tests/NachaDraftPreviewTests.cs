using Banking.Domain;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class NachaDraftPreviewTests
{
    [Fact]
    public void Future_dated_batch_can_be_inspected_without_assigning_durable_traces_or_changing_state()
    {
        var bank = new Bank { Name = "Lab origin", RoutingNumber = "101000019", FedParticipantId = "LAB",
            Bic = "LABBUS33XXX", TownName = "Tulsa", CountryCode = "US" };
        var batch = new AchBatch { OriginatingBankId = bank.Id, CompanyName = "LAB PAYROLL", CompanyId = "1234567890",
            EffectiveEntryDate = new DateOnly(2026, 7, 6), SecCode = AchStandardEntryClass.Ppd };
        var entry = new AchEntry { OriginatingBankId = bank.Id, ReceiverName = "Fictional employee",
            ReceivingRoutingNumber = "103000648", ReceivingAccountNumber = "654321", Amount = 1234.56m,
            TransactionCode = AchTransactionCode.CheckingCredit, EntryDescription = "PAYROLL", Addenda05 = "LAB PAYDAY",
            Status = AchEntryStatus.Batched, EffectiveEntryDate = batch.EffectiveEntryDate };
        batch.Entries.Add(entry);
        var payload = new NachaDraftPreview(new NachaFileWriter()).Create(bank, batch,
            new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero));
        var validation = new NachaFileParser().Validate(payload);
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));
        Assert.Equal(1234.56m, validation.TotalCredits);
        Assert.Equal(1, validation.EntryCount);
        Assert.All(payload.Split(Environment.NewLine), x => Assert.Equal(94, x.Length));
        Assert.Contains(NachaRecordAnnotator.Annotate(payload), x => x.Type == '7');
        Assert.Contains(NachaRecordAnnotator.Annotate(payload), x => x.Name == "File control");
        Assert.Null(entry.TraceNumber);
        Assert.Null(batch.AchFileId);
        Assert.Equal("Open", batch.Status);
        Assert.Equal(AchEntryStatus.Batched, entry.Status);
        bank.Id = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() => new NachaDraftPreview(new NachaFileWriter()).Create(bank, batch, DateTimeOffset.UtcNow));
    }
}
