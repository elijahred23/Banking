using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Banking.Domain;

/// <summary>Versioned learning profile; structural validation is not rail certification.</summary>
public static class CamtStatementBuilder
{
    public static string Build(AccountStatement statement)
    {
        var type = StatementPolicy.MessageType(statement.Kind);
        XNamespace ns = $"urn:iso:std:iso:20022:tech:xsd:{type}.001.08";
        XElement E(string name, params object?[] children) => new(ns + name, children);
        string Amount(decimal value) => Math.Abs(value).ToString("0.0000", CultureInfo.InvariantCulture);
        XElement Balance(string code, decimal value, DateTimeOffset at) => E("Bal",
            E("Tp", E("CdOrPrtry", E("Cd", code))), E("Amt", new XAttribute("Ccy", "USD"), Amount(value)),
            E("CdtDbtInd", value < 0 ? "DBIT" : "CRDT"), E("Dt", E("DtTm", at.ToString("O"))));
        var root = statement.Kind switch { StatementKind.Intraday => "BkToCstmrAcctRpt", StatementKind.Daily => "BkToCstmrStmt", _ => "BkToCstmrDbtCdtNtfctn" };
        var item = statement.Kind switch { StatementKind.Intraday => "Rpt", StatementKind.Daily => "Stmt", _ => "Ntfctn" };
        var body = E(item, E("Id", statement.StatementNumber), E("ElctrncSeqNb", statement.SequenceNumber),
            E("CreDtTm", statement.CreatedAt.ToString("O")),
            E("FrToDt", E("FrDtTm", statement.PeriodStart.ToString("O")), E("ToDtTm", statement.PeriodEnd.ToString("O"))),
            E("Acct", E("Id", E("Othr", E("Id", statement.AccountNumber))), E("Ccy", "USD"),
                E("Ownr", E("Nm", statement.RecipientName)), E("Svcr", E("FinInstnId", E("Nm", statement.IssuerName)))));
        if (statement.Kind != StatementKind.Notification)
        {
            body.Add(Balance("OPBD", statement.OpeningBalance, statement.PeriodStart));
            body.Add(Balance(statement.Kind == StatementKind.Intraday ? "ITBD" : "CLBD", statement.ClosingBalance, statement.PeriodEnd));
        }
        foreach (var entry in statement.Entries.OrderBy(x => x.BookingAt).ThenBy(x => x.PostingId))
            body.Add(E("Ntry", E("NtryRef", entry.PostingId.ToString("N")), E("Amt", new XAttribute("Ccy", "USD"), Amount(entry.Amount)),
                E("CdtDbtInd", entry.IsCredit ? "CRDT" : "DBIT"), E("Sts", E("Cd", "BOOK")),
                E("BookgDt", E("DtTm", entry.BookingAt.ToString("O"))), E("ValDt", E("Dt", entry.ValueDate.ToString("yyyy-MM-dd"))),
                E("AcctSvcrRef", entry.CorrelationId.ToString("N")), E("BkTxCd", E("Prtry", E("Cd", "LAB-BOOKED"))),
                E("NtryDtls", E("TxDtls", E("Refs", E("EndToEndId", entry.CorrelationId.ToString("N"))),
                    E("RmtInf", E("Ustrd", entry.Reference))))));
        body.Add(E("Addtl" + item + "Inf", $"Learning-only booked account snapshot. Version {statement.Version}." +
            (statement.PreviousStatementId is Guid previous ? $" Corrects {previous}." : "")));
        var xml = new XDocument(new XDeclaration("1.0", "utf-8", null), E("Document",
            E(root, E("GrpHdr", E("MsgId", statement.Id.ToString("N")), E("CreDtTm", statement.CreatedAt.ToString("O"))), body))).ToString();
        Validate(xml, statement.Kind);
        return xml;
    }
    public static void Validate(string xml, StatementKind kind)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var doc = XDocument.Load(reader);
        XNamespace ns = $"urn:iso:std:iso:20022:tech:xsd:{StatementPolicy.MessageType(kind)}.001.08";
        var root = kind switch { StatementKind.Intraday => "BkToCstmrAcctRpt", StatementKind.Daily => "BkToCstmrStmt", _ => "BkToCstmrDbtCdtNtfctn" };
        if (doc.Root?.Name != ns + "Document" || doc.Root.Element(ns + root) is null
            || !doc.Descendants(ns + "Acct").Any() || !doc.Descendants(ns + "MsgId").Any())
            throw new InvalidOperationException("Wrong camt message version or missing reporting structure.");
        foreach (var entry in doc.Descendants(ns + "Ntry"))
            if (entry.Element(ns + "Amt")?.Attribute("Ccy")?.Value != "USD"
                || !decimal.TryParse(entry.Element(ns + "Amt")?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0
                || entry.Element(ns + "CdtDbtInd")?.Value is not ("CRDT" or "DBIT") || entry.Element(ns + "BookgDt") is null || entry.Element(ns + "ValDt") is null)
                throw new InvalidOperationException("Invalid booked statement entry.");
    }
}
