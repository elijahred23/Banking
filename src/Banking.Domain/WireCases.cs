namespace Banking.Domain;

public static class WireCasePolicy
{
    public static bool CanRequestReturn(WireTransfer wire) =>
        wire.Direction == WireDirection.Outgoing && wire.Status == WireStatus.Settled;

    public static bool CanInvestigate(WireTransfer wire) =>
        wire.Direction == WireDirection.Outgoing
        && wire.Status is WireStatus.SentToFed or WireStatus.PendingAtFed or WireStatus.Settled
            or WireStatus.Rejected or WireStatus.Returned;

    public static string RequestMessageType(WireCaseType type, PaymentRail rail) => type switch
    {
        WireCaseType.ReturnRequest => "camt.056",
        WireCaseType.Investigation when rail == PaymentRail.FedNow => "pacs.028",
        WireCaseType.Investigation => "camt.027",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static string ResponseMessageType(WireCaseType type, PaymentRail rail) =>
        type == WireCaseType.Investigation && rail == PaymentRail.FedNow ? "pacs.002" : "camt.029";
}

public static class WireReturnPosting
{
    public static bool CanComplete(WireTransfer outgoing, WireTransfer? incoming,
        Account? origin, Account? beneficiary, Bank receiver) =>
        outgoing.TransferType == WireTransferType.FinancialInstitutionCreditTransfer
            ? incoming is not null && receiver.MasterAccountBalance >= outgoing.Amount
            : incoming is not null && origin is not null && beneficiary is not null
        && beneficiary.Balance >= outgoing.Amount
        && (outgoing.Rail == PaymentRail.SwiftCbprPlus
            || receiver.MasterAccountBalance >= outgoing.Amount);

    public static void Complete(WireTransfer outgoing, WireTransfer incoming,
        Account? origin, Account? beneficiary, Bank sender, Bank receiver)
    {
        if (!CanComplete(outgoing, incoming, origin, beneficiary, receiver))
            throw new InvalidOperationException("The wire return cannot be completed with the available balances.");

        if (outgoing.TransferType == WireTransferType.FinancialInstitutionCreditTransfer)
        {
            sender.MasterAccountBalance += outgoing.Amount;
            receiver.MasterAccountBalance -= outgoing.Amount;
        }
        else
        {
            origin!.Balance += outgoing.Amount;
            beneficiary!.Balance -= outgoing.Amount;
            if (outgoing.Rail != PaymentRail.SwiftCbprPlus)
            {
                sender.MasterAccountBalance += outgoing.Amount;
                receiver.MasterAccountBalance -= outgoing.Amount;
            }
        }
        outgoing.Status = WireStatus.Returned;
        incoming.Status = WireStatus.Returned;
    }
}
