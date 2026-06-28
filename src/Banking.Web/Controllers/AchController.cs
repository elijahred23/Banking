using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize]
public sealed class AchController(IDbContextFactory<BankingDbContext> dbFactory, IMessageBus bus) : Controller
{
    public async Task<IActionResult> Index(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var entries = await db.AchEntries.Where(x => x.OriginatingBankId == bankId || x.ReceivingBankId == bankId)
            .OrderByDescending(x => x.CreatedDate).AsNoTracking().ToListAsync(token);
        return View(new AchIndexViewModel(bank, entries));
    }

    [HttpGet]
    public async Task<IActionResult> Instructions(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);

        var sourceAccounts = await db.Accounts
            .Where(x => x.Customer.BankId == bankId)
            .OrderBy(x => x.Customer.Name)
            .Select(x => new AchInstructionAccountViewModel(
                x.Customer.Name, x.AccountNumber, x.Balance - x.HeldBalance))
            .AsNoTracking()
            .ToListAsync(token);

        var destinations = await db.Accounts
            .Where(x => x.Customer.BankId != bankId && x.Customer.Bank.CountryCode == bank.CountryCode)
            .OrderBy(x => x.Customer.Bank.Name)
            .ThenBy(x => x.Customer.Name)
            .Select(x => new AchInstructionDestinationViewModel(
                x.Customer.Bank.Name, x.Customer.Name, x.AccountNumber, x.Customer.Bank.RoutingNumber))
            .AsNoTracking()
            .ToListAsync(token);

