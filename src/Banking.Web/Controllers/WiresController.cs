using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Xml.Linq;

namespace Banking.Web.Controllers;

[Authorize]
public sealed class WiresController(IDbContextFactory<BankingDbContext> dbFactory, IMessageBus bus,
    IIsoMessageService iso) : Controller
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
        if (model.Rail == PaymentRail.FedNow && model.Amount > FedNowProfile.CustomerCreditTransferLimit)
            ModelState.AddModelError(nameof(model.Amount),
                $"FedNow customer credit transfers cannot exceed {FedNowProfile.CustomerCreditTransferLimit:C0}.");
        if (model.Rail == PaymentRail.FedNow && receiverBank is not null
            && (!receiverBank.FedNowEnabled || !receiverBank.FedNowReceiveEnabled || !receiverBank.FedNowOnline))
            ModelState.AddModelError(nameof(model.ReceiverBankId),
                "The receiving bank is not currently enabled and online to receive FedNow payments.");
        if (!ModelState.IsValid) return View(await PopulateAsync(model, bankId, db, token));

        var beneficiaryAccount = model.BeneficiaryAccountNumber.Trim();
        var receiverAccount = await db.Accounts.Include(x => x.Customer).FirstOrDefaultAsync(x =>
            x.Customer.BankId == model.ReceiverBankId && x.AccountNumber == beneficiaryAccount, token);
        var wire = new WireTransfer
        {
            BankId = bankId, SenderBankId = bankId, ReceiverBankId = model.ReceiverBankId,
            FromAccountId = account!.Id, ToAccountId = receiverAccount?.Id,
            Direction = WireDirection.Outgoing, Status = WireStatus.Created, Amount = model.Amount,
            SenderName = account.Customer.Name, ReceiverName = model.ReceiverName.Trim(),
            BeneficiaryAccountNumber = beneficiaryAccount, Scenario = model.Scenario,
            Rail = model.Rail,
            Events = [new WireEvent { EventType = "Created",
                Description = $"Outgoing {model.Rail} payment created using the {model.Scenario} lab scenario." }]
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
        var model = await LoadDetailsAsync(id, bankId, db, token);
        return model is null ? NotFound() : View(model);
    }

    [HttpGet]
    public async Task<IActionResult> DetailsFragment(Guid id, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var model = await LoadDetailsAsync(id, bankId, db, token);
        return model is null ? NotFound() : PartialView("_WireDetails", model);
    }

    private async Task<WireDetailsViewModel?> LoadDetailsAsync(Guid id, Guid bankId,
        BankingDbContext db, CancellationToken token)
    {
        var wire = await db.WireTransfers.Include(x => x.Bank).Include(x => x.Events)
            .Include(x => x.IsoMessages).AsSplitQuery().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.BankId == bankId, token);
        if (wire is null) return null;

        var deliveries = await db.MessageDeliveries.Where(x => x.WireTransferId == id)
            .OrderBy(x => x.UpdatedDate).AsNoTracking().ToListAsync(token);
        var events = wire.Events.OrderBy(x => x.CreatedDate).ToList();
        var stages = events.Select((item, index) => new ProcessingStageViewModel(
            item,
            ServiceFor(item.EventType),
            index == 0 ? null : item.CreatedDate - events[index - 1].CreatedDate)).ToList();
        var messages = wire.IsoMessages.OrderBy(x => x.CreatedDate).Select(x => BuildMessage(x)).ToList();
        var ledgerEntries = await db.LedgerEntries.Where(x => x.WireTransferId == id)
            .OrderBy(x => x.CreatedDate).ThenBy(x => x.AccountCode).AsNoTracking().ToListAsync(token);
        var failure = deliveries.LastOrDefault(x => !string.IsNullOrWhiteSpace(x.LastError))?.LastError
            ?? events.LastOrDefault(x => x.EventType.Contains("Reject", StringComparison.OrdinalIgnoreCase))?.Description;
        var lastActivity = new[]
        {
            events.LastOrDefault()?.CreatedDate ?? wire.CreatedDate,
            wire.IsoMessages.OrderBy(x => x.CreatedDate).LastOrDefault()?.CreatedDate ?? wire.CreatedDate,
            deliveries.LastOrDefault()?.UpdatedDate ?? wire.CreatedDate
        }.Max();
        return new WireDetailsViewModel(wire, deliveries, stages, messages, ledgerEntries, failure,
            lastActivity - wire.CreatedDate);
    }

    private IsoMessageViewModel BuildMessage(IsoMessage message)
    {
        try
        {
            var document = XDocument.Parse(message.XmlPayload);
            var result = iso.Validate(message.XmlPayload);
            var validation = result.IsValid
                ? $"Valid {result.MessageType} lab profile · business header, UETR, and required fields present"
                : string.Join(" ", result.Errors);
            return new IsoMessageViewModel(message, document.ToString(), true, result.IsValid, validation);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            return new IsoMessageViewModel(message, message.XmlPayload, false, false,
                $"Invalid XML · {ex.Message}");
        }
    }

    private static string ServiceFor(string eventType) => eventType switch
    {
        "Created" => "Banking.Web",
        "Validated" or "Rejected" or "IsoGenerated" => "Banking.WireService",
        "PendingAtFed" or "AcceptedByFed" or "RejectedByFed" => "Fed rail simulator → MessageManager",
        "SentToFed" or "Settled" or "Delivered" or "ReceivedFromFed" or "Posted" or "HoldReleased" or "Completed"
            => "Banking.MessageManager",
        _ => "Unknown"
    };

    private static async Task<CreateWireViewModel> PopulateAsync(CreateWireViewModel model, Guid bankId,
        BankingDbContext db, CancellationToken token)
    {
        model.Accounts = await db.Accounts.Where(x => x.Customer.BankId == bankId)
            .Select(x => new SelectListItem($"{x.Customer.Name} · {x.AccountNumber} · available {(x.Balance - x.HeldBalance):C}", x.Id.ToString()))
            .ToListAsync(token);
        model.Banks = await db.Banks.Where(x => x.Id != bankId).OrderBy(x => x.Name)
            .Select(x => new SelectListItem($"{x.Name} · {x.RoutingNumber}", x.Id.ToString()))
            .ToListAsync(token);
        return model;
    }
}
