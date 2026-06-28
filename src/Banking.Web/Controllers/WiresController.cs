using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize]
public sealed class WiresController(IDbContextFactory<BankingDbContext> dbFactory, IMessageBus bus) : Controller
{
    public async Task<IActionResult> Index(WireDirection? direction, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var query = db.WireTransfers.Where(x => x.BankId == bankId);
        if (direction is not null) query = query.Where(x => x.Direction == direction);
        var wires = await query.OrderByDescending(x => x.CreatedDate).AsNoTracking().ToListAsync(token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        return View(new WireIndexViewModel(bank, direction, wires));
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        return View(await PopulateAsync(new CreateWireViewModel(), bankId, db, token));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateWireViewModel model, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var account = await db.Accounts.Include(x => x.Customer)
            .SingleOrDefaultAsync(x => x.Id == model.FromAccountId && x.Customer.BankId == bankId, token);
        var receiverBank = await db.Banks.SingleOrDefaultAsync(x => x.Id == model.ReceiverBankId, token);
        if (account is null) ModelState.AddModelError(nameof(model.FromAccountId), "Select an account owned by the active bank.");
        if (receiverBank is null || receiverBank.Id == bankId)
            ModelState.AddModelError(nameof(model.ReceiverBankId), "Select a different receiving bank.");
        if (!ModelState.IsValid) return View(await PopulateAsync(model, bankId, db, token));

        var receiverAccount = await db.Accounts.Include(x => x.Customer).FirstOrDefaultAsync(x =>
            x.Customer.BankId == model.ReceiverBankId && x.Customer.Name == model.ReceiverName, token);
        var wire = new WireTransfer
        {
            BankId = bankId, SenderBankId = bankId, ReceiverBankId = model.ReceiverBankId,
            FromAccountId = account!.Id, ToAccountId = receiverAccount?.Id,
            Direction = WireDirection.Outgoing, Status = WireStatus.Created, Amount = model.Amount,
            SenderName = account.Customer.Name, ReceiverName = model.ReceiverName.Trim(),
            Events = [new WireEvent { EventType = "Created", Description = "Outgoing wire created by operator." }]
        };
        db.WireTransfers.Add(wire);
        await db.SaveChangesAsync(token);
        await bus.PublishAsync(Queues.WireCreated, new WireCreated(wire.Id), token);
        TempData["Notice"] = $"Wire {wire.Id.ToString()[..8]} created and queued for validation.";
        return RedirectToAction(nameof(Details), new { id = wire.Id });
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var wire = await db.WireTransfers.Include(x => x.Bank).Include(x => x.Events)
            .Include(x => x.IsoMessages).AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.BankId == bankId, token);
        return wire is null ? NotFound() : View(wire);
    }

    private static async Task<CreateWireViewModel> PopulateAsync(CreateWireViewModel model, Guid bankId,
        BankingDbContext db, CancellationToken token)
    {
        model.Accounts = await db.Accounts.Where(x => x.Customer.BankId == bankId)
            .Select(x => new SelectListItem($"{x.Customer.Name} · {x.AccountNumber} · {x.Balance:C}", x.Id.ToString()))
            .ToListAsync(token);
        model.Banks = await db.Banks.Where(x => x.Id != bankId).OrderBy(x => x.Name)
            .Select(x => new SelectListItem($"{x.Name} · {x.RoutingNumber}", x.Id.ToString()))
            .ToListAsync(token);
        return model;
    }
}
