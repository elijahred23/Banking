using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class WireCaseTests
{
    [Fact]
    public void Only_settled_outgoing_wire_is_eligible_for_return_request()
    {
        var wire = Wire(WireStatus.Settled, WireDirection.Outgoing);
        Assert.True(WireCasePolicy.CanRequestReturn(wire));

        wire.Status = WireStatus.PendingAtFed;
        Assert.False(WireCasePolicy.CanRequestReturn(wire));
        wire.Status = WireStatus.Settled;
        wire.Direction = WireDirection.Incoming;
        Assert.False(WireCasePolicy.CanRequestReturn(wire));
    }

    [Theory]
    [InlineData(WireStatus.SentToFed, true)]
    [InlineData(WireStatus.PendingAtFed, true)]
    [InlineData(WireStatus.Settled, true)]
    [InlineData(WireStatus.Rejected, true)]
    [InlineData(WireStatus.Created, false)]
    public void Investigation_eligibility_tracks_network_processing(WireStatus status, bool expected)
    {
        Assert.Equal(expected, WireCasePolicy.CanInvestigate(Wire(status, WireDirection.Outgoing)));
    }

    [Theory]
    [InlineData(WireCaseType.ReturnRequest, PaymentRail.Fedwire, "camt.056", "camt.029")]
    [InlineData(WireCaseType.ReturnRequest, PaymentRail.FedNow, "camt.056", "camt.029")]
    [InlineData(WireCaseType.Investigation, PaymentRail.FedNow, "pacs.028", "pacs.002")]
    [InlineData(WireCaseType.Investigation, PaymentRail.Fedwire, "camt.110", "camt.029")]
    [InlineData(WireCaseType.Investigation, PaymentRail.SwiftCbprPlus, "camt.027", "camt.029")]
    public void Case_types_map_to_rail_specific_iso_messages(WireCaseType type, PaymentRail rail,
        string request, string response)
    {
        Assert.Equal(request, WireCasePolicy.RequestMessageType(type, rail));
        Assert.Equal(response, WireCasePolicy.ResponseMessageType(type, rail));
    }

    [Fact]
    public void SqlServer_model_contains_wire_case_schema_and_relationship()
    {
        var options = new DbContextOptionsBuilder<BankingDbContext>()
            .UseSqlServer("Server=localhost;Database=unused;User Id=unused;Password=unused;TrustServerCertificate=True")
            .Options;
        using var db = new BankingDbContext(options);
        var script = db.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE [WireCases]", script);
        Assert.Contains("IX_WireCases_WireTransferId_CreatedDate", script);
        Assert.Contains("ON DELETE CASCADE", script);
    }

    [Fact]
    public void Completing_domestic_return_reverses_customer_and_master_account_balances()
    {
        var outgoing = Wire(WireStatus.Settled, WireDirection.Outgoing);
        var incoming = Wire(WireStatus.Completed, WireDirection.Incoming);
        var origin = Account(900m);
        var beneficiary = Account(300m);
        var sender = Bank(10_000m);
        var receiver = Bank(8_000m);

        WireReturnPosting.Complete(outgoing, incoming, origin, beneficiary, sender, receiver);

        Assert.Equal(1_000m, origin.Balance);
        Assert.Equal(200m, beneficiary.Balance);
        Assert.Equal(10_100m, sender.MasterAccountBalance);
        Assert.Equal(7_900m, receiver.MasterAccountBalance);
        Assert.Equal(WireStatus.Returned, outgoing.Status);
        Assert.Equal(WireStatus.Returned, incoming.Status);
    }

    [Fact]
    public void Return_is_unavailable_when_beneficiary_no_longer_has_the_funds()
    {
        var outgoing = Wire(WireStatus.Settled, WireDirection.Outgoing);
        Assert.False(WireReturnPosting.CanComplete(outgoing,
            Wire(WireStatus.Completed, WireDirection.Incoming), Account(900m), Account(99m), Bank(8_000m)));
    }

    [Fact]
    public void Completing_institution_return_reverses_master_account_liquidity_without_customer_accounts()
    {
        var outgoing = Wire(WireStatus.Settled, WireDirection.Outgoing);
        outgoing.TransferType = WireTransferType.FinancialInstitutionCreditTransfer;
        var incoming = Wire(WireStatus.Completed, WireDirection.Incoming);
        incoming.TransferType = WireTransferType.FinancialInstitutionCreditTransfer;
        var sender = Bank(9_900m);
        var receiver = Bank(8_100m);

        Assert.True(WireReturnPosting.CanComplete(outgoing, incoming, null, null, receiver));
        WireReturnPosting.Complete(outgoing, incoming, null, null, sender, receiver);

        Assert.Equal(10_000m, sender.MasterAccountBalance);
        Assert.Equal(8_000m, receiver.MasterAccountBalance);
        Assert.Equal(WireStatus.Returned, outgoing.Status);
        Assert.Equal(WireStatus.Returned, incoming.Status);
    }

    private static WireTransfer Wire(WireStatus status, WireDirection direction) => new()
    {
        BankId = Guid.NewGuid(), SenderBankId = Guid.NewGuid(), ReceiverBankId = Guid.NewGuid(),
        Status = status, Direction = direction, Amount = 100m, SenderName = "Sender",
        ReceiverName = "Receiver", BeneficiaryAccountNumber = "123456",
        Rail = PaymentRail.Fedwire, Scenario = ProcessingScenario.Standard
    };

    private static Account Account(decimal balance) => new()
    {
        CustomerId = Guid.NewGuid(), AccountNumber = Guid.NewGuid().ToString("N"), Balance = balance
    };

    private static Bank Bank(decimal balance) => new()
    {
        Name = "Bank", RoutingNumber = Random.Shared.Next(100_000_000, 999_999_999).ToString(),
        FedParticipantId = "BANK", Bic = $"BANK{Random.Shared.Next(1000000, 9999999)}",
        TownName = "Town", CountryCode = "US", MasterAccountBalance = balance
    };
}
