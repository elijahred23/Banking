using Banking.Domain;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class IncomingWirePostingTests
{
    [Fact]
    public void Posting_is_independent_and_replays_do_not_credit_twice()
    {
        var (wire, account) = Payment();
        Assert.Equal(100m, account.Balance);
        IncomingWirePostingPolicy.Post(wire, account, wire.BankId);
        IncomingWirePostingPolicy.Post(wire, account, wire.BankId);
        Assert.Equal(125m, account.Balance);
        Assert.Equal(WireStatus.Completed, wire.Status);
        Assert.Equal(account.Id, wire.ToAccountId);
    }

    [Theory]
    [InlineData("bank")]
    [InlineData("account bank")]
    [InlineData("account number")]
    [InlineData("direction")]
    [InlineData("amount")]
    [InlineData("status")]
    [InlineData("frozen")]
    public void Invalid_posting_cannot_mutate_balance(string failure)
    {
        var (wire, account) = Payment();
        var bankId = wire.BankId;
        switch (failure)
        {
            case "bank": bankId = Guid.NewGuid(); break;
            case "account bank": account.Customer.BankId = Guid.NewGuid(); break;
            case "account number": account.AccountNumber = "other"; break;
            case "direction": wire.Direction = WireDirection.Outgoing; break;
            case "amount": wire.Amount = -25; break;
            case "status": wire.Status = WireStatus.Returned; break;
            case "frozen": account.Status = AccountStatus.Frozen; break;
        }
        Assert.Throws<InvalidOperationException>(() => IncomingWirePostingPolicy.Post(wire, account, bankId));
        Assert.Equal(100m, account.Balance);
    }

    private static (WireTransfer, Account) Payment()
    {
        var bankId = Guid.NewGuid();
        return (new WireTransfer { BankId = bankId, ReceiverBankId = bankId,
            Amount = 25, Status = WireStatus.Received, Direction = WireDirection.Incoming,
            Rail = PaymentRail.Fedwire, TransferType = WireTransferType.CustomerCreditTransfer,
            SenderName = "Synthetic sender", ReceiverName = "Synthetic receiver", BeneficiaryAccountNumber = "1234" },
            new Account { AccountNumber = "1234", Balance = 100,
                Customer = new Customer { Name = "Synthetic receiver", BankId = bankId } });
    }
}
