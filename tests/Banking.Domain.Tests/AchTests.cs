using Banking.Domain;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class AchTests
{
    [Theory]
    [InlineData("101000019", true)]
    [InlineData("103000648", true)]
    [InlineData("091000080", true)]
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
}
