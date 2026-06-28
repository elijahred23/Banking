using System.Security.Cryptography;
using Banking.Domain;
using Banking.Infrastructure;
using Banking.Infrastructure.Checks;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize]
public sealed class CheckController(IDbContextFactory<BankingDbContext> dbFactory,
    IMessageBus bus) : Controller
{
    public async Task<IActionResult> Index(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var deposits = await db.CheckDeposits.Include(x => x.Images)
            .Where(x => x.DepositoryBankId == bankId || x.PayingBankId == bankId)
            .OrderByDescending(x => x.CreatedDate).AsNoTracking().ToListAsync(token);
        return View(new CheckIndexViewModel(bank, deposits));
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var deposit = await db.CheckDeposits.Include(x => x.DepositoryBank)
            .Include(x => x.PayingBank).Include(x => x.DepositingAccount)
            .Include(x => x.Images).Include(x => x.Events).Include(x => x.Returns)
            .Include(x => x.LedgerEntries).Include(x => x.CashLetter)
            .AsSplitQuery().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id
                && (x.DepositoryBankId == bankId || x.PayingBankId == bankId), token);
        return deposit is null ? NotFound() : View(new CheckDetailsViewModel(bank, deposit));
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        return View(await PopulateAsync(new CreateCheckDepositViewModel(), bankId, db, token));
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(5_000_000)]
    public async Task<IActionResult> Create(CreateCheckDepositViewModel model,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var account = await db.Accounts.Include(x => x.Customer).SingleOrDefaultAsync(x =>
            x.Id == model.DepositingAccountId && x.Customer.BankId == bankId, token);
        if (account is null) ModelState.AddModelError(nameof(model.DepositingAccountId),
            "Select an account owned by the active bank.");

        MicrParseResult? micr = null;
        try { micr = MicrParser.Parse(model.RawMicrLine); }
        catch (ArgumentException ex) { ModelState.AddModelError(nameof(model.RawMicrLine), ex.Message); }
        byte[]? front = null;
        byte[]? back = null;
        if (model.FrontImage is not null)
        {
            try { front = await ReadAndValidateAsync(model.FrontImage, token); }
            catch (ArgumentException ex) { ModelState.AddModelError(nameof(model.FrontImage), ex.Message); }
        }
        if (model.BackImage is not null)
        {
            try { back = await ReadAndValidateAsync(model.BackImage, token); }
            catch (ArgumentException ex) { ModelState.AddModelError(nameof(model.BackImage), ex.Message); }
        }
        if (!ModelState.IsValid)
            return View(await PopulateAsync(model, bankId, db, token));

        var parsedMicr = micr!;
        var payingBankId = await db.Banks.Where(x => x.RoutingNumber == parsedMicr.RoutingNumber)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        var deposit = new CheckDeposit
        {
            DepositoryBankId = bankId,
            PayingBankId = payingBankId,
            DepositingAccountId = account!.Id,
            DepositorName = model.DepositorName.Trim(),
            PayingRoutingNumber = parsedMicr.RoutingNumber,
            PayingAccountNumber = parsedMicr.AccountNumber,
            CheckNumber = parsedMicr.CheckNumber,
            RawMicrLine = parsedMicr.NormalizedLine,
            Amount = model.Amount,
            Scenario = model.Scenario,
            Status = CheckDepositStatus.Captured
        };
        deposit.Images.Add(ToImage(deposit.Id, CheckImageSide.Front, model.FrontImage!, front!));
        deposit.Images.Add(ToImage(deposit.Id, CheckImageSide.Back, model.BackImage!, back!));
        deposit.Events.Add(new CheckEvent { EventType = "Captured",
            Description = "Check captured with front/back TIFF images and a parsed MICR line." });
        db.CheckDeposits.Add(deposit);
        await db.SaveChangesAsync(token);
        await bus.PublishAsync(Queues.CheckDepositCreated, new CheckDepositCreated(deposit.Id), token);
        TempData["Notice"] = "Check captured and queued for image cash letter processing.";
        return RedirectToAction(nameof(Details), new { id = deposit.Id });
    }

    private static async Task<byte[]> ReadAndValidateAsync(IFormFile file, CancellationToken token)
    {
        if (file.Length > TiffImageValidator.MaxBytes)
            throw new ArgumentException($"{file.FileName} exceeds the 2 MB lab limit.");
        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, token);
        var content = memory.ToArray();
        TiffImageValidator.Validate(file.FileName, file.ContentType, content);
        return content;
    }

    private static CheckImage ToImage(Guid depositId, CheckImageSide side, IFormFile file,
        byte[] content) => new()
    {
        CheckDepositId = depositId,
        Side = side,
        Format = CheckImageFormat.Tiff,
        FileName = Path.GetFileName(file.FileName),
        ContentType = file.ContentType,
        Content = content,
        SizeBytes = content.Length,
        Sha256Hash = Convert.ToHexString(SHA256.HashData(content))
    };

    private static async Task<CreateCheckDepositViewModel> PopulateAsync(
        CreateCheckDepositViewModel model, Guid bankId, BankingDbContext db,
        CancellationToken token)
    {
        model.Accounts = await db.Accounts.Where(x => x.Customer.BankId == bankId)
            .OrderBy(x => x.Customer.Name).Select(x => new SelectListItem(
                $"{x.Customer.Name} · {x.AccountNumber} · available {(x.Balance - x.HeldBalance):C}",
                x.Id.ToString())).ToListAsync(token);
        return model;
    }
}
