using System.Globalization;
using System.Xml.Linq;

namespace Banking.Domain;

public sealed record WireIsoMessageDefinition(
    string MessageType,
    string Purpose,
    MessageDirection Direction);

public sealed record CreatedWireIsoMessage(
    string MessageType,
    MessageDirection Direction,
    string XmlPayload);

public interface IWireIsoMessageService
{
    IReadOnlyList<WireIsoMessageDefinition> SupportedMessages { get; }
    CreatedWireIsoMessage Create(string messageType, WireTransfer wire, Bank sender, Bank receiver,
        string? debtorAccount, string? details = null);
}

/// <summary>
/// Creates wire-scoped ISO messages with a matching business header, original-payment
/// correlation, rail endpoints, and a payload suitable for the lab's structural validator.
/// </summary>
public sealed class WireIsoMessageService(IIsoMessageService iso) : IWireIsoMessageService
{
    private static readonly IReadOnlyList<WireIsoMessageDefinition> Definitions =
    [
        new("pacs.008", "Customer credit transfer", MessageDirection.Outbound),
        new("pacs.009", "Financial institution credit transfer", MessageDirection.Outbound),
        new("pacs.004", "Payment return", MessageDirection.Inbound),
        new("pain.013", "Drawdown request", MessageDirection.Outbound),
        new("pain.014", "Drawdown response", MessageDirection.Inbound),
        new("camt.110", "Investigation request", MessageDirection.Outbound),
        new("pacs.028", "Payment status request", MessageDirection.Outbound),
        new("camt.056", "Payment cancellation request", MessageDirection.Outbound),
        new("camt.029", "Investigation resolution", MessageDirection.Inbound),
        new("admi.007", "Receipt acknowledgement", MessageDirection.Inbound),
        new("pacs.002", "Payment status report", MessageDirection.Inbound),
        new("admi.002", "Message rejection", MessageDirection.Inbound),
        new("camt.052", "Account report", MessageDirection.Inbound),
        new("camt.060", "Account reporting request", MessageDirection.Outbound),
        new("admi.004", "System event notification", MessageDirection.Outbound),
        new("admi.011", "System event acknowledgement", MessageDirection.Inbound)
    ];

    private static readonly IReadOnlyDictionary<string, WireIsoMessageDefinition> ByType =
        Definitions.ToDictionary(x => x.MessageType, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<WireIsoMessageDefinition> SupportedMessages => Definitions;

    public CreatedWireIsoMessage Create(string messageType, WireTransfer wire, Bank sender,
        Bank receiver, string? debtorAccount, string? details = null)
    {
        ArgumentNullException.ThrowIfNull(wire);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(receiver);
        var baseType = BaseType(messageType);
        if (!ByType.TryGetValue(baseType, out var definition))
            throw new ArgumentException($"'{messageType}' is not supported by the wire workflow.",
                nameof(messageType));

        var xml = baseType switch
        {
            "pacs.008" => CreatePacs008(wire, sender, receiver, debtorAccount),
            "pacs.009" => wire.Rail == PaymentRail.FedNow
                ? iso.CreateFedNowPacs009(wire, sender, receiver)
                : iso.CreatePacs009(wire, sender, receiver),
            _ => CreateSupportingMessage(definition, wire, sender, receiver, details)
        };
        var validation = iso.Validate(xml);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Generated {baseType} failed validation: " +
                string.Join(" ", validation.Errors));
        return new(baseType, definition.Direction, xml);
    }

    private string CreatePacs008(WireTransfer wire, Bank sender, Bank receiver,
        string? debtorAccount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(debtorAccount);
        return wire.Rail switch
        {
            PaymentRail.FedNow => iso.CreateFedNowPacs008(wire, sender, receiver, debtorAccount,
                wire.BeneficiaryAccountNumber),
            PaymentRail.SwiftCbprPlus => iso.CreateCbprPlusPacs008(wire, sender, receiver,
                debtorAccount, wire.BeneficiaryAccountNumber),
            _ => iso.CreatePacs008(wire, sender, receiver, debtorAccount,
                wire.BeneficiaryAccountNumber)
        };
    }

    private string CreateSupportingMessage(WireIsoMessageDefinition definition, WireTransfer wire,
        Bank sender, Bank receiver, string? details)
    {
        var outbound = definition.Direction == MessageDirection.Outbound;
        var from = outbound ? sender : receiver;
        var to = outbound ? receiver : sender;
        var messageId = Guid.NewGuid().ToString("N");
        var note = string.IsNullOrWhiteSpace(details) ? definition.Purpose : details.Trim();
        return iso.CreateMessage(definition.MessageType, Endpoint(from, wire.Rail),
            Endpoint(to, wire.Rail), Payload(definition.MessageType, wire, sender, receiver,
                messageId, note), messageId, BusinessService(wire.Rail));
    }

