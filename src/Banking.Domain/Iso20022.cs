using System.Globalization;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Banking.Domain;

public sealed record IsoValidationResult(bool IsValid, string MessageType,
    IReadOnlyList<string> Errors);

public interface IIsoMessageService
{
    string CreatePacs008(WireTransfer wire, Bank senderBank, Bank receiverBank,
        string debtorAccount, string creditorAccount);
    string CreatePacs002(Guid correlationId, string statusCode, string reason, string imad);
    string CreateMessage(string messageType, string from, string to, XElement businessMessage,
        string? businessMessageId = null, string businessService = "iso20022");
    string CreateBusinessApplicationHeader(string from, string to, string messageDefinitionId,
        string? businessMessageId = null, string businessService = "iso20022");
    IsoValidationResult Validate(string xml);
    bool IsWellFormed(string xml, out string? error);
}

public sealed class IsoMessageService : IIsoMessageService
{
    private const string IsoNamespacePrefix = "urn:iso:std:iso:20022:tech:xsd:";
    public const string Pacs008Namespace = "urn:iso:std:iso:20022:tech:xsd:pacs.008.001.08";
    public const string Pacs002Namespace = "urn:iso:std:iso:20022:tech:xsd:pacs.002.001.10";
    public const string HeaderNamespace = "urn:iso:std:iso:20022:tech:xsd:head.001.001.02";
    public const string EnvelopeNamespace = "urn:fedwire-lab:message-envelope:v1";

    private static readonly XNamespace Pacs008 = Pacs008Namespace;
    private static readonly XNamespace Pacs002 = Pacs002Namespace;
    private static readonly XNamespace Head = HeaderNamespace;
    private static readonly XNamespace Envelope = EnvelopeNamespace;

    public string CreatePacs008(WireTransfer wire, Bank senderBank, Bank receiverBank,
        string debtorAccount, string creditorAccount)
    {
        var messageId = wire.CorrelationId.ToString("N");
        var document = new XElement(Pacs008 + "Document",
            new XElement(Pacs008 + "FIToFICstmrCdtTrf",
                new XElement(Pacs008 + "GrpHdr",
                    new XElement(Pacs008 + "MsgId", messageId),
                    new XElement(Pacs008 + "CreDtTm", wire.CreatedDate.UtcDateTime.ToString("O")),
                    new XElement(Pacs008 + "NbOfTxs", 1),
                    new XElement(Pacs008 + "SttlmInf", new XElement(Pacs008 + "SttlmMtd", "CLRG"))),
                new XElement(Pacs008 + "CdtTrfTxInf",
                    new XElement(Pacs008 + "PmtId",
                        new XElement(Pacs008 + "InstrId", messageId),
                        new XElement(Pacs008 + "EndToEndId", messageId),
                        new XElement(Pacs008 + "UETR", wire.CorrelationId.ToString().ToLowerInvariant())),
                    new XElement(Pacs008 + "IntrBkSttlmAmt", new XAttribute("Ccy", "USD"),
                        wire.Amount.ToString("0.00", CultureInfo.InvariantCulture)),
                    new XElement(Pacs008 + "IntrBkSttlmDt", DateTime.UtcNow.ToString("yyyy-MM-dd")),
                    new XElement(Pacs008 + "ChrgBr", "SLEV"),
                    new XElement(Pacs008 + "Dbtr", new XElement(Pacs008 + "Nm", wire.SenderName)),
                    Account(Pacs008, "DbtrAcct", debtorAccount),
                    Agent(Pacs008, "DbtrAgt", senderBank.RoutingNumber),
                    Agent(Pacs008, "CdtrAgt", receiverBank.RoutingNumber),
                    new XElement(Pacs008 + "Cdtr", new XElement(Pacs008 + "Nm", wire.ReceiverName)),
                    Account(Pacs008, "CdtrAcct", creditorAccount))));
        return EnvelopeMessage(Header(senderBank.RoutingNumber, receiverBank.RoutingNumber,
            messageId, "pacs.008.001.08"), document);
    }

