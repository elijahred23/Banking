using Banking.Domain;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class IsoMessageServiceTests
{
    private readonly IsoMessageService _service = new();

    [Fact]
    public void Pacs008_contains_header_uetr_and_valid_payment_fields()
    {
        var wire = Wire();
        var xml = _service.CreatePacs008(wire, Bank("101000019"), Bank("103000648"),
            "123456", "654321");

        var result = _service.Validate(xml);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("pacs.008", result.MessageType);
        Assert.Contains("head.001.001.02", xml);
        Assert.Contains(wire.CorrelationId.ToString().ToLowerInvariant(), xml);
    }

    [Theory]
    [InlineData("PDNG")]
    [InlineData("ACSC")]
    [InlineData("RJCT")]
    public void Pacs002_accepts_supported_fedwire_statuses(string status)
    {
        var xml = _service.CreatePacs002(Guid.NewGuid(), status, "lab reason", "20260628ABC123");

        var result = _service.Validate(xml);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal("pacs.002", result.MessageType);
    }

    [Fact]
    public void Validation_reports_malformed_xml_without_throwing()
    {
        var result = _service.Validate("<Document>");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("not well formed"));
    }

    private static WireTransfer Wire() => new()
    {
        BankId = Guid.NewGuid(), SenderBankId = Guid.NewGuid(), ReceiverBankId = Guid.NewGuid(),
        Direction = WireDirection.Outgoing, Amount = 125.50m, Status = WireStatus.Created,
        SenderName = "John Smith", ReceiverName = "Mary Jones", BeneficiaryAccountNumber = "654321"
    };

    private static Bank Bank(string routing) => new()
    {
        Name = "Test Bank", RoutingNumber = routing, FedParticipantId = "TEST",
        MasterAccountBalance = 1_000_000m
    };
}