        return View(new AchInstructionsViewModel(bank, sourceAccounts, destinations));
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        return View(await PopulateAsync(new CreateAchViewModel(), bankId, db, token));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateAchViewModel model, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var account = await db.Accounts.Include(x => x.Customer)
            .SingleOrDefaultAsync(x => x.Id == model.OriginatingAccountId && x.Customer.BankId == bankId, token);
        if (account is null) ModelState.AddModelError(nameof(model.OriginatingAccountId), "Select an account owned by the active bank.");
        if (model.EffectiveEntryDate < DateOnly.FromDateTime(DateTime.Today))
            ModelState.AddModelError(nameof(model.EffectiveEntryDate), "The effective date cannot be in the past.");
        if (model.Purpose == AchPaymentPurpose.TaxPaymentEftps)
            ModelState.AddModelError(nameof(model.Purpose), "Use the EFTPS-style form for tax payments.");
        if (!ModelState.IsValid) return View(await PopulateAsync(model, bankId, db, token));
        await CreateEntryAsync(db, account!, bankId, model.CompanyName, model.CompanyId, model.SecCode,
            model.ReceiverName, model.ReceivingRoutingNumber, model.ReceivingAccountNumber,
            model.TransactionCode, model.Amount, model.EntryDescription, model.EffectiveEntryDate,
            model.Addenda05, model.Purpose, model.Scenario, token);
        TempData["Notice"] = "ACH entry created. It will be included at the next simulated batch cutoff.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Batches(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var batches = await db.AchBatches.Include(x => x.Entries).Where(x => x.OriginatingBankId == bankId)
            .OrderByDescending(x => x.EffectiveEntryDate).AsNoTracking().ToListAsync(token);
        return View(new AchBatchIndexViewModel(bank, batches));
    }

    public async Task<IActionResult> Files(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var files = await db.AchFiles.Include(x => x.Batches).ThenInclude(x => x.Entries)
            .Where(x => x.OriginatingBankId == bankId).OrderByDescending(x => x.CreatedDate).AsNoTracking().ToListAsync(token);
        return View(new AchFileIndexViewModel(bank, files));
    }

    public async Task<IActionResult> Returns(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var returns = await db.AchReturns.Include(x => x.Entry).Where(x => x.Entry.OriginatingBankId == bankId)
            .OrderByDescending(x => x.ReceivedDate).AsNoTracking().ToListAsync(token);
        var nocs = await db.AchNotificationsOfChange.Include(x => x.Entry).Where(x => x.Entry.OriginatingBankId == bankId)
            .OrderByDescending(x => x.ReceivedDate).AsNoTracking().ToListAsync(token);
        return View(new AchExceptionIndexViewModel(bank, returns, nocs));
    }

    [HttpGet]
    public async Task<IActionResult> Eftps(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        return View(await PopulateEftpsAsync(new CreateEftpsViewModel(), bankId, db, token));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Eftps(CreateEftpsViewModel model, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var account = await db.Accounts.Include(x => x.Customer)
            .SingleOrDefaultAsync(x => x.Id == model.OriginatingAccountId && x.Customer.BankId == bankId, token);
        if (account is null) ModelState.AddModelError(nameof(model.OriginatingAccountId), "Select an account owned by the active bank.");
        string? addenda = null;
        if (ModelState.IsValid)
        {
            try { addenda = EftpsTaxPaymentAddendaBuilder.Build(new EftpsTaxPayment {
                TaxpayerIdentificationNumber = model.TaxpayerIdentificationNumber,
                TaxTypeCode = model.TaxTypeCode, TaxPeriodEndDate = model.TaxPeriodEndDate, Amount = model.Amount }); }
            catch (ArgumentException ex) { ModelState.AddModelError(string.Empty, ex.Message); }
        }
        if (!ModelState.IsValid) return View(await PopulateEftpsAsync(model, bankId, db, token));
        await CreateEntryAsync(db, account!, bankId, model.CompanyName, model.CompanyId, AchStandardEntryClass.Ccd,
            "US TREASURY EFTPS", "091036164", "234010000", AchTransactionCode.CheckingCredit,
            model.Amount, "TAXPAYMENT", model.SettlementDate, addenda, AchPaymentPurpose.TaxPaymentEftps,
            model.Scenario, token);
        TempData["Notice"] = "EFTPS-style CCD+ entry created for the next simulated batch cutoff.";
        return RedirectToAction(nameof(Index));
    }

    private async Task CreateEntryAsync(BankingDbContext db, Account account, Guid bankId,
        string companyName, string companyId, AchStandardEntryClass sec, string receiverName,
        string routing, string receiverAccount, AchTransactionCode code, decimal amount,
        string description, DateOnly effectiveDate, string? addenda, AchPaymentPurpose purpose,
        AchProcessingScenario scenario, CancellationToken token)
    {
        var receivingBankId = await db.Banks.Where(x => x.RoutingNumber == routing).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        var entry = new AchEntry { OriginatingBankId = bankId, ReceivingBankId = receivingBankId,
            OriginatingAccountId = account.Id, CompanyName = companyName.Trim(), CompanyId = companyId.Trim(),
            SecCode = sec, ReceiverName = receiverName.Trim(), ReceivingRoutingNumber = routing.Trim(),
            ReceivingAccountNumber = receiverAccount.Trim(), TransactionCode = code, Amount = amount,
            EntryDescription = description.Trim(), EffectiveEntryDate = effectiveDate, Addenda05 = addenda?.Trim(),
            Purpose = purpose, Scenario = scenario, Status = AchEntryStatus.Entered };
        db.AchEntries.Add(entry);
        await db.SaveChangesAsync(token);
        await bus.PublishAsync(Queues.AchEntryCreated, new AchEntryCreated(entry.Id), token);
    }

    private static async Task<CreateAchViewModel> PopulateAsync(CreateAchViewModel model, Guid bankId,
        BankingDbContext db, CancellationToken token) { model.Accounts = await AccountsAsync(bankId, db, token); return model; }
    private static async Task<CreateEftpsViewModel> PopulateEftpsAsync(CreateEftpsViewModel model, Guid bankId,
        BankingDbContext db, CancellationToken token) { model.Accounts = await AccountsAsync(bankId, db, token); return model; }
    private static Task<List<SelectListItem>> AccountsAsync(Guid bankId, BankingDbContext db, CancellationToken token) =>
        db.Accounts.Where(x => x.Customer.BankId == bankId).Select(x => new SelectListItem(
            $"{x.Customer.Name} · {x.AccountNumber} · available {(x.Balance - x.HeldBalance):C}", x.Id.ToString())).ToListAsync(token);
}
