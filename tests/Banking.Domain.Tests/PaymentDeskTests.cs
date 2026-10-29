using System.Security.Claims;
using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Controllers;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class PaymentDeskTests
{
    [Fact]
    public async Task DeskExcludesOtherBanksAndUnsubmittedReceivingEntries()
    {
        var factory = Factory(); var mine = Bank(); var other = Bank();
        var own = Entry(mine.Id, other.Id, AchEntryStatus.Batched);
        var hidden = Entry(other.Id, mine.Id, AchEntryStatus.PendingApproval);
        var visible = Entry(other.Id, mine.Id, AchEntryStatus.SentToOperator);
        var unrelated = Entry(other.Id, Guid.NewGuid(), AchEntryStatus.Batched);
        await using (var db = factory.CreateDbContext())
        {
            db.Banks.AddRange(mine, other);
            db.SimulationStates.Add(new() { Name = "default", CurrentUtc = new(2026, 10, 28, 0, 0, 0, TimeSpan.Zero) });
            db.AchEntries.AddRange(own, hidden, visible, unrelated);
            await db.SaveChangesAsync();
        }
        var controller = new PaymentDeskController(factory, new(factory), new(factory), new(factory))
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("bank_id", mine.Id.ToString()), new Claim(ClaimTypes.Role, "Operations")], "test")) } }
        };
        var result = Assert.IsType<ViewResult>(await controller.Index(default));
        var model = Assert.IsType<PaymentDeskViewModel>(result.Model);
        Assert.Equal(2, model.Items.Count);
        Assert.Contains(model.Items, x => x.Id == own.Id);
        Assert.Contains(model.Items, x => x.Id == visible.Id);
        Assert.DoesNotContain(model.Items, x => x.Id == hidden.Id || x.Id == unrelated.Id);
        Assert.Equal(own.Amount, model.AchCreditExposure);
        var export = Assert.IsType<FileContentResult>(await controller.Evidence(default));
        var json = System.Text.Encoding.UTF8.GetString(export.FileContents);
        Assert.Contains(own.Id.ToString(), json);
        Assert.DoesNotContain(hidden.Id.ToString(), json);
        Assert.DoesNotContain(unrelated.Id.ToString(), json);
        controller.TempData = new TempDataDictionary(controller.HttpContext, new MemoryTempData());
        await controller.RecordReview("Inspected posting journals and holds; investigate unresolved delivery evidence before closing.", default);
        await using var verify = factory.CreateDbContext();
        var review = Assert.Single(verify.OperationsCases);
        Assert.Equal(mine.Id, review.BankId);
        Assert.Equal(OperationsCaseStatus.Investigating, review.Status);
        Assert.Contains("Reviewer explanation", review.Notes);
        await controller.RecordReview("too short", default);
        Assert.Single(verify.OperationsCases);
    }

    [Fact]
    public async Task ReconciliationIdentifiesMissingAchJournalsAndHoldAmountsWithoutLeakingOtherBanks()
    {
        var factory = Factory(); var mine = Bank(); var other = Bank();
        var posted = Entry(mine.Id, other.Id, AchEntryStatus.Posted);
        var unrelated = Entry(other.Id, Guid.NewGuid(), AchEntryStatus.Posted);
        var customer = new Customer { BankId = mine.Id, Name = "Own customer" };
        var account = new Account { Customer = customer, AccountNumber = "12345678", HeldBalance = 12 };
        await using (var db = factory.CreateDbContext())
        {
            db.Banks.AddRange(mine, other); db.Accounts.Add(account);
            db.AchEntries.AddRange(posted, unrelated);
            await db.SaveChangesAsync();
        }
        var result = await new PaymentReconciliationService(factory).ReconcileAsync(mine.Id, default);
        Assert.False(result.IsClear);
        Assert.Equal(1, result.SettledAchWithoutJournals);
        Assert.Equal(1, result.HoldMismatches);
        Assert.Contains(result.Findings, x => x.ItemId == posted.Id && x.Expected == 1 && x.Observed == 0);
        Assert.Contains(result.Findings, x => x.ItemId == account.Id && x.Expected == 0 && x.Observed == 12 && x.Reference == "Account •••• 5678");
        Assert.DoesNotContain(result.Findings, x => x.ItemId == unrelated.Id);
    }

    [Fact]
    public void DeskMutationsRequireOperationsAndAntiforgery()
    {
        Assert.Equal("Operations", Assert.Single(typeof(PaymentDeskController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>()).Roles);
        foreach (var name in new[] { nameof(PaymentDeskController.NextEvent), nameof(PaymentDeskController.RecordReview) })
        {
            var action = typeof(PaymentDeskController).GetMethod(name)!;
            Assert.NotEmpty(action.GetCustomAttributes(typeof(HttpPostAttribute), true));
            Assert.NotEmpty(action.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        }
    }

    private sealed class MemoryTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static PooledDbContextFactory<BankingDbContext> Factory() => new(new DbContextOptionsBuilder<BankingDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static Bank Bank() => new() { Name = "Demo", RoutingNumber = "101000019", FedParticipantId = "demo", Bic = "DEMOUS33", TownName = "Tulsa", CountryCode = "US" };
    private static AchEntry Entry(Guid origin, Guid receiver, AchEntryStatus status) => new()
    {
        OriginatingBankId = origin, ReceivingBankId = receiver, Status = status,
        Amount = 25, TransactionCode = AchTransactionCode.CheckingCredit, EffectiveEntryDate = new(2026, 11, 2)
    };
}
