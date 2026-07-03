using Banking.Domain;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class AchTests
{
    [Theory]
    [InlineData("101000019", true)]
    [InlineData("103000648", true)]
    [InlineData("091000080", true)]
    [InlineData("990000000", false)]
    [InlineData("101000018", false)]
    [InlineData("123", false)]
    public void AbaRoutingNumberValidationUsesCheckDigit(string routing, bool expected) =>
        Assert.Equal(expected, AbaRoutingNumberValidator.IsValid(routing));

    [Fact]
    public void WriterCreatesValidBlockedNachaFile()
    {
        var bankId = Guid.NewGuid();
        var file = new AchFile
        {
            OriginatingBankId = bankId,
            OriginatingBank = new Bank { Id = bankId, Name = "Bankers Bank", RoutingNumber = "101000019",
                FedParticipantId = "BANKERS", Bic = "BAKRUS44XXX", TownName = "Tulsa", CountryCode = "US" },
            ImmediateDestinationRoutingNumber = "091000080",
            ImmediateOriginRoutingNumber = "101000019",
            Batches = [new AchBatch
            {
                OriginatingBankId = bankId, BatchNumber = 1, CompanyName = "ACME PAYROLL",
                CompanyId = "1234567890", SecCode = AchStandardEntryClass.Ppd,
                EffectiveEntryDate = new DateOnly(2026, 6, 29), Entries = [new AchEntry
                {
                    OriginatingBankId = bankId, CompanyName = "ACME PAYROLL", CompanyId = "1234567890",
                    SecCode = AchStandardEntryClass.Ppd, ReceiverName = "MARY JONES",
                    ReceivingRoutingNumber = "103000648", ReceivingAccountNumber = "654321",
                    TransactionCode = AchTransactionCode.CheckingCredit, Amount = 125.50m,
                    EntryDescription = "PAYROLL", EffectiveEntryDate = new DateOnly(2026, 6, 29),
                    TraceNumber = "101000010000001", Addenda05 = "PAY PERIOD 202606"
                }]
            }]
        };

        var payload = new NachaFileWriter().Write(file);
        var lines = payload.Split(Environment.NewLine);
        Assert.Equal(0, lines.Length % 10);
        Assert.All(lines, line => Assert.Equal(94, line.Length));
        var result = new NachaFileParser().Validate(payload);
        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        Assert.Equal(1, result.BatchCount);
        Assert.Equal(1, result.EntryCount);
        Assert.Equal(125.50m, result.TotalCredits);

        var tampered = payload.Replace("000000012550", "000000012551", StringComparison.Ordinal);
        var invalid = new NachaFileParser().Validate(tampered);
        Assert.False(invalid.IsValid);
        Assert.Contains(invalid.Errors, x => x.Contains("batch credit total", StringComparison.Ordinal));
    }

    [Fact]
    public void EftpsBuilderCreatesTxpPaymentInformation()
    {
        var value = EftpsTaxPaymentAddendaBuilder.Build(new EftpsTaxPayment
        { TaxpayerIdentificationNumber = "123456789", TaxTypeCode = "94105", TaxPeriodEndDate = "260630", Amount = 100.25m });
        Assert.Equal("TXP*123456789*94105*260630*T*10025\\", value);
    }

    [Theory]
    [InlineData(2026, 7, 3, true)] // Reserve Banks stay open before a Saturday holiday
    [InlineData(2026, 7, 4, false)]
    [InlineData(2026, 7, 6, true)]
    [InlineData(2026, 11, 26, false)] // Thanksgiving
    public void BusinessCalendarRecognizesFederalReserveBankingDays(int year, int month, int day, bool expected) =>
        Assert.Equal(expected, AchBusinessCalendar.IsBankingDay(new DateOnly(year, month, day)));

    [Fact]
    public void EntryRulesEnforceSecPurposeDirectionAndPrecision()
    {
        var entry = ValidEntry();
        entry.Purpose = AchPaymentPurpose.ConsumerDebit;
        entry.SecCode = AchStandardEntryClass.Web;
        entry.TransactionCode = AchTransactionCode.CheckingCredit;
        entry.Amount = 10.001m;

        var errors = AchRules.Validate(entry, new DateOnly(2026, 7, 2));

        Assert.Contains(errors, x => x.Field == nameof(AchEntry.TransactionCode));
        Assert.Contains(errors, x => x.Field == nameof(AchEntry.Purpose));
        Assert.Contains(errors, x => x.Field == nameof(AchEntry.Amount));
    }

    [Fact]
    public void ServiceClassReflectsEntryDirections()
    {
        var credit = ValidEntry();
        var debit = ValidEntry();
        debit.TransactionCode = AchTransactionCode.CheckingDebit;

        Assert.Equal("220", AchRules.ServiceClassCode([credit]));
        Assert.Equal("225", AchRules.ServiceClassCode([debit]));
        Assert.Equal("200", AchRules.ServiceClassCode([credit, debit]));
    }

    [Fact]
    public void ParserRejectsBrokenBlockingAndAddendaIndicator()
    {
        var file = TestFile();
        var payload = new NachaFileWriter().Write(file);
        var lines = payload.Split(Environment.NewLine).ToList();
        var detailIndex = lines.FindIndex(x => x[0] == '6');
        lines[detailIndex] = lines[detailIndex].Remove(78, 1).Insert(78, "0");
        lines.RemoveAt(lines.Count - 1);

        var result = new NachaFileParser().Validate(string.Join(Environment.NewLine, lines));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("multiple of 10", StringComparison.Ordinal));
        Assert.Contains(result.Errors, x => x.Contains("addenda indicator", StringComparison.Ordinal));
    }

    private static AchEntry ValidEntry() => new()
    {
        CompanyName = "ACME", CompanyId = "1234567890", SecCode = AchStandardEntryClass.Ppd,
        ReceiverName = "MARY JONES", ReceivingRoutingNumber = "103000648",
        ReceivingAccountNumber = "654321", TransactionCode = AchTransactionCode.CheckingCredit,
        Amount = 10m, EntryDescription = "PAYROLL", EffectiveEntryDate = new DateOnly(2026, 7, 6),
        Purpose = AchPaymentPurpose.Payroll, TraceNumber = "101000010000001"
    };

    private static AchFile TestFile()
    {
        var bankId = Guid.NewGuid();
        var entry = ValidEntry();
        entry.OriginatingBankId = bankId;
        entry.Addenda05 = "PAY PERIOD 202607";
        return new AchFile
        {
            OriginatingBankId = bankId,
            OriginatingBank = new Bank { Id = bankId, Name = "Bankers Bank", RoutingNumber = "101000019",
                FedParticipantId = "BANKERS", Bic = "BAKRUS44XXX", TownName = "Tulsa", CountryCode = "US" },
            ImmediateDestinationRoutingNumber = "091000080", ImmediateOriginRoutingNumber = "101000019",
            Batches = [new AchBatch { OriginatingBankId = bankId, BatchNumber = 1,
                CompanyName = "ACME", CompanyId = "1234567890", SecCode = AchStandardEntryClass.Ppd,
                EffectiveEntryDate = entry.EffectiveEntryDate, Entries = [entry] }]
        };
    }
}