    private static XElement Payload(string type, WireTransfer wire, Bank sender, Bank receiver,
        string messageId, string note)
    {
        XElement payload = type switch
        {
        "pacs.004" => new("PmtRtr",
            GroupHeader(messageId),
            new XElement("TxInf", new XElement("RtrId", messageId), OriginalPayment(wire),
                Amount("RtrdIntrBkSttlmAmt", wire.Amount),
                new XElement("RtrRsnInf", new XElement("Rsn", new XElement("Prtry", "CUST")),
                    new XElement("AddtlInf", note)))),
        "pain.013" => new("CdtrPmtActvtnReq",
            GroupHeader(messageId),
            new XElement("PmtInf", new XElement("PmtInfId", messageId),
                new XElement("PmtMtd", "TRF"),
                new XElement("ReqdExctnDt", DateTime.UtcNow.ToString("yyyy-MM-dd")),
                Party("Dbtr", wire.ReceiverName), Agent("DbtrAgt", receiver),
                Agent("CdtrAgt", sender), Party("Cdtr", wire.SenderName),
                new XElement("CdtTrfTx", new XElement("PmtId",
                        new XElement("InstrId", wire.CorrelationId.ToString("N")),
                        new XElement("EndToEndId", wire.CorrelationId.ToString("N"))),
                    Amount("Amt", wire.Amount), new XElement("RmtInf", new XElement("Ustrd", note))))),
        "pain.014" => new("CdtrPmtActvtnReqStsRpt", GroupHeader(messageId),
            new XElement("OrgnlGrpInfAndSts",
                new XElement("OrgnlMsgId", wire.CorrelationId.ToString("N")),
                new XElement("OrgnlMsgNmId", "pain.013.001.07"),
                new XElement("GrpSts", "ACCP"), new XElement("StsRsnInf",
                    new XElement("AddtlInf", note)))),
        "camt.110" => new("InvstgtnReq",
            new XElement("InvstgtnReq", new XElement("MsgId", messageId),
                new XElement("RqstrInvstgtnId", messageId),
                new XElement("InvstgtnTp", new XElement("Prtry", "PAYMENT")),
                new XElement("Undrlyg", new XElement("Pmt", OriginalPayment(wire))),
                Agent("Rqstr", sender), Agent("Rspndr", receiver)),
            new XElement("InvstgtnData", new XElement("InvstgtnRsn",
                new XElement("Prtry", "INQUIRY")), new XElement("InvstgtnRsnDesc", note))),
        "pacs.028" => new("FIToFIPmtStsReq", GroupHeader(messageId),
            new XElement("TxInf", OriginalPayment(wire), new XElement("StsReqRsn", note))),
        "camt.056" => new("FIToFIPmtCxlReq", Assignment(messageId, sender, receiver),
            new XElement("Undrlyg", OriginalPayment(wire), new XElement("CxlRsnInf",
                new XElement("Rsn", new XElement("Prtry", "CUST")),
                new XElement("AddtlInf", note)))),
        "camt.029" => new("RsltnOfInvstgtn", Assignment(messageId, receiver, sender),
            new XElement("Sts", new XElement("Conf", "ACCP")),
            new XElement("CxlDtls", OriginalPayment(wire),
                new XElement("RsltnRltdInf", note))),
        "admi.007" => new("RctAck", new XElement("MsgId", messageId),
            new XElement("RctDtTm", DateTime.UtcNow.ToString("O")),
            new XElement("AckdMsgRef", wire.CorrelationId.ToString("N")),
            new XElement("AckSts", "ACCP")),
        "pacs.002" => new("FIToFIPmtStsRpt", GroupHeader(messageId),
            new XElement("OrgnlGrpInfAndSts",
                new XElement("OrgnlMsgId", wire.CorrelationId.ToString("N")),
                new XElement("OrgnlMsgNmId", OriginalDefinition(wire))),
            new XElement("TxInfAndSts",
                new XElement("OrgnlInstrId", wire.CorrelationId.ToString("N")),
                new XElement("OrgnlUETR", wire.CorrelationId.ToString().ToLowerInvariant()),
                new XElement("TxSts", "ACSC"),
                new XElement("StsRsnInf", new XElement("AddtlInf", note)))),
        "admi.002" => new("AdmiRjct", new XElement("RltdRef",
                new XElement("Ref", wire.CorrelationId.ToString("N"))),
            new XElement("Rsn", new XElement("RjctgPtyRsn", "NARR"),
                new XElement("RjctnDtTm", DateTime.UtcNow.ToString("O")),
                new XElement("AddtlData", note))),
        "camt.052" => new("BkToCstmrAcctRpt", GroupHeader(messageId),
            new XElement("Rpt", new XElement("Id", messageId),
                new XElement("CreDtTm", DateTime.UtcNow.ToString("O")),
                new XElement("Acct", new XElement("Id", new XElement("Othr",
                    new XElement("Id", wire.BeneficiaryAccountNumber)))),
                new XElement("Bal", new XElement("Tp", new XElement("CdOrPrtry",
                        new XElement("Cd", "CLBD"))), Amount("Amt", wire.Amount),
                    new XElement("CdtDbtInd", "CRDT"),
                    new XElement("Dt", new XElement("Dt", DateTime.UtcNow.ToString("yyyy-MM-dd")))))),
        "camt.060" => new("AcctRptgReq", GroupHeader(messageId),
            new XElement("RptgReq", new XElement("Id", messageId),
                new XElement("ReqdMsgNmId", "camt.052.001.08"),
                new XElement("Acct", new XElement("Id", new XElement("Othr",
                    new XElement("Id", wire.BeneficiaryAccountNumber)))),
                new XElement("AddtlRptgInf", note))),
        "admi.004" => new("SysEvtNtfctn", new XElement("EvtInf",
            new XElement("EvtCd", "PING"), new XElement("EvtParam", note),
            new XElement("EvtTm", DateTime.UtcNow.ToString("O")))),
        "admi.011" => new("SysEvtAck", new XElement("MsgId", messageId),
            new XElement("OrgnlMsgId", wire.CorrelationId.ToString("N")),
            new XElement("AckSts", "ACCP"), new XElement("AddtlInf", note)),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
        if (type is "pain.013" or "pain.014" or "admi.007" or "admi.002" or "camt.052"
            or "camt.060" or "admi.004" or "admi.011")
            payload.Add(new XElement("SplmtryData", new XElement("Envlp",
                new XElement("OrgnlUETR", wire.CorrelationId.ToString().ToLowerInvariant()))));
        return payload;
    }

    private static XElement GroupHeader(string messageId) => new("GrpHdr",
        new XElement("MsgId", messageId), new XElement("CreDtTm", DateTime.UtcNow.ToString("O")),
        new XElement("NbOfTxs", 1));

    private static XElement Assignment(string id, Bank from, Bank to) => new("Assgnmt",
        new XElement("Id", id), new XElement("Assgnr", from.Name),
        new XElement("Assgne", to.Name), new XElement("CreDtTm", DateTime.UtcNow.ToString("O")));

    private static XElement OriginalPayment(WireTransfer wire) => new("OrgnlTxRef",
        new XElement("OrgnlInstrId", wire.CorrelationId.ToString("N")),
        new XElement("OrgnlUETR", wire.CorrelationId.ToString().ToLowerInvariant()),
        Amount("OrgnlIntrBkSttlmAmt", wire.Amount));

    private static XElement Amount(string name, decimal amount) => new(name,
        new XAttribute("Ccy", "USD"), amount.ToString("0.00", CultureInfo.InvariantCulture));

    private static XElement Party(string name, string partyName) =>
        new(name, new XElement("Nm", partyName));

    private static XElement Agent(string name, Bank bank) => new(name,
        new XElement("FinInstnId", new XElement("ClrSysMmbId",
            new XElement("MmbId", bank.RoutingNumber))));

    private static string OriginalDefinition(WireTransfer wire) =>
        wire.TransferType == WireTransferType.FinancialInstitutionCreditTransfer
            ? "pacs.009.001.08" : "pacs.008.001.08";

    private static string Endpoint(Bank bank, PaymentRail rail) =>
        rail == PaymentRail.SwiftCbprPlus ? bank.Bic : bank.RoutingNumber;

    private static string BusinessService(PaymentRail rail) => rail switch
    {
        PaymentRail.FedNow => FedNowProfile.BusinessService,
        PaymentRail.SwiftCbprPlus => CbprPlusProfile.BusinessService,
        _ => "fedwire-lab"
    };

    private static string BaseType(string identifier)
    {
        if (!IsoMessageCatalog.TryResolve(identifier, out var definition, out _))
            return identifier.Trim();
        return definition.MessageType;
    }
}
