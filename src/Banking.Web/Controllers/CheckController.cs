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
    public async Task<IActionResult> Instructions(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var depositingAccounts = await db.Accounts.Where(x => x.Customer.BankId == bankId)
            .OrderBy(x => x.Customer.Name).Select(x => new CheckInstructionAccountViewModel(
                x.Customer.Name, x.AccountNumber, x.Balance - x.HeldBalance))
            .AsNoTracking().ToListAsync(token);
        var payingAccounts = await db.Accounts.Where(x => x.Customer.BankId != bankId
                && x.Customer.Bank.CountryCode == bank.CountryCode)
            .OrderBy(x => x.Customer.Bank.Name).ThenBy(x => x.Customer.Name)
            .Select(x => new CheckInstructionDestinationViewModel(x.Customer.Bank.Name,
                x.Customer.Name, x.Customer.Bank.RoutingNumber, x.AccountNumber))
            .AsNoTracking().ToListAsync(token);
        return View(new CheckInstructionsViewModel(bank, depositingAccounts, payingAccounts));
    }

    [HttpGet]
    public async Task<IActionResult> Generator(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        return View(await PopulateGeneratorAsync(new CheckTiffGeneratorViewModel(), bankId, db, token));
    }

    [HttpGet]
    public async Task<IActionResult> DownloadTiff(CheckTiffGeneratorViewModel model, CheckImageSide side,
        CancellationToken token)
    {
        if (!ModelState.IsValid)
        {
            await using var db = await dbFactory.CreateDbContextAsync(token);
            var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
            return View("Generator", await PopulateGeneratorAsync(model, bankId, db, token));
        }
        var content = CheckTiffGenerator.Generate(side, model.RoutingNumber,
            model.AccountNumber, model.CheckNumber, model.Amount);
        var suffix = side.ToString().ToLowerInvariant();
        return File(content, "image/tiff", $"sample-check-{model.CheckNumber}-{suffix}.tiff");
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
            Description = "Check captured with front/back TIFF images and a parsed MICR line. Provisional credit is uncollected and unavailable." });
        AccountAvailabilityPolicy.PlaceProvisionalCredit(account, deposit.Amount);
        db.CheckDeposits.Add(deposit);
        await db.SaveChangesAsync(token);
        await LearningRunLinker.AttachAsync(HttpContext, db, bankId, "Check", deposit.Id, token);
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
        var depositing = await db.Accounts.Where(x => x.Customer.BankId == bankId)
            .OrderBy(x => x.Customer.Name).Select(x => new CheckAccountChoice(x.Id, x.Customer.Name,
                x.AccountNumber, x.Balance - x.HeldBalance, x.Customer.Bank.RoutingNumber))
            .AsNoTracking().ToListAsync(token);
        var country = await db.Banks.Where(x => x.Id == bankId).Select(x => x.CountryCode).SingleAsync(token);
        var paying = await db.Accounts.Where(x => x.Customer.BankId != bankId
                && x.Customer.Bank.CountryCode == country && x.Balance - x.HeldBalance > 0)
            .OrderBy(x => x.Customer.Bank.Name).ThenBy(x => x.Customer.Name)
            .Select(x => new CheckAccountChoice(x.Id, x.Customer.Name, x.AccountNumber,
                x.Balance - x.HeldBalance, x.Customer.Bank.RoutingNumber))
            .AsNoTracking().ToListAsync(token);
        var history = await db.CheckDeposits.Where(x => x.DepositoryBankId == bankId)
            .OrderByDescending(x => x.CreatedDate).Take(20)
            .Select(x => new CheckHistoryChoice(x.DepositorName, x.RawMicrLine, x.Amount, x.CheckNumber))
            .AsNoTracking().ToListAsync(token);
        model.Accounts = depositing.Select(x => new SelectListItem(
            $"{x.CustomerName} · {x.AccountNumber} · available {x.AvailableBalance:C}", x.Id.ToString())).ToList();
        model.Choices = CheckCaptureChoices(depositing, paying, history);
        return model;
    }

    private static async Task<CheckTiffGeneratorViewModel> PopulateGeneratorAsync(CheckTiffGeneratorViewModel model,
        Guid bankId, BankingDbContext db, CancellationToken token)
    {
        var country = await db.Banks.Where(x => x.Id == bankId).Select(x => x.CountryCode).SingleAsync(token);
        var paying = await db.Accounts.Where(x => x.Customer.BankId != bankId
                && x.Customer.Bank.CountryCode == country && x.Balance - x.HeldBalance > 0)
            .OrderBy(x => x.Customer.Bank.Name).ThenBy(x => x.Customer.Name)
            .Select(x => new CheckAccountChoice(x.Id, x.Customer.Name, x.AccountNumber,
                x.Balance - x.HeldBalance, x.Customer.Bank.RoutingNumber))
            .AsNoTracking().ToListAsync(token);
        var history = await db.CheckDeposits.Where(x => x.DepositoryBankId == bankId)
            .OrderByDescending(x => x.CreatedDate).Take(20)
            .Select(x => new CheckHistoryChoice(x.DepositorName, x.RawMicrLine, x.Amount, x.CheckNumber))
            .AsNoTracking().ToListAsync(token);
        model.Choices = CheckGeneratorChoices(paying, history);
        return model;
    }

    private static Dictionary<string, AchFieldChoices> CheckCaptureChoices(IReadOnlyList<CheckAccountChoice> depositing,
        IReadOnlyList<CheckAccountChoice> paying, IReadOnlyList<CheckHistoryChoice> history)
    {
        var depositorOptions = depositing.Select(x => CheckOption($"{x.CustomerName} · {x.AccountNumber}",
            $"{x.AvailableBalance:C} available", (nameof(CreateCheckDepositViewModel.DepositingAccountId), x.Id.ToString()),
            (nameof(CreateCheckDepositViewModel.DepositorName), x.CustomerName))).ToList();
        var micrOptions = paying.Select((x, index) =>
        {
            var checkNumber = (1001 + index).ToString();
            var amount = Math.Max(.01m, Math.Min(125.50m, x.AvailableBalance));
            return CheckOption($"{x.CustomerName} · {x.RoutingNumber}", $"Account {x.AccountNumber} · {x.AvailableBalance:C} available",
                (nameof(CreateCheckDepositViewModel.RawMicrLine), $"t{x.RoutingNumber}t {x.AccountNumber}o {checkNumber}"),
                (nameof(CreateCheckDepositViewModel.Amount), amount.ToString("0.00")));
        }).ToList();
        var historicalMicr = history.Select(x => CheckOption(x.RawMicrLine, $"Previous deposit · {x.Amount:C}",
            (nameof(CreateCheckDepositViewModel.RawMicrLine), x.RawMicrLine),
            (nameof(CreateCheckDepositViewModel.Amount), x.Amount.ToString("0.00"))));
        micrOptions.AddRange(historicalMicr);
        return new()
        {
            [nameof(CreateCheckDepositViewModel.DepositingAccountId)] = CheckChoice("Depositing accounts", "Selecting an account also fills the depositor name.", depositorOptions),
            [nameof(CreateCheckDepositViewModel.DepositorName)] = CheckChoice("Database depositors", "Customers belonging to the active bank are listed.", depositorOptions),
            [nameof(CreateCheckDepositViewModel.Amount)] = CheckChoice("Paying-account examples", "Suggested amounts stay within the stored paying account's available balance.", micrOptions),
            [nameof(CreateCheckDepositViewModel.RawMicrLine)] = CheckChoice("Database paying accounts", "Selecting a value builds the teaching MICR line and matching amount.", micrOptions),
            [nameof(CreateCheckDepositViewModel.Scenario)] = CheckEnumChoices<CheckProcessingScenario>(nameof(CreateCheckDepositViewModel.Scenario), "Learning scenarios", "Standard follows normal presentment; other values exercise check exceptions.")
        };
    }

    private static Dictionary<string, AchFieldChoices> CheckGeneratorChoices(IReadOnlyList<CheckAccountChoice> paying,
        IReadOnlyList<CheckHistoryChoice> history)
    {
        var accountOptions = paying.Select((x, index) => CheckOption($"{x.CustomerName} · {x.RoutingNumber}",
            $"Account {x.AccountNumber} · {x.AvailableBalance:C} available",
            (nameof(CheckTiffGeneratorViewModel.RoutingNumber), x.RoutingNumber),
            (nameof(CheckTiffGeneratorViewModel.AccountNumber), x.AccountNumber),
            (nameof(CheckTiffGeneratorViewModel.CheckNumber), (1001 + index).ToString()),
            (nameof(CheckTiffGeneratorViewModel.Amount), Math.Max(.01m, Math.Min(125.50m, x.AvailableBalance)).ToString("0.00")))).ToList();
        var checkNumbers = history.Select(x => x.CheckNumber).Prepend("1001").Distinct()
            .Select(x => CheckOption(x, "Fixture check number", (nameof(CheckTiffGeneratorViewModel.CheckNumber), x))).ToList();
        var amounts = history.Select(x => x.Amount).Prepend(125.50m).Distinct()
            .Select(x => CheckOption(x.ToString("C"), "Previous or standard fixture amount",
                (nameof(CheckTiffGeneratorViewModel.Amount), x.ToString("0.00")))).ToList();
        return new()
        {
            [nameof(CheckTiffGeneratorViewModel.RoutingNumber)] = CheckChoice("Database paying accounts", "Selecting one fills routing, account, check number, and an affordable amount.", accountOptions),
            [nameof(CheckTiffGeneratorViewModel.AccountNumber)] = CheckChoice("Database paying accounts", "Selecting one fills all related fixture fields.", accountOptions),
            [nameof(CheckTiffGeneratorViewModel.CheckNumber)] = CheckChoice("Check numbers", "Recent database values and the standard fixture number are listed.", checkNumbers),
            [nameof(CheckTiffGeneratorViewModel.Amount)] = CheckChoice("Fixture amounts", "Recent database values and the standard fixture amount are listed.", amounts)
        };
    }

    private static AchFieldChoices CheckEnumChoices<T>(string field, string title, string guidance) where T : struct, Enum =>
        CheckChoice(title, guidance, Enum.GetValues<T>().Select(x => CheckOption(x.ToString(), null,
            (field, Convert.ToInt32(x).ToString()))).ToList());
    private static AchFieldChoices CheckChoice(string title, string guidance, IReadOnlyList<AchValueOption> options) => new(title, guidance, options);
    private static AchValueOption CheckOption(string label, string? detail, params (string Field, string Value)[] values) =>
        new(label, detail, values.ToDictionary(x => x.Field, x => x.Value));
    private sealed record CheckAccountChoice(Guid Id, string CustomerName, string AccountNumber,
        decimal AvailableBalance, string RoutingNumber);
    private sealed record CheckHistoryChoice(string DepositorName, string RawMicrLine, decimal Amount, string CheckNumber);
}
