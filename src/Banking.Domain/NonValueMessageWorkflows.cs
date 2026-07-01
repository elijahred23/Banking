using System.Globalization;
using System.Xml.Linq;

namespace Banking.Domain;

public interface INonValueMessageWorkflowService
{
    IReadOnlyList<CreatedWireIsoMessage> CreateRequestForPayment(Guid exchangeId, Bank creditorBank,
        Bank debtorBank, string creditorName, string creditorAccount, string debtorName,
        string debtorAccount, decimal amount, string remittance, PaymentRail rail);
    CreatedWireIsoMessage CreateRequestForPaymentResponse(Guid exchangeId, Bank creditorBank,
        Bank debtorBank, PaymentRail rail, bool accepted, string? reason = null);
    IReadOnlyList<CreatedWireIsoMessage> CreateAccountReport(Guid exchangeId, Bank bank,
        string reportType, DateOnly businessDate, decimal balance, PaymentRail rail);
    IReadOnlyList<CreatedWireIsoMessage> CreateSystemEvent(Guid exchangeId, Bank sender,
        Bank recipient, string eventCode, string details);
}

public sealed class NonValueMessageWorkflowService(IIsoMessageService iso)
    : INonValueMessageWorkflowService
{
    public IReadOnlyList<CreatedWireIsoMessage> CreateRequestForPayment(Guid exchangeId,
        Bank creditorBank, Bank debtorBank, string creditorName, string creditorAccount,
        string debtorName, string debtorAccount, decimal amount, string remittance,
        PaymentRail rail)
    {
        var id = exchangeId.ToString("N");
        var request = new XElement("CdtrPmtActvtnReq",
            Header(id),
            new XElement("PmtInf", new XElement("PmtInfId", id),
                new XElement("PmtMtd", "TRF"),
                new XElement("ReqdExctnDt", DateTime.UtcNow.ToString("yyyy-MM-dd")),
                Party("Dbtr", debtorName), Account("DbtrAcct", debtorAccount),
                Agent("DbtrAgt", debtorBank), Agent("CdtrAgt", creditorBank),
                Party("Cdtr", creditorName), Account("CdtrAcct", creditorAccount),
                new XElement("CdtTrfTx", new XElement("PmtId",
                        new XElement("InstrId", id), new XElement("EndToEndId", id)),
                    new XElement("Amt", new XElement("InstdAmt", new XAttribute("Ccy", "USD"),
                        amount.ToString("0.00", CultureInfo.InvariantCulture))),
                    new XElement("RmtInf", new XElement("Ustrd", remittance)))));
        var requestXml = iso.CreateMessage("pain.013", Endpoint(creditorBank, rail),
            Endpoint(debtorBank, rail), request, id, BusinessService(rail));

        var responseXml = iso.CreateMessage("admi.007", Endpoint(debtorBank, rail),
            Endpoint(creditorBank, rail), ReceiptAcknowledgement(id),
            Guid.NewGuid().ToString("N"), BusinessService(rail));
        return Validate([
            new("pain.013", MessageDirection.Outbound, requestXml),
            new("admi.007", MessageDirection.Inbound, responseXml)
        ]);
    }

    public CreatedWireIsoMessage CreateRequestForPaymentResponse(Guid exchangeId,
        Bank creditorBank, Bank debtorBank, PaymentRail rail, bool accepted, string? reason = null)
    {
        var originalId = exchangeId.ToString("N");
        var status = accepted ? "ACCP" : "RJCT";
        var response = new XElement("CdtrPmtActvtnReqStsRpt",
            Header(Guid.NewGuid().ToString("N")),
            new XElement("OrgnlGrpInfAndSts",
                new XElement("OrgnlMsgId", originalId),
                new XElement("OrgnlMsgNmId", "pain.013.001.07"),
                new XElement("GrpSts", status),
                accepted ? null : new XElement("StsRsnInf",
                    new XElement("Rsn", new XElement("Prtry", "DECLINED")),
                    new XElement("AddtlInf", string.IsNullOrWhiteSpace(reason)
                        ? "The debtor declined the request for payment."
                        : reason.Trim()))));
        var xml = iso.CreateMessage("pain.014", Endpoint(debtorBank, rail),
            Endpoint(creditorBank, rail), response, Guid.NewGuid().ToString("N"),
            BusinessService(rail));
        return Validate([new("pain.014", MessageDirection.Inbound, xml)])[0];
    }

    public IReadOnlyList<CreatedWireIsoMessage> CreateAccountReport(Guid exchangeId, Bank bank,
        string reportType, DateOnly businessDate, decimal balance, PaymentRail rail)
    {
        var id = exchangeId.ToString("N");
        var request = new XElement("AcctRptgReq", Header(id),
            new XElement("RptgReq", new XElement("Id", id),
                new XElement("ReqdMsgNmId", "camt.052.001.08"),
                new XElement("Acct", new XElement("Id", new XElement("Othr",
                    new XElement("Id", bank.FedParticipantId)))),
                new XElement("RptgPrd", new XElement("FrToDt",
                    new XElement("FrDt", businessDate.ToString("yyyy-MM-dd")),
                    new XElement("ToDt", businessDate.ToString("yyyy-MM-dd")))),
                new XElement("AddtlRptgInf", reportType)));
        var requestXml = iso.CreateMessage("camt.060", bank.RoutingNumber, "FEDERALRESERVE",
            request, id, BusinessService(rail));
        var report = new XElement("BkToCstmrAcctRpt", Header(Guid.NewGuid().ToString("N")),
            new XElement("Rpt", new XElement("Id", $"RPT-{id}"),
                new XElement("CreDtTm", DateTime.UtcNow.ToString("O")),
                new XElement("FrToDt", new XElement("FrDtTm", businessDate.ToString("yyyy-MM-dd")),
                    new XElement("ToDtTm", businessDate.ToString("yyyy-MM-dd"))),
                new XElement("Acct", new XElement("Id", new XElement("Othr",
                    new XElement("Id", bank.FedParticipantId))),
                    new XElement("Nm", $"{bank.Name} master account")),
                new XElement("Bal", new XElement("Tp", new XElement("CdOrPrtry",
                        new XElement("Cd", "CLBD"))),
                    new XElement("Amt", new XAttribute("Ccy", "USD"),
                        balance.ToString("0.00", CultureInfo.InvariantCulture)),
                    new XElement("CdtDbtInd", "CRDT"),
                    new XElement("Dt", new XElement("Dt", businessDate.ToString("yyyy-MM-dd")))),
                new XElement("AddtlRptInf", reportType)));
        var reportXml = iso.CreateMessage("camt.052", "FEDERALRESERVE", bank.RoutingNumber,
            report, Guid.NewGuid().ToString("N"), BusinessService(rail));
        return Validate([
            new("camt.060", MessageDirection.Outbound, requestXml),
            new("camt.052", MessageDirection.Inbound, reportXml)
        ]);
    }

    public IReadOnlyList<CreatedWireIsoMessage> CreateSystemEvent(Guid exchangeId, Bank sender,
        Bank recipient, string eventCode, string details)
    {
        var id = exchangeId.ToString("N");
        var request = new XElement("SysEvtNtfctn", new XElement("EvtInf",
            new XElement("EvtId", id), new XElement("EvtCd", eventCode),
            new XElement("EvtDesc", details), new XElement("EvtTm", DateTime.UtcNow.ToString("O")),
            Agent("InstgAgt", sender), Agent("InstdAgt", recipient)));
        var requestXml = iso.CreateMessage("admi.004", sender.RoutingNumber,
            recipient.RoutingNumber, request, id, FedNowProfile.BusinessService);
        var response = new XElement("SysEvtAck", new XElement("MsgId", Guid.NewGuid().ToString("N")),
            new XElement("OrgnlMsgId", id), new XElement("AckSts", "ACCP"),
            new XElement("RspnTm", DateTime.UtcNow.ToString("O")));
        var responseXml = iso.CreateMessage("admi.011", recipient.RoutingNumber,
            sender.RoutingNumber, response, Guid.NewGuid().ToString("N"), FedNowProfile.BusinessService);
        return Validate([
            new("admi.004", MessageDirection.Outbound, requestXml),
            new("admi.011", MessageDirection.Inbound, responseXml)
        ]);
    }

    private IReadOnlyList<CreatedWireIsoMessage> Validate(
        IReadOnlyList<CreatedWireIsoMessage> messages)
    {
        foreach (var message in messages)
        {
            var result = iso.Validate(message.XmlPayload);
            if (!result.IsValid)
                throw new InvalidOperationException($"Generated {message.MessageType} failed validation: " +
                    string.Join(" ", result.Errors));
        }
        return messages;
    }

    private static XElement Header(string id) => new("GrpHdr", new XElement("MsgId", id),
        new XElement("CreDtTm", DateTime.UtcNow.ToString("O")), new XElement("NbOfTxs", 1));
    private static XElement Party(string element, string name) =>
        new(element, new XElement("Nm", name));
    private static XElement Account(string element, string account) => new(element,
        new XElement("Id", new XElement("Othr", new XElement("Id", account))));
    private static XElement Agent(string element, Bank bank) => new(element,
        new XElement("FinInstnId", new XElement("ClrSysMmbId",
            new XElement("MmbId", bank.RoutingNumber))));
    private static XElement ReceiptAcknowledgement(string originalId) => new("RctAck",
        new XElement("MsgId", Guid.NewGuid().ToString("N")),
        new XElement("RctDtTm", DateTime.UtcNow.ToString("O")),
        new XElement("AckdMsgRef", originalId), new XElement("AckSts", "ACCP"));
    private static string Endpoint(Bank bank, PaymentRail rail) =>
        rail == PaymentRail.SwiftCbprPlus ? bank.Bic : bank.RoutingNumber;
    private static string BusinessService(PaymentRail rail) => rail switch
    {
        PaymentRail.FedNow => FedNowProfile.BusinessService,
        PaymentRail.SwiftCbprPlus => CbprPlusProfile.BusinessService,
        _ => "fedwire-lab"
    };
}
