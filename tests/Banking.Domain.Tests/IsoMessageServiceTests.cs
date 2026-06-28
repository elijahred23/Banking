using Banking.Domain;
using System.Xml.Linq;
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

    [Fact]
    public void Catalog_contains_every_supported_wire_message_type()
    {
        string[] expected =
        [
            "head.001", "admi.002", "admi.004", "admi.006", "admi.007", "admi.011",
            "pacs.002", "pacs.003", "pacs.004", "pacs.007", "pacs.008", "pacs.009",
            "pacs.010", "pacs.028", "pain.001", "pain.002", "pain.007", "pain.008",
            "pain.013", "pain.014", "camt.026", "camt.027", "camt.028", "camt.029",
            "camt.052", "camt.053", "camt.054", "camt.055", "camt.056", "camt.057",
            "camt.058", "camt.060", "camt.087"
        ];

        Assert.Equal(expected.Order(), IsoMessageCatalog.All.Select(x => x.MessageType).Order());
    }

    [Fact]
    public void Every_business_message_type_can_be_created_and_recognized()
    {
        foreach (var definition in IsoMessageCatalog.All.Where(x => !x.IsBusinessApplicationHeader))
        {
            var xml = _service.CreateMessage(definition.MessageType, "SENDER", "RECEIVER",
                GenericPayload(definition.MessageType));

            var result = _service.Validate(xml);

            Assert.True(result.IsValid,
                $"{definition.MessageType}: {string.Join(Environment.NewLine, result.Errors)}");
            Assert.Equal(definition.MessageType, result.MessageType);
            Assert.Contains(definition.DefaultMessageDefinitionId, xml);
        }
    }

    [Fact]
    public void Versioned_supported_message_identifier_is_preserved()
    {
        var xml = _service.CreateMessage("pacs.008.001.14", "SENDER", "RECEIVER",
            new XElement("FIToFICstmrCdtTrf"));

        Assert.Contains("pacs.008.001.14", xml);
        Assert.Equal("pacs.008", _service.Validate(xml).MessageType);
    }

    [Fact]
    public void Head001_can_be_created_for_any_supported_business_message()
    {
        var xml = _service.CreateBusinessApplicationHeader("SENDER", "RECEIVER", "camt.056");

        Assert.True(_service.IsWellFormed(xml, out var error), error);
        Assert.Contains("head.001.001.02", xml);
        Assert.Contains("camt.056.001.08", xml);
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

    private static XElement GenericPayload(string messageType) => messageType switch
    {
        "pacs.002" => new XElement("FIToFIPmtStsRpt",
            new XElement("TxInfAndSts",
                new XElement("OrgnlUETR", Guid.NewGuid()),
                new XElement("TxSts", "ACSC"))),
        "pacs.008" => new XElement("FIToFICstmrCdtTrf",
            new XElement("CdtTrfTxInf",
                new XElement("PmtId", new XElement("UETR", Guid.NewGuid())),
                new XElement("IntrBkSttlmAmt", new XAttribute("Ccy", "USD"), "1.00"),
                new XElement("Dbtr", new XElement("Nm", "Debtor")),
                new XElement("DbtrAcct", new XElement("Id", new XElement("Othr",
                    new XElement("Id", "123")))),
                new XElement("DbtrAgt", new XElement("FinInstnId", new XElement("ClrSysMmbId",
                    new XElement("MmbId", "101000019")))),
                new XElement("CdtrAgt", new XElement("FinInstnId", new XElement("ClrSysMmbId",
                    new XElement("MmbId", "103000648")))),
                new XElement("Cdtr", new XElement("Nm", "Creditor")),
                new XElement("CdtrAcct", new XElement("Id", new XElement("Othr",
                    new XElement("Id", "456")))))),
        _ => new XElement("TestBusinessMessage", new XElement("MessageId", "test-id"))
    };
}
