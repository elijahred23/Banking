using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize(Roles = BankSecurity.NetworkOperator)]
public sealed class NetworkController(IDbContextFactory<BankingDbContext> factory) : Controller
{
    public async Task<IActionResult> Index(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var banks = await db.Banks.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new NetworkParticipantViewModel(x.Name, x.RoutingNumber, x.FedParticipantId, x.MasterAccountBalance))
            .ToListAsync(token);
        var settlements = await (from settlement in db.FedSettlements.AsNoTracking()
            join wire in db.WireTransfers.Where(x => x.Direction == WireDirection.Outgoing && x.Rail == PaymentRail.Fedwire)
                on settlement.CorrelationId equals wire.CorrelationId
            join sender in db.Banks on wire.SenderBankId equals sender.Id
            join receiver in db.Banks on wire.ReceiverBankId equals receiver.Id
            orderby wire.CreatedDate descending
            select new NetworkSettlementViewModel(settlement.CorrelationId, sender.Name, receiver.Name,
                wire.Amount, settlement.StatusCode, settlement.Imad, settlement.Omad)).Take(200).ToListAsync(token);
        return View(new NetworkOverviewViewModel(banks, settlements));
    }

    [HttpGet]
    public IActionResult Provision() => View(new ProvisionBankViewModel());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Provision(ProvisionBankViewModel model, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        if (await db.Banks.AnyAsync(x => x.RoutingNumber == model.RoutingNumber || x.Bic == model.Bic
            || x.FedParticipantId == model.ParticipantId, token))
            ModelState.AddModelError("", "Routing identifier, BIC, and participant identity must be unique.");
        if (!ModelState.IsValid) return View(model);
        db.Banks.Add(new Bank { Name = model.Name.Trim(), RoutingNumber = model.RoutingNumber,
            FedParticipantId = model.ParticipantId, Bic = model.Bic, TownName = "Synthetic lab city", CountryCode = "US",
            MasterAccountBalance = model.MasterBalance,
            Customers = [new Customer { Name = "Synthetic demonstration customer",
                Accounts = [new Account { AccountNumber = $"LAB{model.RoutingNumber}", Balance = 100_000m }] }] });
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException)
        {
            ModelState.AddModelError("", "Participant could not be created. Check for a concurrent registration using the same identity.");
            return View(model);
        }
        TempData["Notice"] = "Simulated bank provisioned with a customer account. Select its maker, approver, or operations persona in Switch context.";
        return RedirectToAction(nameof(Index));
    }
}
