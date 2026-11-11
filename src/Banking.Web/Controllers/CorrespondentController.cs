using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace Banking.Web.Controllers;

public sealed record CorrespondentDestination(Guid Id, string AccountNumber, string BankName);
public sealed record CorrespondentPortal(Guid BankId, List<CorrespondentAccount> Accounts,
    List<CorrespondentServiceAgreement> Agreements, List<CorrespondentBookOrder> Orders,
    List<CorrespondentFundingRule> Rules, List<CorrespondentFundingRun> Runs, List<CorrespondentAuthorizationEvent> Audit, List<CorrespondentDestination> Destinations,
    List<CorrespondentAccountPosition> Positions);

[Authorize]
public sealed class CorrespondentController(IDbContextFactory<BankingDbContext> factory, CorrespondentPlatformService service) : Controller
{
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var bank = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        var accounts = await db.CorrespondentAccounts.AsNoTracking().Where(x => x.IssuerBankId == bank || x.RespondentBankId == bank).ToListAsync(ct);
        var ids = accounts.Select(x => x.Id).ToList();
        var rules = await db.CorrespondentFundingRules.AsNoTracking().Where(x => ids.Contains(x.SourceAccountId) || ids.Contains(x.DestinationAccountId)).ToListAsync(ct);
        var ruleIds = rules.Select(x => x.Id).ToList();
        return View(new CorrespondentPortal(bank, accounts,
            await db.CorrespondentServiceAgreements.AsNoTracking().Where(x => ids.Contains(x.AccountId)).ToListAsync(ct),
            await db.CorrespondentBookOrders.AsNoTracking().Where(x => ids.Contains(x.SourceAccountId) || ids.Contains(x.DestinationAccountId)).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct), rules,
            await db.CorrespondentFundingRuns.AsNoTracking().Where(x => ruleIds.Contains(x.RuleId)).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct),
            await db.CorrespondentAuthorizationEvents.AsNoTracking().Where(x => ids.Contains(x.AccountId)).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct),
            await (from account in db.CorrespondentAccounts.AsNoTracking()
                   join respondent in db.Banks on account.RespondentBankId equals respondent.Id
                   where db.CorrespondentAccounts.Any(owned => owned.RespondentBankId == bank && owned.IssuerBankId == account.IssuerBankId)
                   select new CorrespondentDestination(account.Id, account.AccountNumber, respondent.Name)).ToListAsync(ct),
            await service.PositionsAsync(bank, ct)));
    }
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentCreator)]
    public Task<IActionResult> Agreement(Guid accountId, decimal limit, CancellationToken ct) =>
        Execute(async bank => { await service.ProposeAgreementAsync(bank, accountId, limit, User.Identity!.Name!, ct); }, ct);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentApprover)]
    public Task<IActionResult> ApproveAgreement(Guid id, bool suspend, CancellationToken ct) =>
        Execute(bank => service.ApproveAgreementAsync(bank, id, User.Identity!.Name!, suspend, ct), ct);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentCreator)]
    public Task<IActionResult> Book(Guid sourceAccountId, Guid destinationAccountId, decimal amount, CancellationToken ct) =>
        Execute(async bank => { await service.RequestBookAsync(bank, sourceAccountId, destinationAccountId, amount, User.Identity!.Name!, ct); }, ct);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentApprover)]
    public Task<IActionResult> Decide(Guid id, bool approve, CancellationToken ct) =>
        Execute(bank => service.DecideBookAsync(bank, id, User.Identity!.Name!, approve, ct), ct);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentCreator)]
    public Task<IActionResult> Funding(Guid sourceAccountId, Guid destinationAccountId, CorrespondentFundingStrategy strategy,
        decimal threshold, decimal target, decimal maximum, DateTimeOffset nextRunAt, CancellationToken ct) =>
        Execute(async bank => { await service.ProposeFundingAsync(bank, new CorrespondentFundingRule { SourceAccountId = sourceAccountId,
            DestinationAccountId = destinationAccountId, Strategy = strategy, Threshold = threshold, Target = target,
            Maximum = maximum, NextRunAt = nextRunAt }, User.Identity!.Name!, ct); }, ct);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentCreator)]
    public Task<IActionResult> PreviewFunding(Guid sourceAccountId, Guid destinationAccountId,
        CorrespondentFundingStrategy strategy, decimal threshold, decimal target, decimal maximum, CancellationToken ct) =>
        Execute(async bank => {
            var amount = await service.PreviewFundingAsync(bank, sourceAccountId, destinationAccountId,
                strategy, threshold, target, maximum, ct);
            TempData["FundingPreview"] = $"Calculated funding: {amount:C} USD. Includes current holds and the source liquidity cap. Preview only; no funds reserved or transferred. Scheduled execution rechecks balances and bilateral authority.";
        }, ct);
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = BankSecurity.PaymentApprover)]
    public Task<IActionResult> ApproveFunding(Guid id, bool disable, CancellationToken ct) =>
        Execute(bank => service.ApproveFundingAsync(bank, id, User.Identity!.Name!, disable, ct), ct);
    private async Task<IActionResult> Execute(Func<Guid, Task> action, CancellationToken ct)
    {
        if (!ModelState.IsValid) { TempData["Error"] = "Enter valid form values."; return RedirectToAction(nameof(Index)); }
        await using var db = await factory.CreateDbContextAsync(ct);
        var bank = await ActiveBank.ResolveAsync(HttpContext, db, ct);
        try { await action(bank); if (!TempData.ContainsKey("FundingPreview")) TempData["Notice"] = "Instruction recorded."; }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index));
    }
}