    public string CreatePacs002(Guid correlationId, string statusCode, string reason, string imad)
    {
        var messageId = Guid.NewGuid().ToString("N");
        var document = new XElement(Pacs002 + "Document",
            new XElement(Pacs002 + "FIToFIPmtStsRpt",
                new XElement(Pacs002 + "GrpHdr",
                    new XElement(Pacs002 + "MsgId", messageId),
                    new XElement(Pacs002 + "CreDtTm", DateTime.UtcNow.ToString("O"))),
                new XElement(Pacs002 + "OrgnlGrpInfAndSts",
                    new XElement(Pacs002 + "OrgnlMsgId", correlationId.ToString("N")),
                    new XElement(Pacs002 + "OrgnlMsgNmId", "pacs.008.001.08")),
                new XElement(Pacs002 + "TxInfAndSts",
                    new XElement(Pacs002 + "OrgnlInstrId", correlationId.ToString("N")),
                    new XElement(Pacs002 + "OrgnlUETR", correlationId.ToString().ToLowerInvariant()),
                    new XElement(Pacs002 + "TxSts", statusCode),
                    new XElement(Pacs002 + "StsRsnInf",
                        new XElement(Pacs002 + "Rsn", new XElement(Pacs002 + "Prtry", reason)),
                        new XElement(Pacs002 + "AddtlInf", $"IMAD {imad}")))));
        return EnvelopeMessage(Header("FEDWIRE", "PARTICIPANT", messageId, "pacs.002.001.10"), document);
    }

    public string CreateMessage(string messageType, string from, string to, XElement businessMessage,
        string? businessMessageId = null, string businessService = "iso20022")
    {
        if (!IsoMessageCatalog.TryResolve(messageType, out var definition, out var definitionId)
            || definition.IsBusinessApplicationHeader)
            throw new ArgumentException($"Unsupported ISO 20022 business message type '{messageType}'.",
                nameof(messageType));
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentNullException.ThrowIfNull(businessMessage);
        ArgumentException.ThrowIfNullOrWhiteSpace(businessService);

        var messageId = string.IsNullOrWhiteSpace(businessMessageId)
            ? Guid.NewGuid().ToString("N")
            : businessMessageId;
        var ns = (XNamespace)(IsoNamespacePrefix + definitionId);
        var payload = Qualify(businessMessage, ns);
        var document = new XElement(ns + "Document", payload);
        return EnvelopeMessage(Header(from, to, messageId, definitionId, businessService), document);
    }

    public string CreateBusinessApplicationHeader(string from, string to, string messageDefinitionId,
        string? businessMessageId = null, string businessService = "iso20022")
    {
        if (!IsoMessageCatalog.TryResolve(messageDefinitionId, out var definition, out var definitionId)
            || definition.IsBusinessApplicationHeader)
            throw new ArgumentException($"Unsupported ISO 20022 business message type '{messageDefinitionId}'.",
                nameof(messageDefinitionId));
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(businessService);

        return new XDocument(Header(from, to,
            string.IsNullOrWhiteSpace(businessMessageId) ? Guid.NewGuid().ToString("N") : businessMessageId,
            definitionId, businessService)).ToString();
    }

    public IsoValidationResult Validate(string xml)
    {
        XDocument parsed;
        try { parsed = XDocument.Parse(xml); }
        catch (Exception ex) when (ex is System.Xml.XmlException or XmlSchemaException)
        { return new(false, "unknown", [$"XML is not well formed: {ex.Message}"]); }

        var errors = new List<string>();
        var header = parsed.Descendants().SingleOrDefault(x => x.Name.LocalName == "AppHdr"
            && x.Name.NamespaceName.StartsWith(IsoNamespacePrefix + "head.001.", StringComparison.Ordinal));
        var document = parsed.Descendants().FirstOrDefault(x => x.Name.LocalName == "Document");
        if (header is null) errors.Add("Business Application Header head.001.001.02 is required.");
        if (document is null) return new(false, "unknown", [.. errors, "ISO Document element is required."]);

        var definitionId = document.Name.NamespaceName.StartsWith(IsoNamespacePrefix, StringComparison.Ordinal)
            ? document.Name.NamespaceName[IsoNamespacePrefix.Length..]
            : string.Empty;
        var supported = IsoMessageCatalog.TryResolve(definitionId, out var definition, out _)
            && !definition.IsBusinessApplicationHeader;
        var messageType = supported ? definition.MessageType : "unknown";
        if (messageType == "unknown") errors.Add($"Unsupported ISO namespace '{document.Name.NamespaceName}'.");
        if (supported && document.Elements().Count() != 1)
            errors.Add("ISO Document must contain exactly one business message root element.");
        var headerType = header?.Descendants().SingleOrDefault(x => x.Name.LocalName == "MsgDefIdr")?.Value;
        if (messageType != "unknown" && !string.Equals(headerType, definitionId,
                StringComparison.Ordinal))
            errors.Add("Business header message definition does not match the document namespace.");

        if (messageType == "pacs.008") ValidatePacs008(document, errors);
        if (messageType == "pacs.002") ValidatePacs002(document, errors);
        return new(errors.Count == 0, messageType, errors);
    }

