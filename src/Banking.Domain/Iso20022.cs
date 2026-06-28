using System.Xml.Linq;
using System.Xml.Schema;

namespace Banking.Domain;

public interface IIsoMessageService
{
    string CreatePacs008(WireTransfer wire, Bank senderBank, Bank receiverBank,
        string debtorAccount, string creditorAccount);
    string CreatePacs002(Guid correlationId, string statusCode, string reason, string imad);
    bool IsWellFormed(string xml, out string? error);
}

public sealed class IsoMessageService : IIsoMessageService
{
    private static readonly XNamespace Pacs008 = "urn:iso:std:iso:20022:tech:xsd:pacs.008.001.08";
    private static readonly XNamespace Pacs002 = "urn:iso:std:iso:20022:tech:xsd:pacs.002.001.10";

    public string CreatePacs008(WireTransfer wire, Bank senderBank, Bank receiverBank,
        string debtorAccount, string creditorAccount)
    {
        var amount = wire.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        return new XDocument(new XElement(Pacs008 + "Document",
            new XElement(Pacs008 + "FIToFICstmrCdtTrf",
                new XElement(Pacs008 + "GrpHdr",
                    new XElement(Pacs008 + "MsgId", wire.CorrelationId),
                    new XElement(Pacs008 + "CreDtTm", wire.CreatedDate.UtcDateTime.ToString("O")),
                    new XElement(Pacs008 + "NbOfTxs", 1)),
                new XElement(Pacs008 + "CdtTrfTxInf",
                    new XElement(Pacs008 + "PmtId", new XElement(Pacs008 + "EndToEndId", wire.CorrelationId)),
                    new XElement(Pacs008 + "IntrBkSttlmAmt", new XAttribute("Ccy", "USD"), amount),
                    new XElement(Pacs008 + "Dbtr", new XElement(Pacs008 + "Nm", wire.SenderName)),
                    new XElement(Pacs008 + "DbtrAcct", new XElement(Pacs008 + "Id", debtorAccount)),
                    new XElement(Pacs008 + "DbtrAgt", new XElement(Pacs008 + "FinInstnId", senderBank.RoutingNumber)),
                    new XElement(Pacs008 + "CdtrAgt", new XElement(Pacs008 + "FinInstnId", receiverBank.RoutingNumber)),
                    new XElement(Pacs008 + "Cdtr", new XElement(Pacs008 + "Nm", wire.ReceiverName)),
                    new XElement(Pacs008 + "CdtrAcct", new XElement(Pacs008 + "Id", creditorAccount)))))).ToString();
    }

    public string CreatePacs002(Guid correlationId, string statusCode, string reason, string imad) =>
        new XDocument(new XElement(Pacs002 + "Document",
            new XElement(Pacs002 + "FIToFIPmtStsRpt",
                new XElement(Pacs002 + "GrpHdr", new XElement(Pacs002 + "MsgId", Guid.NewGuid())),
                new XElement(Pacs002 + "OrgnlGrpInfAndSts",
                    new XElement(Pacs002 + "OrgnlMsgId", correlationId),
                    new XElement(Pacs002 + "GrpSts", statusCode),
                    new XElement(Pacs002 + "StsRsnInf", new XElement(Pacs002 + "AddtlInf", reason)),
                    new XElement(Pacs002 + "OrgnlPmtInfAndSts", new XElement(Pacs002 + "OrgnlPmtInfId", imad)))))).ToString();

    public bool IsWellFormed(string xml, out string? error)
    {
        try { _ = XDocument.Parse(xml); error = null; return true; }
        catch (Exception ex) when (ex is System.Xml.XmlException or XmlSchemaException)
        { error = ex.Message; return false; }
    }
}
