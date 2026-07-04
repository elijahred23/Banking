using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize]
public sealed class AchController(IDbContextFactory<BankingDbContext> dbFactory,
    SimulationStateService simulation, NachaFileParser parser) : Controller
{
    public async Task<IActionResult> Index(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var entries = await db.AchEntries.Where(x => x.OriginatingBankId == bankId
                || x.ReceivingBankId == bankId && (x.Status == AchEntryStatus.SentToOperator
                    || x.Status == AchEntryStatus.Settled || x.Status == AchEntryStatus.Posted
                    || x.Status == AchEntryStatus.Returned || x.Status == AchEntryStatus.NocReceived))
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

    [HttpGet, Authorize(Roles = BankSecurity.PaymentCreator)]
    public async Task<IActionResult> Create(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        return View(await PopulateAsync(new CreateAchViewModel(), bankId, db, token));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentCreator)]
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
        var entryId = await CreateEntryAsync(db, account!, bankId, model.CompanyName, model.CompanyId, model.SecCode,
            model.ReceiverName, model.ReceivingRoutingNumber, model.ReceivingAccountNumber,
            model.TransactionCode, model.Amount, model.EntryDescription, model.EffectiveEntryDate,
            model.Addenda05, model.Purpose, model.CaseType, token);
        return RedirectToAction(nameof(Details), new { id = entryId });
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

    public async Task<IActionResult> Details(Guid id, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var entry = await db.AchEntries.Include(x => x.OriginatingAccount).ThenInclude(x => x.Customer)
            .Include(x => x.Batch).ThenInclude(x => x!.File)
            .AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && (x.OriginatingBankId == bankId
                || x.ReceivingBankId == bankId && (x.Status == AchEntryStatus.SentToOperator
                    || x.Status == AchEntryStatus.Settled || x.Status == AchEntryStatus.Posted
                    || x.Status == AchEntryStatus.Returned || x.Status == AchEntryStatus.NocReceived)), token);
        if (entry is null) return NotFound();
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var returns = await db.AchReturns.Where(x => x.AchEntryId == id).OrderBy(x => x.ReceivedDate)
            .AsNoTracking().ToListAsync(token);
        var nocs = await db.AchNotificationsOfChange.Where(x => x.AchEntryId == id).OrderBy(x => x.ReceivedDate)
            .AsNoTracking().ToListAsync(token);
        var journal = await db.AchLedgerEntries.Where(x => x.AchEntryId == id).OrderBy(x => x.CreatedDate)
            .AsNoTracking().ToListAsync(token);
        var receiver = entry.ReceivingBankId is null ? null : await db.Accounts.Include(x => x.Customer)
            .AsNoTracking().FirstOrDefaultAsync(x => x.Customer.BankId == entry.ReceivingBankId
                && x.AccountNumber == entry.ReceivingAccountNumber, token);
        var balances = new List<AchBalanceViewModel>
        {
            new($"Originator · {entry.OriginatingAccount.Customer.Name}", entry.OriginatingAccount.Balance,
                entry.OriginatingAccount.HeldBalance, entry.OriginatingAccount.AvailableBalance)
        };
        if (receiver is not null) balances.Add(new($"Receiver · {receiver.Customer.Name}", receiver.Balance,
            receiver.HeldBalance, receiver.AvailableBalance));
        var currentExposure = await db.AchEntries.Where(x => x.OriginatingBankId == entry.OriginatingBankId
                && x.Id != entry.Id && (x.Status == AchEntryStatus.Validated || x.Status == AchEntryStatus.Batched
                    || x.Status == AchEntryStatus.SentToOperator)
                && (x.TransactionCode == AchTransactionCode.CheckingCredit
                    || x.TransactionCode == AchTransactionCode.SavingsCredit))
            .SumAsync(x => x.Amount, token);
        var proposed = currentExposure + (AchRules.IsCredit(entry.TransactionCode)
            && entry.Status == AchEntryStatus.PendingApproval ? entry.Amount : 0m);
        var exposure = new AchExposureViewModel(currentExposure, proposed,
            await db.Banks.Where(x => x.Id == entry.OriginatingBankId).Select(x => x.AchExposureLimit).SingleAsync(token));
        return View(new AchDetailsViewModel(bank, entry, entry.Batch, entry.Batch?.File,
            returns, nocs, journal, BuildLifecycle(entry, returns, nocs, journal), balances,
            exposure, AchLearningPolicy.DecisionFor(entry)));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentApprover)]
    public async Task<IActionResult> DecideApproval(Guid id, bool approve, string? reason,
        CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var entry = await db.AchEntries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id
            && x.OriginatingBankId == bankId, token);
        if (entry is null) return NotFound();
        if (entry.Status != AchEntryStatus.PendingApproval)
        {
            TempData["Error"] = "This ACH entry is no longer waiting for approval.";
            return RedirectToAction(nameof(Details), new { id });
        }
        var selfApproval = string.Equals(entry.CreatedBy, User.Identity!.Name, StringComparison.OrdinalIgnoreCase);
        if (selfApproval && !User.HasAllPermissions())
        {
            TempData["Error"] = "The entry creator cannot approve their own ACH entry.";
            return RedirectToAction(nameof(Details), new { id });
        }
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "Approver declined the ACH entry." : reason.Trim();
        var decisionReason = approve ? null : normalizedReason[..Math.Min(500, normalizedReason.Length)];
        var approvedBy = User.Identity.Name;
        var approvedDate = DateTimeOffset.UtcNow;
        var nextStatus = approve ? AchEntryStatus.Entered : AchEntryStatus.Rejected;
        var strategy = db.Database.CreateExecutionStrategy();
        var applied = await strategy.ExecuteAsync(async () =>
        {
            await using var writeDb = await dbFactory.CreateDbContextAsync(token);
            await using var transaction = await writeDb.Database.BeginTransactionAsync(token);
            var updated = await writeDb.AchEntries.Where(x => x.Id == id
                    && x.OriginatingBankId == bankId && x.Status == AchEntryStatus.PendingApproval)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.ApprovedBy, approvedBy)
                    .SetProperty(x => x.ApprovedDate, approvedDate)
                    .SetProperty(x => x.ApprovalDecisionReason, decisionReason)
                    .SetProperty(x => x.Status, nextStatus), token);
            if (updated != 1)
            {
                // A retry after an ambiguous commit observes its own completed decision.
                return await writeDb.AchEntries.AsNoTracking().AnyAsync(x => x.Id == id
                    && x.OriginatingBankId == bankId && x.Status == nextStatus
                    && x.ApprovedBy == approvedBy && x.ApprovedDate == approvedDate, token);
            }
            if (approve) writeDb.OutboxMessages.Add(Outbox.Message(Queues.AchEntryCreated,
                new AchEntryCreated(entry.Id), bankId));
            await writeDb.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return true;
        });
        if (!applied)
        {
            TempData["Error"] = "Another approver already answered this ACH entry.";
            return RedirectToAction(nameof(Details), new { id });
        }
        TempData["Notice"] = approve
            ? "ACH entry approved and durably queued for ODFI validation. Approval did not move funds."
            : "ACH entry rejected before validation, batching, transport, or settlement; no funds moved.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Explorer(Guid? id, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var file = id is null
            ? await db.AchFiles.Where(x => x.OriginatingBankId == bankId).OrderByDescending(x => x.CreatedDate)
                .AsNoTracking().FirstOrDefaultAsync(token)
            : await db.AchFiles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OriginatingBankId == bankId, token);
        if (id is not null && file is null) return NotFound();
        var payload = file?.RawNachaPayload ?? "";
        return View(new AchNachaSandboxViewModel { FileId = file?.Id, Payload = payload,
            Records = NachaRecordAnnotator.Annotate(payload) });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Explorer(AchNachaSandboxViewModel model, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        if (model.FileId is not null && !await db.AchFiles.AnyAsync(x => x.Id == model.FileId
            && x.OriginatingBankId == bankId, token)) return NotFound();
        model.Result = parser.Validate(model.Payload);
        model.Records = NachaRecordAnnotator.Annotate(model.Payload);
        model.PredictionCorrect = string.Equals(model.Prediction, model.Result.IsValid ? "Accepted" : "Rejected",
            StringComparison.OrdinalIgnoreCase);
        return View(model);
    }

    public async Task<IActionResult> Windows(CancellationToken token)
    {
        var now = await simulation.UtcNowAsync(token);
        var windows = AchOperatingPolicy.For(now);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var time = TimeOnly.FromDateTime(now.UtcDateTime);
        var next = AchBusinessCalendar.IsBankingDay(today) ? windows.FirstOrDefault(x => x.Cutoff > time) : null;
        var nextDate = next?.SettlementDate ?? AchBusinessCalendar.NextBankingDay(today);
        var action = next is null
            ? $"All teaching cutoffs have passed or today is not a banking day. Move the item to {nextDate:yyyy-MM-dd}."
            : $"Next eligible window: {next.Name} at {next.Cutoff}. Expected settlement date: {next.SettlementDate:yyyy-MM-dd}.";
        return View(new AchWindowsViewModel(now, windows, action, nextDate));
    }

    [Authorize(Roles = BankSecurity.Operations)]
    public async Task<IActionResult> Operations(Guid? answeredId, string? decision, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, token);
        var entries = await db.AchEntries.Where(x => x.OriginatingBankId == bankId
                && (x.Scenario != AchProcessingScenario.Standard || x.Status == AchEntryStatus.Returned
                    || x.Status == AchEntryStatus.NocReceived || x.Status == AchEntryStatus.Rejected))
            .OrderByDescending(x => x.CreatedDate).Take(30).AsNoTracking().ToListAsync(token);
        var cases = entries.Select(x =>
        {
            var expected = AchLearningPolicy.DecisionFor(x);
            var selected = answeredId == x.Id ? decision : null;
            return new AchOperationsCaseViewModel(x, CaseEvidence(x), expected, selected,
                selected is null ? null : string.Equals(selected, expected.Layer, StringComparison.Ordinal));
        }).ToList();
        return View(new AchOperationsViewModel(bank, cases));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public async Task<IActionResult> Replay(Guid id, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        var exists = await db.AchEntries.AnyAsync(x => x.Id == id && x.OriginatingBankId == bankId, token);
        if (!exists) return NotFound();
        db.OutboxMessages.Add(Outbox.Message(Queues.AchEntryCreated, new AchEntryCreated(id), bankId));
        await db.SaveChangesAsync(token);
        TempData["Notice"] = "A duplicate ACH validation delivery was durably queued. The consumer must preserve the current state and must not create another batch, hold, settlement, or journal.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet, Authorize(Roles = BankSecurity.PaymentCreator)]
    public async Task<IActionResult> Eftps(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, token);
        return View(await PopulateEftpsAsync(new CreateEftpsViewModel(), bankId, db, token));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentCreator)]
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
        var entryId = await CreateEntryAsync(db, account!, bankId, model.CompanyName, model.CompanyId, AchStandardEntryClass.Ccd,
            "US TREASURY EFTPS", "091036164", "234010000", AchTransactionCode.CheckingCredit,
            model.Amount, "TAXPAYMENT", model.SettlementDate, addenda, AchPaymentPurpose.TaxPaymentEftps,
            model.CaseType, token);
        return RedirectToAction(nameof(Details), new { id = entryId });
    }

    private static IReadOnlyList<AchLifecycleStepViewModel> BuildLifecycle(AchEntry entry,
        IReadOnlyList<AchReturn> returns, IReadOnlyList<AchNotificationOfChange> nocs,
        IReadOnlyList<AchLedgerEntry> journal)
    {
        var approved = entry.ApprovedDate is not null;
        var approvalRejected = entry.Status == AchEntryStatus.Rejected
            && !string.IsNullOrWhiteSpace(entry.ApprovalDecisionReason);
        var validated = !approvalRejected && entry.Status is not (AchEntryStatus.PendingApproval or AchEntryStatus.Entered);
        var batched = entry.AchBatchId is not null;
        var file = entry.Batch?.File;
        var operatorComplete = file?.Status is "Accepted" or "Rejected" or "Settled"
            || entry.Status is AchEntryStatus.Settled or AchEntryStatus.Posted or AchEntryStatus.Returned or AchEntryStatus.NocReceived;
        var settlement = entry.Status is AchEntryStatus.Settled or AchEntryStatus.Posted
            or AchEntryStatus.Returned or AchEntryStatus.NocReceived;
        var exceptionDate = returns.Select(x => (DateTimeOffset?)x.ReceivedDate)
            .Concat(nocs.Select(x => (DateTimeOffset?)x.ReceivedDate)).OrderBy(x => x).FirstOrDefault();
        return
        [
            new("Captured", "Maker", $"Created by {entry.CreatedBy ?? "legacy workflow"}.",
                "No money moves and no hold is placed.", entry.CreatedDate, true),
            new("Authorized", "Approver", approved
                    ? $"Decision by {entry.ApprovedBy}." : "Independent approval is still required.",
                "Approval authorizes processing but does not settle the entry.", entry.ApprovedDate, approved),
            new("ODFI validation", "ACH service", validated
                    ? $"Observed status: {entry.Status}." : "Waiting for validation after approval.",
                AchRules.IsCredit(entry.TransactionCode) ? "A valid credit places an originator hold." : "No originator credit hold is required for a debit.",
                entry.Batch?.Entries.FirstOrDefault(x => x.Id == entry.Id)?.CreatedDate, validated),
            new("Batched", "ACH service", batched
                    ? $"Grouped into batch {entry.Batch!.Id.ToString()[..8]} by company, SEC code, and effective date." : "No batch assignment yet.",
                "Batching groups instructions; it does not itself move value.", entry.Batch?.File?.CreatedDate, batched),
            new("File delivered", "ODFI / ACH operator", file is null
                    ? "No NACHA file yet." : $"File {file.Id.ToString()[..8]} status: {file.Status}.",
                "Transport delivery remains distinct from operator acceptance.", file?.CreatedDate, file is not null),
            new("Operator outcome", "ACH operator", operatorComplete
                    ? $"Entry status: {entry.Status}." : "No final operator evidence yet.",
                settlement ? "Settlement became eligible only after acceptance." : "No settlement evidence is present.",
                journal.FirstOrDefault()?.CreatedDate, operatorComplete),
            new("RDFI outcome", "RDFI", returns.Count > 0 ? $"Return {returns[0].ReturnCode} received."
                    : nocs.Count > 0 ? $"NOC {nocs[0].ChangeCode} received." : "No post-settlement exception observed.",
                returns.Count > 0 ? "The return reverses value." : nocs.Count > 0
                    ? "The NOC communicates corrected data and does not reverse value." : "No later value effect.",
                exceptionDate, returns.Count > 0 || nocs.Count > 0 || settlement),
            new("Accounting", "Posting service", journal.Count == 0 ? "No value journal present."
                    : $"{journal.Select(x => x.JournalId).Distinct().Count()} journal(s); debits {journal.Sum(x => x.Debit):C}, credits {journal.Sum(x => x.Credit):C}.",
                journal.Count > 0 && journal.Sum(x => x.Debit) == journal.Sum(x => x.Credit)
                    ? "Observed journal lines balance." : "No balanced posting has been observed yet.",
                journal.LastOrDefault()?.CreatedDate, journal.Count > 0)
        ];
    }

    private static string CaseEvidence(AchEntry entry) => entry.Status switch
    {
        AchEntryStatus.PendingApproval => "The instruction exists, but no hold, batch, file, or settlement is present.",
        AchEntryStatus.Rejected when entry.AchBatchId is not null => "A file was created, then processing stopped without settlement.",
        AchEntryStatus.Returned => $"The original entry has return code {entry.ReturnCode ?? "pending"} after operator processing.",
        AchEntryStatus.NocReceived => "The RDFI supplied corrected receiving information after processing.",
        _ => $"Current status is {entry.Status}; trace number is {entry.TraceNumber ?? "not assigned"}."
    };

    private async Task<Guid> CreateEntryAsync(BankingDbContext db, Account account, Guid bankId,
        string companyName, string companyId, AchStandardEntryClass sec, string receiverName,
        string routing, string receiverAccount, AchTransactionCode code, decimal amount,
        string description, DateOnly effectiveDate, string? addenda, AchPaymentPurpose purpose,
        AchLearningCaseType caseType, CancellationToken token)
    {
        var receivingBankId = await db.Banks.Where(x => x.RoutingNumber == routing).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
        var entry = new AchEntry { OriginatingBankId = bankId, ReceivingBankId = receivingBankId,
            OriginatingAccountId = account.Id, CompanyName = companyName.Trim(), CompanyId = companyId.Trim(),
            SecCode = sec, ReceiverName = receiverName.Trim(), ReceivingRoutingNumber = routing.Trim(),
            ReceivingAccountNumber = receiverAccount.Trim(), TransactionCode = code, Amount = amount,
            EntryDescription = description.Trim(), EffectiveEntryDate = effectiveDate, Addenda05 = addenda?.Trim(),
            Purpose = purpose, Status = AchEntryStatus.PendingApproval, CreatedBy = User.Identity!.Name };
        entry.Scenario = AchLearningPolicy.SelectScenario(caseType, entry.Id);
        db.AchEntries.Add(entry);
        await db.SaveChangesAsync(token);
        await LearningRunLinker.AttachAsync(HttpContext, db, bankId, "ACH", entry.Id, token);
        TempData["Notice"] = $"ACH entry {entry.Id.ToString()[..8]} created. A different payment approver must release it; the exercise outcome remains hidden until evidence appears.";
        return entry.Id;
    }

    private static AchEntry Candidate(CreateAchViewModel model, Account account, Guid bankId) => new()
    {
        OriginatingBankId = bankId, OriginatingAccountId = account.Id, CompanyName = model.CompanyName,
        CompanyId = model.CompanyId, SecCode = model.SecCode, ReceiverName = model.ReceiverName,
        ReceivingRoutingNumber = model.ReceivingRoutingNumber, ReceivingAccountNumber = model.ReceivingAccountNumber,
        TransactionCode = model.TransactionCode, Amount = model.Amount, EntryDescription = model.EntryDescription,
        EffectiveEntryDate = model.EffectiveEntryDate, Addenda05 = model.Addenda05, Purpose = model.Purpose
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
            [nameof(CreateAchViewModel.CaseType)] = EnumChoices<AchLearningCaseType>(nameof(CreateAchViewModel.CaseType), "Exercise modes", "Blind modes choose a deterministic hidden condition. Predict the operational outcome from the resulting evidence."),
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
            [nameof(CreateEftpsViewModel.CaseType)] = EnumChoices<AchLearningCaseType>(nameof(CreateEftpsViewModel.CaseType), "Exercise modes", "Blind modes choose a deterministic hidden condition. Predict the outcome from the resulting evidence.")
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
