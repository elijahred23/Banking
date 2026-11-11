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

public sealed class MultiBankIsolationTests
{
    [Fact]
    public async Task Wire_views_and_posting_reject_another_banks_identifiers_and_hide_customer_directory()
    {
        var factory = new PooledDbContextFactory<BankingDbContext>(new DbContextOptionsBuilder<BankingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var own = Bank("Own bank", "990000001", "OWN");
        var foreign = Bank("Other bank", "990000002", "OTHER");
        var wire = new WireTransfer { BankId = foreign.Id, ReceiverBankId = foreign.Id,
            Direction = WireDirection.Incoming, Status = WireStatus.Received,
            SenderName = "Private sender", ReceiverName = "Private receiver", BeneficiaryAccountNumber = "PRIVATE-ACCOUNT" };
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Banks.AddRange(own, foreign);
            db.Customers.Add(new Customer { BankId = foreign.Id, Name = "PRIVATE-CUSTOMER",
                Accounts = [new Account { AccountNumber = "PRIVATE-ACCOUNT", Balance = 987654m }] });
            db.WireTransfers.Add(wire);
            await db.SaveChangesAsync();
        }
        var iso = new IsoMessageService();
        var controller = new WiresController(factory, iso, new CbprPlusMessageService(iso),
            new WireIsoMessageService(iso), new NonValueMessageWorkflowService(iso), new IncomingWirePostingService(factory))
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext {
                User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim("bank_id", own.Id.ToString()), new Claim(ClaimTypes.Name, "operations@own"),
                    new Claim(ClaimTypes.Role, "Operations") ], "test")) } }
        };
        Assert.IsType<NotFoundResult>(await controller.Details(wire.Id, default));
        Assert.IsType<NotFoundResult>(await controller.PostIncoming(wire.Id, default));
        var create = Assert.IsType<CreateWireViewModel>(Assert.IsType<ViewResult>(
            await controller.Create(null, null, null, default)).Model);
        Assert.Empty(create.Accounts);
        Assert.DoesNotContain("PRIVATE-CUSTOMER", JsonSerializer.Serialize(create));
        Assert.DoesNotContain("PRIVATE-ACCOUNT", JsonSerializer.Serialize(create));
        var index = Assert.IsType<WireIndexViewModel>(Assert.IsType<ViewResult>(await controller.Index(null, default)).Model);
        Assert.Empty(index.Wires);
        var reconciliation = await new BankNetworkReconciliationService(factory).BuildAsync(own.Id, default);
        Assert.Empty(reconciliation);
    }

    private static Bank Bank(string name, string routing, string participant) => new() {
        Name = name, RoutingNumber = routing, FedParticipantId = participant, Bic = $"{participant}US44XXX",
        TownName = "Synthetic city", CountryCode = "US" };
}
