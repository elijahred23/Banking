using Banking.Domain;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class WireVisualizationTests
{
    [Fact]
    public void CustomerForecastPreservesExistingHoldsAndAvailabilityAdjustmentsWithoutMutation()
    {
        var source = new Account { AccountNumber = "SOURCE", Balance = 1000, HeldBalance = 50,
            UncollectedBalance = 100, OverdraftLimit = 25 };
        var sender = Bank("Sender", 5000);
        var result = WireVisualizationPolicy.Preview(Wire(), sender, Bank("Receiver"), source);
        var stages = result.Branches.Single(x => x.Id == "accepted").Steps;
        Assert.All(stages.Take(4), stage => Assert.False(stage.Posted));
        Assert.Equal(875, Balance(stages[0], "Customer available"));
        Assert.Equal(750, Balance(stages[1], "Customer available"));
        Assert.Equal(175, Balance(stages[1], "Customer holds"));
        Assert.Equal(1000, Balance(stages[1], "Customer ledger"));
        Assert.Equal(5000, Balance(stages[3], "Bank settlement cash"));
        Assert.Equal(875, Balance(stages[^1], "Customer ledger"));
        Assert.Equal(50, Balance(stages[^1], "Customer holds"));
        Assert.Equal(750, Balance(stages[^1], "Customer available"));
        Assert.Equal(4875, Balance(stages[^1], "Bank settlement cash"));
        Assert.Equal(1000, source.Balance);
        Assert.Equal(50, source.HeldBalance);
        Assert.Equal(5000, sender.MasterAccountBalance);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("malformed")]
    public void PreSettlementFailureReleasesOnlyProjectedHoldAndNeverPosts(string branch)
    {
        var source = new Account { AccountNumber = "SOURCE", Balance = 1000, HeldBalance = 50 };
        var result = WireVisualizationPolicy.Preview(Wire(), Bank("Sender"), Bank("Receiver"), source);
        var stages = result.Branches.Single(x => x.Id == branch).Steps;
        Assert.All(stages, stage => Assert.False(stage.Posted));
        Assert.Equal(1000, Balance(stages[^1], "Customer ledger"));
        Assert.Equal(50, Balance(stages[^1], "Customer holds"));
        Assert.Equal(950, Balance(stages[^1], "Customer available"));
        if (branch == "malformed") Assert.DoesNotContain(stages, step => step.Title == "Delivered");
    }

    [Fact]
    public void InstitutionForecastHasNoCustomerHoldAndSwiftDoesNotForecastMasterCash()
    {
        var wire = Wire(); wire.TransferType = WireTransferType.FinancialInstitutionCreditTransfer;
        var result = WireVisualizationPolicy.Preview(wire, Bank("Sender", 1000), Bank("Receiver"), null);
        Assert.All(result.Branches.SelectMany(x => x.Steps), step => Assert.Single(step.Balances));
        Assert.Equal(1000, Balance(result.Branches[0].Steps[1], "Bank settlement cash"));
        Assert.Equal(875, Balance(result.Branches[0].Steps[^1], "Bank settlement cash"));
        wire.TransferType = WireTransferType.CustomerCreditTransfer; wire.Rail = PaymentRail.SwiftCbprPlus;
        result = WireVisualizationPolicy.Preview(wire, Bank("Sender"), Bank("Receiver"), new Account { AccountNumber = "SOURCE", Balance = 1000 });
        Assert.True(result.Correspondent);
        Assert.All(result.Branches.SelectMany(x => x.Steps), step => Assert.DoesNotContain(step.Balances, x => x.Name == "Bank settlement cash"));
    }

    [Fact]
    public void IneligibleSourceStartsOnStoppedBranchWithoutCreatingAHold()
    {
        var account = new Account { AccountNumber = "SOURCE", Balance = 1000, Status = AccountStatus.Closed };
        var result = WireVisualizationPolicy.Preview(Wire(), Bank("Sender"), Bank("Receiver"), account);
        Assert.Equal("funds", result.SelectedBranch);
        var final = result.Branches.Single(x => x.Id == "funds").Steps[^1];
        Assert.False(final.Posted);
        Assert.Equal(0, Balance(final, "Customer holds"));
    }

    private static Bank Bank(string name, decimal cash = 0) => new() { Name = name, MasterAccountBalance = cash, RoutingNumber = "101000019", FedParticipantId = "101000019", Bic = "DEMOUS33", TownName = "Demo", CountryCode = "US" };
    private static decimal Balance(WireVisualStep step, string name) => step.Balances.Single(x => x.Name == name).After;
    private static WireTransfer Wire() => new() { Amount = 125, SenderName = "Sender", ReceiverName = "Receiver", BeneficiaryAccountNumber = "12345678" };
}
