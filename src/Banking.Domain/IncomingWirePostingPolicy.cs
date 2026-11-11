namespace Banking.Domain;

public static class IncomingWirePostingPolicy
{
    public static void Post(WireTransfer wire, Account account, Guid bankId)
    {
        if (wire.BankId != bankId || wire.ReceiverBankId != bankId || account.Customer.BankId != bankId
            || wire.Direction != WireDirection.Incoming || wire.Rail != PaymentRail.Fedwire
            || wire.TransferType != WireTransferType.CustomerCreditTransfer
            || wire.BeneficiaryAccountNumber != account.AccountNumber || wire.Amount <= 0
            || account.Status != AccountStatus.Open)
            throw new InvalidOperationException("Beneficiary posting requires a matching account owned by the receiving bank.");
        if (wire.Status == WireStatus.Completed) return;
        if (wire.Status != WireStatus.Received)
            throw new InvalidOperationException("Only a received wire can be posted.");
        wire.ToAccountId = account.Id;
        WireWorkflow.Transition(wire, WireStatus.Completed);
        account.Balance += wire.Amount;
    }
}
