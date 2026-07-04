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
        if (model.Purpose == AchPaymentPurpose.TaxPaymentEftps)
            ModelState.AddModelError(nameof(model.Purpose), "Use the EFTPS-style form for tax payments.");
        if (account is not null)
        {
            var candidate = Candidate(model, account, bankId);
            foreach (var error in AchRules.Validate(candidate, DateOnly.FromDateTime(DateTime.Today)))
                ModelState.AddModelError(error.Field, error.Message);
            if (AchRules.IsCredit(model.TransactionCode) && account.AvailableBalance < model.Amount)
                ModelState.AddModelError(nameof(model.Amount), "The originating account has insufficient available funds.");
        }
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
        if (model.SettlementDate < DateOnly.FromDateTime(DateTime.Today))
            ModelState.AddModelError(nameof(model.SettlementDate), "The settlement date cannot be in the past.");
        else if (!AchBusinessCalendar.IsBankingDay(model.SettlementDate))
            ModelState.AddModelError(nameof(model.SettlementDate), "The settlement date must be a Federal Reserve banking day.");
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
        await LearningRunLinker.AttachAsync(HttpContext, db, bankId, "ACH", entry.Id, token);
        await bus.PublishAsync(Queues.AchEntryCreated, new AchEntryCreated(entry.Id), token);
    }

    private static AchEntry Candidate(CreateAchViewModel model, Account account, Guid bankId) => new()
    {
        OriginatingBankId = bankId, OriginatingAccountId = account.Id, CompanyName = model.CompanyName,
        CompanyId = model.CompanyId, SecCode = model.SecCode, ReceiverName = model.ReceiverName,
        ReceivingRoutingNumber = model.ReceivingRoutingNumber, ReceivingAccountNumber = model.ReceivingAccountNumber,
        TransactionCode = model.TransactionCode, Amount = model.Amount, EntryDescription = model.EntryDescription,
        EffectiveEntryDate = model.EffectiveEntryDate, Addenda05 = model.Addenda05, Purpose = model.Purpose,
        Scenario = model.Scenario
    };

    private static async Task<CreateAchViewModel> PopulateAsync(CreateAchViewModel model, Guid bankId,
        BankingDbContext db, CancellationToken token)
    {
        var data = await FormDataAsync(bankId, db, token);
        model.Accounts = AccountItems(data.Accounts);
        model.Choices = StandardChoices(data);
        return model;
    }

    private static async Task<CreateEftpsViewModel> PopulateEftpsAsync(CreateEftpsViewModel model, Guid bankId,
        BankingDbContext db, CancellationToken token)
    {
        var data = await FormDataAsync(bankId, db, token);
        model.Accounts = AccountItems(data.Accounts);
        model.Choices = EftpsChoices(data);
        return model;
    }

    private static async Task<AchFormData> FormDataAsync(Guid bankId, BankingDbContext db, CancellationToken token)
    {
        var country = await db.Banks.Where(x => x.Id == bankId).Select(x => x.CountryCode).SingleAsync(token);
        var accounts = await db.Accounts.Where(x => x.Customer.BankId == bankId)
            .OrderBy(x => x.Customer.Name).ThenBy(x => x.AccountNumber)
            .Select(x => new AchAccountChoice(x.Id, x.Customer.Name, x.AccountNumber, x.Balance - x.HeldBalance))
            .AsNoTracking().ToListAsync(token);
        var receivers = await db.Accounts.Where(x => x.Customer.BankId != bankId
                && x.Customer.Bank.CountryCode == country && x.AccountNumber.Length <= 17)
            .OrderBy(x => x.Customer.Bank.Name).ThenBy(x => x.Customer.Name)
            .Select(x => new AchReceiverChoice(x.Customer.Bank.Name, x.Customer.Name,
                x.Customer.Bank.RoutingNumber, x.AccountNumber))
            .AsNoTracking().ToListAsync(token);
        var history = await db.AchEntries.Where(x => x.OriginatingBankId == bankId)
            .OrderByDescending(x => x.CreatedDate).Take(50)
            .Select(x => new AchHistoryChoice(x.CompanyName, x.CompanyId, x.EntryDescription, x.Addenda05))
            .AsNoTracking().ToListAsync(token);
        return new(accounts, receivers, history);
    }

    private static List<SelectListItem> AccountItems(IEnumerable<AchAccountChoice> accounts) => accounts
        .Select(x => new SelectListItem($"{x.CustomerName} · {x.AccountNumber} · available {x.AvailableBalance:C}", x.Id.ToString()))
        .ToList();

    private static Dictionary<string, AchFieldChoices> StandardChoices(AchFormData data)
    {
        var accountOptions = data.Accounts.Select(x => Option($"{x.CustomerName} · {x.AccountNumber}",
            $"{x.AvailableBalance:C} available", (nameof(CreateAchViewModel.OriginatingAccountId), x.Id.ToString()))).ToList();
        var receiverOptions = data.Receivers.Select(x => Option($"{x.CustomerName} · {x.BankName}",
            $"ABA {x.RoutingNumber} · account {x.AccountNumber}",
            (nameof(CreateAchViewModel.ReceiverName), x.CustomerName),
            (nameof(CreateAchViewModel.ReceivingRoutingNumber), x.RoutingNumber),
            (nameof(CreateAchViewModel.ReceivingAccountNumber), x.AccountNumber))).ToList();
        var companyOptions = data.History.Select(x => new { x.CompanyName, x.CompanyId }).Distinct()
            .Select(x => Option(x.CompanyName, $"Company ID {x.CompanyId}",
                (nameof(CreateAchViewModel.CompanyName), x.CompanyName),
                (nameof(CreateAchViewModel.CompanyId), x.CompanyId))).ToList();
        if (companyOptions.Count == 0)
            companyOptions.AddRange(data.Accounts.Select(x => Option(x.CustomerName, "Customer name from this bank",
                (nameof(CreateAchViewModel.CompanyName), x.CustomerName[..Math.Min(16, x.CustomerName.Length)]))));

        var entryProfiles = StandardEntryProfiles();
        return new()
        {
            [nameof(CreateAchViewModel.OriginatingAccountId)] = Choices("Funded accounts", "Only accounts owned by the active bank are allowed.", accountOptions),
            [nameof(CreateAchViewModel.CompanyName)] = Choices("Company profiles", "Choose a previous profile to fill both company fields, or enter up to 16 characters.", companyOptions),
            [nameof(CreateAchViewModel.CompanyId)] = Choices("Company IDs", "Previous ACH profiles are listed. A new ID may contain up to 10 characters.", companyOptions),
            [nameof(CreateAchViewModel.Purpose)] = Choices("Valid ACH entry profiles", "Selecting a profile fills purpose, SEC code, and transaction code together.", entryProfiles),
            [nameof(CreateAchViewModel.SecCode)] = Choices("Valid ACH entry profiles", "Selecting a profile fills purpose, SEC code, and transaction code together.", entryProfiles),
            [nameof(CreateAchViewModel.TransactionCode)] = Choices("Valid ACH entry profiles", "Selecting a profile fills purpose, SEC code, and transaction code together.", entryProfiles),
            [nameof(CreateAchViewModel.ReceiverName)] = Choices("Database receivers", "Selecting a receiver fills all three receiving fields.", receiverOptions),
            [nameof(CreateAchViewModel.ReceivingRoutingNumber)] = Choices("Database receivers", "Only valid ABA routing numbers are listed; selecting one fills the receiver.", receiverOptions),
            [nameof(CreateAchViewModel.ReceivingAccountNumber)] = Choices("Database receivers", "Selecting an account fills the receiver and routing number.", receiverOptions),
            [nameof(CreateAchViewModel.Amount)] = Choices("Available account balances", "Enter $0.01 through $999,999,999.99. Credits cannot exceed the selected account's available balance.",
                data.Accounts.Where(x => x.AvailableBalance > 0).Select(x => Option($"{Math.Min(100m, x.AvailableBalance):C} from {x.CustomerName}",
                    $"{x.AvailableBalance:C} available", (nameof(CreateAchViewModel.OriginatingAccountId), x.Id.ToString()),
                    (nameof(CreateAchViewModel.Amount), Math.Min(100m, x.AvailableBalance).ToString("0.00")))).ToList()),
            [nameof(CreateAchViewModel.EffectiveEntryDate)] = DateChoices(nameof(CreateAchViewModel.EffectiveEntryDate), "Banking-day effective dates"),
            [nameof(CreateAchViewModel.EntryDescription)] = Choices("Entry descriptions", "Use 1 to 10 characters.",
                TextOptions(data.History.Select(x => x.EntryDescription).Concat(["PAYROLL", "VENDOR", "PAYMENT"]), nameof(CreateAchViewModel.EntryDescription))),
            [nameof(CreateAchViewModel.Scenario)] = EnumChoices<AchProcessingScenario>(nameof(CreateAchViewModel.Scenario), "Learning scenarios", "Standard follows the normal flow; other values deliberately exercise exceptions."),
            [nameof(CreateAchViewModel.Addenda05)] = Choices("Addenda values", "Optional free-form payment information, up to 80 characters.",
                TextOptions(data.History.Select(x => x.Addenda05).Where(x => !string.IsNullOrWhiteSpace(x))!, nameof(CreateAchViewModel.Addenda05)))
        };
    }

    private static Dictionary<string, AchFieldChoices> EftpsChoices(AchFormData data)
    {
        var accountOptions = data.Accounts.Select(x => Option($"{x.CustomerName} · {x.AccountNumber}",
            $"{x.AvailableBalance:C} available", (nameof(CreateEftpsViewModel.OriginatingAccountId), x.Id.ToString()))).ToList();
        var companyOptions = data.History.Select(x => new { x.CompanyName, x.CompanyId }).Distinct()
            .Select(x => Option(x.CompanyName, $"Company ID {x.CompanyId}",
                (nameof(CreateEftpsViewModel.CompanyName), x.CompanyName), (nameof(CreateEftpsViewModel.CompanyId), x.CompanyId))).ToList();
        if (companyOptions.Count == 0)
            companyOptions.AddRange(data.Accounts.Select(x => Option(x.CustomerName, "Customer name from this bank",
                (nameof(CreateEftpsViewModel.CompanyName), x.CustomerName[..Math.Min(16, x.CustomerName.Length)]))));
        var periodOptions = Enumerable.Range(0, 4).Select(offset => DateOnly.FromDateTime(DateTime.Today).AddMonths(-3 * offset))
            .Select(x => new DateOnly(x.Year, ((x.Month - 1) / 3 + 1) * 3, 1))
            .Select(x => x.AddMonths(1).AddDays(-1))
            .Select(x => Option(x.ToString("yyyy-MM-dd"), "Calendar quarter end",
                (nameof(CreateEftpsViewModel.TaxPeriodEndDate), x.ToString("yyMMdd")))).ToList();
        return new()
        {
            [nameof(CreateEftpsViewModel.OriginatingAccountId)] = Choices("Funded accounts", "Only accounts owned by the active bank are allowed.", accountOptions),
            [nameof(CreateEftpsViewModel.CompanyName)] = Choices("Company profiles", "Choose a previous profile to fill both company fields, or enter up to 16 characters.", companyOptions),
            [nameof(CreateEftpsViewModel.CompanyId)] = Choices("Company IDs", "Previous ACH profiles are listed. A new ID may contain up to 10 characters.", companyOptions),
            [nameof(CreateEftpsViewModel.TaxpayerIdentificationNumber)] = Choices("Taxpayer ID format", "Enter a simulated 9-digit EIN or SSN without punctuation.", [Option("123456789", "Simulated example", (nameof(CreateEftpsViewModel.TaxpayerIdentificationNumber), "123456789"))]),
            [nameof(CreateEftpsViewModel.TaxTypeCode)] = Choices("Common tax type codes", "Select a simulated EFTPS tax form code.",
                [Option("94105", "Form 941 employment tax", (nameof(CreateEftpsViewModel.TaxTypeCode), "94105")), Option("11206", "Form 1120 corporate income tax", (nameof(CreateEftpsViewModel.TaxTypeCode), "11206")), Option("10406", "Form 1040 estimated tax", (nameof(CreateEftpsViewModel.TaxTypeCode), "10406"))]),
            [nameof(CreateEftpsViewModel.TaxPeriodEndDate)] = Choices("Recent tax periods", "The submitted value uses YYMMDD format.", periodOptions),
            [nameof(CreateEftpsViewModel.Amount)] = Choices("Available account balances", "Enter $0.01 through $999,999,999.99 without exceeding the selected account's available balance.",
                data.Accounts.Where(x => x.AvailableBalance > 0).Select(x => Option($"{Math.Min(100m, x.AvailableBalance):C} from {x.CustomerName}",
                    $"{x.AvailableBalance:C} available", (nameof(CreateEftpsViewModel.OriginatingAccountId), x.Id.ToString()),
                    (nameof(CreateEftpsViewModel.Amount), Math.Min(100m, x.AvailableBalance).ToString("0.00")))).ToList()),
            [nameof(CreateEftpsViewModel.SettlementDate)] = DateChoices(nameof(CreateEftpsViewModel.SettlementDate), "Banking-day settlement dates"),
            [nameof(CreateEftpsViewModel.Scenario)] = EnumChoices<AchProcessingScenario>(nameof(CreateEftpsViewModel.Scenario), "Learning scenarios", "Standard follows the normal flow; other values deliberately exercise exceptions.")
        };
    }

    private static AchFieldChoices DateChoices(string field, string title)
    {
        var date = DateOnly.FromDateTime(DateTime.Today).AddDays(-1);
        var options = new List<AchValueOption>();
        while (options.Count < 5)
        {
            date = AchBusinessCalendar.NextBankingDay(date);
            options.Add(Option(date.ToString("dddd, MMMM d"), date.ToString("yyyy-MM-dd"), (field, date.ToString("yyyy-MM-dd"))));
        }
        return Choices(title, "Only Federal Reserve banking days are accepted.", options);
    }

    private static AchFieldChoices EnumChoices<T>(string field, string title, string guidance) where T : struct, Enum =>
        Choices(title, guidance, Enum.GetValues<T>().Select(x => Option(x.ToString(), null,
            (field, Convert.ToInt32(x).ToString()))).ToList());

    private static List<AchValueOption> StandardEntryProfiles()
    {
        var profiles = new List<AchValueOption>();
        Add(AchPaymentPurpose.Payroll, AchStandardEntryClass.Ppd, true);
        Add(AchPaymentPurpose.ConsumerDebit, AchStandardEntryClass.Ppd, false);
        Add(AchPaymentPurpose.ConsumerDebit, AchStandardEntryClass.Web, false);
        Add(AchPaymentPurpose.ConsumerDebit, AchStandardEntryClass.Tel, false);
        Add(AchPaymentPurpose.VendorPayment, AchStandardEntryClass.Ccd, true);
        Add(AchPaymentPurpose.VendorPayment, AchStandardEntryClass.Ccd, false);
        Add(AchPaymentPurpose.VendorPayment, AchStandardEntryClass.Ctx, true);
        Add(AchPaymentPurpose.VendorPayment, AchStandardEntryClass.Ctx, false);
        return profiles;

        void Add(AchPaymentPurpose purpose, AchStandardEntryClass sec, bool credit)
        {
            foreach (var savings in new[] { false, true })
            {
                var code = (credit, savings) switch
                {
                    (true, false) => AchTransactionCode.CheckingCredit,
                    (true, true) => AchTransactionCode.SavingsCredit,
                    (false, false) => AchTransactionCode.CheckingDebit,
                    _ => AchTransactionCode.SavingsDebit
                };
                profiles.Add(Option($"{purpose} · {sec.ToString().ToUpperInvariant()} · {(savings ? "savings" : "checking")} {(credit ? "credit" : "debit")}",
                    credit ? "Sends funds to the receiver" : "Collects funds from the receiver",
                    (nameof(CreateAchViewModel.Purpose), Convert.ToInt32(purpose).ToString()),
                    (nameof(CreateAchViewModel.SecCode), Convert.ToInt32(sec).ToString()),
                    (nameof(CreateAchViewModel.TransactionCode), Convert.ToInt32(code).ToString())));
            }
        }
    }

    private static List<AchValueOption> TextOptions(IEnumerable<string?> values, string field) => values
        .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(10).Select(x => Option(x, null, (field, x))).ToList();

    private static AchFieldChoices Choices(string title, string guidance, IReadOnlyList<AchValueOption> options) =>
        new(title, guidance, options);

    private static AchValueOption Option(string label, string? detail, params (string Field, string Value)[] values) =>
        new(label, detail, values.ToDictionary(x => x.Field, x => x.Value));

    private sealed record AchFormData(IReadOnlyList<AchAccountChoice> Accounts,
        IReadOnlyList<AchReceiverChoice> Receivers, IReadOnlyList<AchHistoryChoice> History);
    private sealed record AchAccountChoice(Guid Id, string CustomerName, string AccountNumber, decimal AvailableBalance);
    private sealed record AchReceiverChoice(string BankName, string CustomerName, string RoutingNumber, string AccountNumber);
    private sealed record AchHistoryChoice(string CompanyName, string CompanyId, string EntryDescription, string? Addenda05);
}
