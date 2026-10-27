using Banking.Domain;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class WireTrafficTests
{
    [Fact]
    public void Generation_does_not_move_money_and_approval_uses_separate_identity()
    {
        var source = Account(Guid.NewGuid(), 5000m);
        var destination = Account(Guid.NewGuid(), 200m);
        var now = DateTimeOffset.UtcNow;
        var wire = WireTraffic.Create(source, destination, now);
        Assert.InRange(wire.Amount, 1m, 250m);
        Assert.Equal(decimal.Round(wire.Amount, 2), wire.Amount);
        Assert.Equal(WireStatus.PendingApproval, wire.Status);
        Assert.Null(wire.ApprovedBy);
        Assert.Equal(source.Customer.BankId, wire.BankId);
        Assert.Equal(destination.Customer.BankId, wire.ReceiverBankId);
        Assert.Equal(destination.AccountNumber, wire.BeneficiaryAccountNumber);
        WireTraffic.Approve(wire, now.AddSeconds(1));
        Assert.Equal(WireStatus.Created, wire.Status);
        Assert.NotEqual(wire.CreatedBy, wire.ApprovedBy);
        Assert.Equal(5000m, source.Balance);
        Assert.Equal(0m, source.HeldBalance);
        Assert.Equal(200m, destination.Balance);
        Assert.Equal(new[] { "Created", "Approved" }, wire.Events.Select(x => x.EventType));
        Assert.Throws<InvalidOperationException>(() => WireTraffic.Approve(wire, now));
    }

    [Fact]
    public void Generated_wire_amounts_vary_in_cents_within_the_funds_cap()
    {
        var source = Account(Guid.NewGuid(), 5000m);
        var destination = Account(Guid.NewGuid(), 100m);
        var random = new Random(19);
        var wires = Enumerable.Range(0, 30).Select(_ => WireTraffic.Create(source, destination, DateTimeOffset.UtcNow, random)).ToList();
        Assert.True(wires.Select(x => x.Amount).Distinct().Count() > 20);
        Assert.All(wires, wire => Assert.InRange(wire.Amount, 1m, 250m));
        Assert.Contains(wires, wire => wire.Amount != decimal.Truncate(wire.Amount));
        Assert.Equal(5000m, source.Balance);
    }

    [Fact]
    public void Simulated_approver_cannot_release_human_created_payment()
    {
        var wire = WireTraffic.Create(Account(Guid.NewGuid(), 100m), Account(Guid.NewGuid(), 0m), DateTimeOffset.UtcNow);
        wire.CreatedBy = "human-maker";
        Assert.Throws<InvalidOperationException>(() => WireTraffic.Approve(wire, DateTimeOffset.UtcNow));
        Assert.Equal(WireStatus.PendingApproval, wire.Status);
    }

    [Theory]
    [InlineData(AccountStatus.Frozen)]
    [InlineData(AccountStatus.Closed)]
    [InlineData(AccountStatus.Restricted)]
    public void Closed_or_restricted_accounts_cannot_generate_or_receive_traffic(AccountStatus status)
    {
        var source = Account(Guid.NewGuid(), 100m);
        var destination = Account(Guid.NewGuid(), 0m);
        source.Status = status;
        Assert.Throws<InvalidOperationException>(() => WireTraffic.Create(source, destination, DateTimeOffset.UtcNow));
        source.Status = AccountStatus.Open;
        destination.Status = status;
        Assert.Throws<InvalidOperationException>(() => WireTraffic.Create(source, destination, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Holds_reduce_generated_amount_and_same_bank_is_rejected()
    {
        var source = Account(Guid.NewGuid(), 100m);
        source.HeldBalance = 90m;
        var destination = Account(Guid.NewGuid(), 0m);
        Assert.Equal(1m, WireTraffic.Create(source, destination, DateTimeOffset.UtcNow).Amount);
        source.HeldBalance = 91m;
        Assert.Throws<InvalidOperationException>(() => WireTraffic.Create(source, destination, DateTimeOffset.UtcNow));
        source.HeldBalance = 0m;
        destination.Customer.BankId = source.Customer.BankId;
        Assert.Throws<InvalidOperationException>(() => WireTraffic.Create(source, destination, DateTimeOffset.UtcNow));
        Assert.NotEqual(WireTraffic.StateName(source.Customer.BankId), WireTraffic.StateName(Guid.NewGuid()));
    }

    private static Account Account(Guid bankId, decimal balance) => new()
    {
        AccountNumber = Guid.NewGuid().ToString("N"), Balance = balance,
        Customer = new Customer { BankId = bankId, Name = "Teaching customer" }
    };
}