    public bool IsWellFormed(string xml, out string? error)
    {
        try { _ = XDocument.Parse(xml); error = null; return true; }
        catch (Exception ex) when (ex is System.Xml.XmlException or XmlSchemaException)
        { error = ex.Message; return false; }
    }

    private static void ValidatePacs008(XElement document, List<string> errors)
    {
        var tx = document.Descendants(Pacs008 + "CdtTrfTxInf").SingleOrDefault();
        if (tx is null) { errors.Add("Exactly one credit transfer transaction is required."); return; }
        var amount = tx.Element(Pacs008 + "IntrBkSttlmAmt");
        if (amount?.Attribute("Ccy")?.Value != "USD" || !decimal.TryParse(amount?.Value,
                NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value <= 0)
            errors.Add("Settlement amount must be a positive USD amount.");
        var uetr = tx.Descendants(Pacs008 + "UETR").SingleOrDefault()?.Value;
        if (!Guid.TryParse(uetr, out _)) errors.Add("A UUID-formatted UETR is required.");
        var routes = tx.Descendants(Pacs008 + "MmbId").Select(x => x.Value).ToList();
        if (routes.Count != 2 || routes.Any(x => x.Length != 9 || !x.All(char.IsDigit)))
            errors.Add("Debtor and creditor agents require nine-digit routing numbers.");
        var accounts = new[] { "DbtrAcct", "CdtrAcct" }
            .Select(name => tx.Element(Pacs008 + name)?.Descendants(Pacs008 + "Othr")
                .SingleOrDefault()?.Element(Pacs008 + "Id")?.Value)
            .ToList();
        if (accounts.Count != 2 || accounts.Any(string.IsNullOrWhiteSpace))
            errors.Add("Debtor and creditor account identifiers are required.");
        if (tx.Element(Pacs008 + "Dbtr")?.Element(Pacs008 + "Nm") is null
            || tx.Element(Pacs008 + "Cdtr")?.Element(Pacs008 + "Nm") is null)
            errors.Add("Debtor and creditor names are required.");
    }

    private static void ValidatePacs002(XElement document, List<string> errors)
    {
        var status = document.Descendants(Pacs002 + "TxSts").SingleOrDefault()?.Value;
        if (status is not ("PDNG" or "ACSC" or "RJCT"))
            errors.Add("Payment status must be PDNG, ACSC, or RJCT.");
        if (!Guid.TryParse(document.Descendants(Pacs002 + "OrgnlUETR").SingleOrDefault()?.Value, out _))
            errors.Add("Original UETR is required.");
    }

    private static XElement Header(string from, string to, string messageId, string definition,
        string businessService = "fedwire-lab") =>
        new(Head + "AppHdr",
            Party("Fr", from), Party("To", to),
            new XElement(Head + "BizMsgIdr", messageId),
            new XElement(Head + "MsgDefIdr", definition),
            new XElement(Head + "BizSvc", businessService),
            new XElement(Head + "CreDt", DateTime.UtcNow.ToString("O")));

    private static XElement Party(string name, string memberId) => new(Head + name,
        new XElement(Head + "FIId", new XElement(Head + "FinInstnId",
            new XElement(Head + "ClrSysMmbId", new XElement(Head + "MmbId", memberId)))));

    private static XElement Agent(XNamespace ns, string name, string routing) => new(ns + name,
        new XElement(ns + "FinInstnId", new XElement(ns + "ClrSysMmbId",
            new XElement(ns + "MmbId", routing))));

    private static XElement Account(XNamespace ns, string name, string id) => new(ns + name,
            new XElement(ns + "Id", new XElement(ns + "Othr", new XElement(ns + "Id", id))));

    private static XElement Qualify(XElement element, XNamespace ns)
    {
        if (!string.IsNullOrEmpty(element.Name.NamespaceName)
            && element.Name.NamespaceName != ns.NamespaceName)
            throw new ArgumentException("Business message elements must be unqualified or use the selected message namespace.",
                nameof(element));

        return new XElement(ns + element.Name.LocalName,
            element.Attributes().Where(x => !x.IsNamespaceDeclaration),
            element.Nodes().Select(node => node is XElement child ? Qualify(child, ns) : node));
    }

    private static string EnvelopeMessage(XElement header, XElement document) =>
        new XDocument(new XElement(Envelope + "Message", header, document)).ToString();
}
