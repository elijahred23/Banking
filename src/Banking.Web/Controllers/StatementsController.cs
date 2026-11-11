using System.Text;
using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Controllers;

[Authorize]
public sealed class StatementsController(IDbContextFactory<BankingDbContext> factory, CorrespondentPostingService postings,
    StatementGenerationService generation, StatementScheduleService schedules, StatementReconciliationService reconciliation,
    StatementExportService exports) : Controller
{
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        var accounts = await db.CorrespondentAccounts.AsNoTracking().Where(x => x.IssuerBankId == bankId || x.RespondentBankId == bankId).ToListAsync(ct);
        var statements = await Visible(db, bankId).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.SequenceNumber).Take(100)
            .Select(x => new AccountStatement { Id = x.Id, AccountId = x.AccountId, IssuerBankId = x.IssuerBankId,
                RecipientBankId = x.RecipientBankId, StatementNumber = x.StatementNumber, Kind = x.Kind,
                PeriodStart = x.PeriodStart, PeriodEnd = x.PeriodEnd, Version = x.Version, CreatedAt = x.CreatedAt,
                OpeningBalance = x.OpeningBalance, ClosingBalance = x.ClosingBalance, GenerationDifference = x.GenerationDifference }).ToListAsync(ct);
        var ids = statements.Select(x => x.Id).ToList();
        return View(new StatementsIndexViewModel(bankId, await StatementTransactionsClock(db, ct), accounts, statements,
            await Names(db, ct), await db.StatementDeliveries.AsNoTracking().Where(x => ids.Contains(x.StatementId)).ToListAsync(ct),
            await db.StatementReconciliations.AsNoTracking().Where(x => ids.Contains(x.StatementId)).OrderByDescending(x => x.CreatedAt).ToListAsync(ct)));
    }
    public async Task<IActionResult> Account(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        var account = await db.CorrespondentAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && (x.IssuerBankId == bankId || x.RespondentBankId == bankId), ct);
        if (account is null) return NotFound();
        var payments = await db.CorrespondentPaymentLinks.AsNoTracking().Where(x => x.AccountId == id).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
        var ledger = await db.CorrespondentLedgerEntries.AsNoTracking().Where(x => x.AccountId == id && x.BankId == bankId)
            .OrderByDescending(x => x.BookingAt).Take(200).ToListAsync(ct);
        var incoming = bankId == account.IssuerBankId ? await db.WireTransfers.AsNoTracking().Where(x => x.BankId == bankId
            && x.Direction == WireDirection.Incoming && x.Rail == PaymentRail.Fedwire && x.TransferType == WireTransferType.FinancialInstitutionCreditTransfer
            && x.Status == WireStatus.Completed && !db.CorrespondentPaymentLinks.Any(p => p.WireTransferId == x.Id)).OrderByDescending(x => x.CreatedDate).Take(100).ToListAsync(ct) : [];
        return View(new StatementAccountViewModel(bankId, await StatementTransactionsClock(db, ct), account, await Names(db, ct), payments, ledger, incoming,
            await db.StatementSchedules.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == id, ct)));
    }
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        var statement = await Visible(db, bankId).Include(x => x.Entries).AsSplitQuery().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (statement is null) return NotFound();
        var wires = statement.Entries.Where(x => x.WireTransferId != null).Select(x => x.WireTransferId!.Value).ToList();
        var owned = (await db.WireTransfers.Where(x => wires.Contains(x.Id) && x.BankId == bankId).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        return View(new StatementDetailsViewModel(bankId, statement,
            await db.StatementDeliveries.AsNoTracking().SingleOrDefaultAsync(x => x.StatementId == id, ct),
            await db.StatementReconciliations.AsNoTracking().Where(x => x.StatementId == id).OrderByDescending(x => x.CreatedAt).Take(20).ToListAsync(ct), owned));
    }
    [HttpGet]
    public async Task<IActionResult> Download(Guid id, string format, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        var statement = await Visible(db, bankId).Include(x => x.Entries).AsSplitQuery().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (statement is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return format.ToLowerInvariant() switch {
            "xml" => File(Encoding.UTF8.GetBytes(statement.XmlPayload), "application/xml", statement.StatementNumber + ".xml"),
            "csv" => File(exports.Csv(statement), "text/csv; charset=utf-8", statement.StatementNumber + ".csv"),
            "pdf" => File(exports.Pdf(statement), "application/pdf", statement.StatementNumber + ".pdf"),
            _ => BadRequest("Choose XML, CSV, or PDF.") };
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentApprover)]
    public async Task<IActionResult> Open(OpenCorrespondentForm form, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        if (!ModelState.IsValid) { TempData["Error"] = "Enter a valid account number and opening position."; return RedirectToAction(nameof(Index)); }
        try { var id = await postings.OpenAsync(bankId, form.RespondentBankId, form.AccountNumber, form.OpeningBalance, ct); return RedirectToAction(nameof(Account), new { id }); }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; return RedirectToAction(nameof(Index)); }
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public async Task<IActionResult> Generate(GenerateStatementForm form, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        if (!ModelState.IsValid) return BadRequest("Choose a valid reporting period.");
        try { var id = await generation.GenerateAsync(bankId, form.AccountId, form.Kind, form.PeriodStart, form.PeriodEnd, form.Corrects, ct); return RedirectToAction(nameof(Details), new { id }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; return RedirectToAction(nameof(Account), new { id = form.AccountId }); }
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public async Task<IActionResult> SaveSchedule(StatementScheduleForm form, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        if (!ModelState.IsValid) { TempData["Error"] = "Enter a valid schedule."; return RedirectToAction(nameof(Account), new { id = form.AccountId }); }
        try
        {
            var holidays = (form.Holidays ?? "").Split([',', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(x => DateOnly.ParseExact(x, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            await schedules.SaveAsync(bankId, form.AccountId, form.TimeZone, form.LocalTime, holidays, form.Enabled, ct);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        { TempData["Error"] = "Invalid schedule: " + ex.Message; }
        return RedirectToAction(nameof(Account), new { id = form.AccountId });
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentCreator)]
    public async Task<IActionResult> RequestWire(Guid accountId, Guid receiverBankId, decimal amount, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        try { await postings.RequestWireAsync(bankId, accountId, receiverBankId, amount, User.Identity!.Name!, ct); TempData["Notice"] = "Transfer requested. The issuer must approve the outgoing wire; the correspondent amount is held until posting or rejection."; }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Account), new { id = accountId });
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public async Task<IActionResult> AssignIncoming(Guid accountId, Guid wireId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        try { await postings.AssignIncomingAsync(bankId, accountId, wireId, ct); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Account), new { id = accountId });
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentApprover)]
    public async Task<IActionResult> Fee(Guid accountId, decimal amount, string reference, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        try { await postings.AssessFeeAsync(bankId, accountId, amount, reference, ct); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Account), new { id = accountId });
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public async Task<IActionResult> Reconcile(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        try { await reconciliation.ReconcileAsync(bankId, id, ct); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Details), new { id });
    }
    private static IQueryable<AccountStatement> Visible(BankingDbContext db, Guid bankId) => db.AccountStatements.AsNoTracking()
        .Where(x => x.IssuerBankId == bankId || x.RecipientBankId == bankId && db.StatementDeliveries.Any(d => d.StatementId == x.Id && d.Status == StatementDeliveryStatus.Delivered));
    private static Task<DateTimeOffset> StatementTransactionsClock(BankingDbContext db, CancellationToken ct) =>
        db.SimulationStates.Where(x => x.Name == "default").Select(x => x.CurrentUtc).SingleAsync(ct);
    private static Task<Dictionary<Guid, string>> Names(BankingDbContext db, CancellationToken ct) => db.Banks.AsNoTracking().Where(x => x.CountryCode == "US").ToDictionaryAsync(x => x.Id, x => x.Name, ct);
}
