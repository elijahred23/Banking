using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Banking.Web.Controllers;

[Authorize]
public sealed class FedFundsController(IDbContextFactory<BankingDbContext> factory, FedFundsTradeService trades,
    FedFundsReadService reports, IOptions<FedFundsOptions> options) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        var bank = await db.Banks.AsNoTracking().SingleAsync(x => x.Id == bankId, ct);
        var now = await Clock(db, ct);
        var lines = await db.FedFundsCreditLines.AsNoTracking().Where(x => x.LenderBankId == bankId).ToListAsync(ct);
        return View(new FedFundsDashboardViewModel(bank, DateOnly.FromDateTime(now.UtcDateTime), options.Value.ReferenceRate,
            await reports.PositionsAsync(bankId, ct), lines, await Names(db, ct), await reports.LiquidityAsync(bankId, ct)));
    }
    public async Task<IActionResult> TradeDesk(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        var now = await Clock(db, ct);
        return View(new FedFundsTradeDeskViewModel(bankId, now, options.Value.ReferenceRate,
            await db.FedFundsQuotes.AsNoTracking().Where(x => x.ExpiresAt > now).OrderBy(x => x.Rate).ToListAsync(ct), await Names(db, ct)));
    }
    [HttpGet, Authorize(Roles = BankSecurity.PaymentCreator)]
    public async Task<IActionResult> Create(Guid? quoteId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        var now = await Clock(db, ct);
        var model = new CreateFedFundsViewModel { ValueDate = DateOnly.FromDateTime(now.UtcDateTime) };
        while (!FedFundsPolicy.IsBusinessDay(model.ValueDate, options.Value.Holidays)) model.ValueDate = model.ValueDate.AddDays(1);
        if (quoteId is not null)
        {
            var quote = await db.FedFundsQuotes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == quoteId && x.BankId != bankId && x.ExpiresAt > now, ct);
            if (quote is null) return NotFound();
            model.CounterpartyBankId = quote.BankId;
            model.Borrow = quote.Side == FedFundsQuoteSide.Lend;
            model.AnnualRate = quote.Rate;
            model.Principal = quote.MinAmount;
        }
        return View(await Populate(model, bankId, db, ct));
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentCreator)]
    public async Task<IActionResult> Create(CreateFedFundsViewModel model, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        if (ModelState.IsValid)
        {
            try
            {
                var id = await trades.ProposeTradeAsync(Actor(bankId), new(model.CounterpartyBankId, model.Borrow,
                    model.Principal, model.AnnualRate!.Value, model.ValueDate), ct);
                return RedirectToAction(nameof(Details), new { id });
            }
            catch (InvalidOperationException ex) { ModelState.AddModelError("", ex.Message); }
        }
        return View(await Populate(model, bankId, db, ct));
    }
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        var trade = await db.FedFundsTrades.AsNoTracking().Where(x => x.Id == id && (x.LenderBankId == bankId || x.BorrowerBankId == bankId))
            .Include(x => x.Approvals).Include(x => x.Accruals).Include(x => x.Obligations).Include(x => x.Events).AsSplitQuery().SingleOrDefaultAsync(ct);
        if (trade is null) return NotFound();
        var ids = trade.Obligations.Select(x => x.CorrelationId).ToList();
        var ownWires = await db.WireTransfers.AsNoTracking().Where(x => x.BankId == bankId && ids.Contains(x.CorrelationId))
            .ToDictionaryAsync(x => x.CorrelationId, x => x.Id, ct);
        var journalIds = trade.Obligations.Select(x => bankId == trade.LenderBankId ? x.LenderJournalId : x.BorrowerJournalId)
            .Where(x => x != null).Select(x => x!.Value).Concat(trade.Accruals.Select(x => bankId == trade.LenderBankId ? x.LenderJournalId : x.BorrowerJournalId)).ToList();
        var journals = await db.LedgerEntries.AsNoTracking().Where(x => x.WireTransfer.BankId == bankId && journalIds.Contains(x.JournalId))
            .OrderBy(x => x.CreatedDate).ToListAsync(ct);
        return View(new FedFundsDetailsViewModel(bankId, trade, await Names(db, ct), journals, ownWires));
    }
    [Authorize(Roles = BankSecurity.Operations)]
    public async Task<IActionResult> Reconciliation(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        return View(await reports.ReconcileAsync(bankId, ct));
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentCreator)]
    public Task<IActionResult> Accept(Guid id, bool accept, CancellationToken ct) => Change(id, (a) => trades.AcceptTradeAsync(a, id, accept, ct), ct);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentApprover)]
    public Task<IActionResult> Approve(Guid id, bool approve, CancellationToken ct) => Change(id, (a) => trades.ApproveTradeAsync(a, id, approve, ct), ct);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public Task<IActionResult> Fund(Guid id, CancellationToken ct) => Change(id, (a) => trades.RequestFundingAsync(a, id, ct), ct);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public Task<IActionResult> Retry(Guid id, CancellationToken ct) => Change(id, (a) => trades.RetryRepaymentAsync(a, id, ct), ct);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentApprover)]
    public Task<IActionResult> Cancel(Guid id, CancellationToken ct) => Change(id, (a) => trades.CancelAsync(a, id, ct), ct);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentApprover)]
    public async Task<IActionResult> CreditLine(FedFundsCreditViewModel model, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        if (!ModelState.IsValid) { TempData["Error"] = "Enter a valid credit limit."; return RedirectToAction(nameof(Index)); }
        try { await trades.SetCreditLineAsync(Actor(bankId), model.BorrowerBankId, model.ApprovedLimit, model.IsActive, ct); }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentCreator)]
    public async Task<IActionResult> Quote(FedFundsQuoteViewModel model, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        if (!ModelState.IsValid) { TempData["Error"] = "Enter valid quote amounts and rate."; return RedirectToAction(nameof(TradeDesk)); }
        try { await trades.PublishQuoteAsync(Actor(bankId), model.Side, model.MinAmount, model.MaxAmount, model.Rate, ct); }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(TradeDesk));
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.Operations)]
    public async Task<IActionResult> Process(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        await trades.ProcessBankAsync(bankId, ct);
        TempData["Notice"] = "This bank's Treasury agreements processed using the shared simulation clock.";
        return RedirectToAction(nameof(Index));
    }
    private async Task<IActionResult> Change(Guid id, Func<FedFundsActor, Task> action, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bankId = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        if (!await db.FedFundsTrades.AnyAsync(x => x.Id == id && (x.LenderBankId == bankId || x.BorrowerBankId == bankId), ct)) return NotFound();
        try { await action(Actor(bankId)); TempData["Notice"] = "Treasury decision recorded."; }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
        catch (DbUpdateConcurrencyException) { TempData["Error"] = "Trade changed during review. Refresh and review its current state."; }
        return RedirectToAction(nameof(Details), new { id });
    }
    private FedFundsActor Actor(Guid bankId) => new(bankId, User.Identity!.Name!);
    private static Task<DateTimeOffset> Clock(BankingDbContext db, CancellationToken ct) =>
        db.SimulationStates.Where(x => x.Name == "default").Select(x => x.CurrentUtc).SingleAsync(ct);
    private static Task<Dictionary<Guid, string>> Names(BankingDbContext db, CancellationToken ct) =>
        db.Banks.AsNoTracking().Where(x => x.CountryCode == "US").ToDictionaryAsync(x => x.Id, x => x.Name, ct);
    private static async Task<CreateFedFundsViewModel> Populate(CreateFedFundsViewModel model, Guid bankId, BankingDbContext db, CancellationToken ct)
    {
        model.Banks = await db.Banks.Where(x => x.Id != bankId && x.CountryCode == "US").OrderBy(x => x.Name)
            .Select(x => new SelectListItem(x.Name, x.Id.ToString())).ToListAsync(ct);
        return model;
    }
}
