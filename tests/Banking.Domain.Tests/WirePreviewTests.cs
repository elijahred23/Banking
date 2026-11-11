using System.Security.Claims;
using System.Text.Json;
using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Controllers;
using Banking.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class WirePreviewTests
{
    [Theory]
    [InlineData(PaymentRail.Fedwire, WireTransferType.CustomerCreditTransfer)]
    [InlineData(PaymentRail.FedNow, WireTransferType.CustomerCreditTransfer)]
    [InlineData(PaymentRail.SwiftCbprPlus, WireTransferType.CustomerCreditTransfer)]
    [InlineData(PaymentRail.Fedwire, WireTransferType.FinancialInstitutionCreditTransfer)]
    [InlineData(PaymentRail.FedNow, WireTransferType.FinancialInstitutionCreditTransfer)]
    public async Task NewWirePreviewUsesFormValuesWithoutSaving(PaymentRail rail, WireTransferType type)
    {
        var (factory, bank, other, account) = await Seed();
        var controller = Controller(factory, bank.Id);
        var result = Assert.IsType<JsonResult>(await controller.Create(new CreateWireViewModel
        {
            FromAccountId = account.Id, ReceiverBankId = other.Id, ReceiverName = "Preview beneficiary",
            BeneficiaryAccountNumber = "12345678", Amount = 123.45m,
            Rail = rail, TransferType = type, CustomerReference = "PREVIEW-REFERENCE"
        }, default, preview: true));
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains(type == WireTransferType.CustomerCreditTransfer ? "pacs.008" : "pacs.009", json);
        Assert.Contains("123.45", json);
        Assert.Contains("PREVIEW-REFERENCE", json);
        await AssertUnchanged(factory);
    }

    [Theory]
    [InlineData(WireCaseType.ReturnRequest, PaymentRail.Fedwire, "camt.056")]
    [InlineData(WireCaseType.Investigation, PaymentRail.Fedwire, "camt.110")]
    [InlineData(WireCaseType.Investigation, PaymentRail.FedNow, "pacs.028")]
    [InlineData(WireCaseType.Investigation, PaymentRail.SwiftCbprPlus, "camt.027")]
    public async Task CasePreviewIsScopedAndDoesNotPostReturns(WireCaseType type, PaymentRail rail, string messageType)
    {
        var (factory, bank, other, _) = await Seed();
        var wire = new WireTransfer
        {
            BankId = bank.Id, SenderBankId = bank.Id, ReceiverBankId = other.Id,
            Direction = WireDirection.Outgoing, Status = WireStatus.Settled, Rail = rail,
            Amount = 123.45m, SenderName = "Sender", ReceiverName = "Receiver",
            BeneficiaryAccountNumber = "12345678"
        };
        await using (var db = factory.CreateDbContext())
        {
            db.WireTransfers.Add(wire);
            await db.SaveChangesAsync();
        }
        var result = Assert.IsType<JsonResult>(await Controller(factory, bank.Id)
            .OpenCase(wire.Id, type, "Please investigate this payment.", default, preview: true));
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains(messageType, json);
        if (type == WireCaseType.ReturnRequest) Assert.Contains("pacs.004", json);
        Assert.IsType<NotFoundResult>(await Controller(factory, other.Id)
            .OpenCase(wire.Id, type, "Please investigate this payment.", default, preview: true));
        await AssertUnchanged(factory, 1);
        await using var verify = factory.CreateDbContext();
        Assert.Equal(WireStatus.Settled, (await verify.WireTransfers.SingleAsync()).Status);
    }

    [Fact]
    public async Task DrawdownPreviewIncludesAccountsAndDoesNotSaveExchange()
    {
        var (factory, bank, other, account) = await Seed();
        var result = Assert.IsType<JsonResult>(await Controller(factory, bank.Id).CreateRequestForPayment(
            new RequestForPaymentViewModel
            {
                CreditorAccountId = account.Id, DebtorBankId = other.Id,
                DebtorName = "Debtor", DebtorAccount = "87654321",
                Amount = 75, Remittance = "Preview invoice", Rail = PaymentRail.Fedwire
            }, default, preview: true));
        var json = JsonSerializer.Serialize(result.Value);
        Assert.Contains("pain.013", json);
        Assert.Contains("87654321", json);
        Assert.Contains("Preview invoice", json);
        await AssertUnchanged(factory);
        Assert.IsType<BadRequestObjectResult>(await Controller(factory, other.Id).CreateRequestForPayment(
            new RequestForPaymentViewModel { CreditorAccountId = account.Id, DebtorBankId = bank.Id },
            default, preview: true));
    }

    [Fact]
    public async Task MalformedScenarioCanBePreviewedAndInvalidFormDoesNotSave()
    {
        var (factory, bank, other, account) = await Seed();
        var controller = Controller(factory, bank.Id);
        var model = new CreateWireViewModel
        {
            FromAccountId = account.Id, ReceiverBankId = other.Id, ReceiverName = "Receiver",
            BeneficiaryAccountNumber = "12345678", Amount = 25,
            Scenario = ProcessingScenario.MalformedIso, CustomerReference = "MALFORMED-PREVIEW"
        };
        var result = Assert.IsType<JsonResult>(await controller.Create(model, default, preview: true));
        var data = JsonSerializer.SerializeToElement(result.Value);
        var xml = data.GetProperty("messages")[0].GetProperty("xml").GetString()!;
        Assert.False(new IsoMessageService().Validate(xml).IsValid);
        controller.ModelState.AddModelError("Amount", "Enter a positive amount.");
        Assert.IsType<BadRequestObjectResult>(await controller.Create(model, default, preview: true));
        await AssertUnchanged(factory);
    }

    private static async Task AssertUnchanged(PooledDbContextFactory<BankingDbContext> factory, int wires = 0)
    {
        await using var db = factory.CreateDbContext();
        Assert.Equal(wires, await db.WireTransfers.CountAsync());
        Assert.Empty(db.WireCases);
        Assert.Empty(db.IsoMessages);
        Assert.Empty(db.MessageExchanges);
        Assert.Empty(db.WireEvents);
        Assert.Empty(db.OutboxMessages);
        Assert.Empty(db.LedgerEntries);
        Assert.Equal(1000m, (await db.Accounts.SingleAsync()).Balance);
        Assert.Equal(0m, (await db.Accounts.SingleAsync()).HeldBalance);
    }

    private static WiresController Controller(PooledDbContextFactory<BankingDbContext> factory, Guid bankId)
    {
        var iso = new IsoMessageService();
        return new(factory, iso, new CbprPlusMessageService(iso),
            new WireIsoMessageService(iso), new NonValueMessageWorkflowService(iso), new IncomingWirePostingService(factory))
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim("bank_id", bankId.ToString()), new Claim(ClaimTypes.Name, "preview-maker")
                ], "test"))
            } }
        };
    }

    private static async Task<(PooledDbContextFactory<BankingDbContext>, Bank, Bank, Account)> Seed()
    {
        var factory = new PooledDbContextFactory<BankingDbContext>(new DbContextOptionsBuilder<BankingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Bank NewBank(string routing) => new()
        {
            Name = "Demo", RoutingNumber = routing, FedParticipantId = routing, Bic = "DEMOUS33",
            TownName = "Tulsa", CountryCode = "US", SwiftEnabled = true,
            FedNowEnabled = true, FedNowReceiveEnabled = true, FedNowOnline = true
        };
        var bank = NewBank("101000019"); var other = NewBank("021000021");
        var account = new Account
        {
            Customer = new Customer { BankId = bank.Id, Name = "Creditor" },
            AccountNumber = "11223344", Balance = 1000
        };
        await using var db = factory.CreateDbContext();
        db.Banks.AddRange(bank, other); db.Accounts.Add(account);
        await db.SaveChangesAsync();
        return (factory, bank, other, account);
    }
}
