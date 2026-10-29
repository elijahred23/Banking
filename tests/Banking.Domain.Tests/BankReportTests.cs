using System.Security.Claims;
using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Controllers;
using Banking.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class BankReportTests
{
    [Fact]
    public void DailyScheduleUsesUtcAndStrictlyFutureTime()
    {
        var now = new DateTimeOffset(2026, 10, 28, 1, 0, 0, TimeSpan.FromHours(-5));
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.Zero), ReportSchedulePolicy.Next(ReportFrequency.Daily, new(6, 0), now));
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 7, 30, 0, TimeSpan.Zero), ReportSchedulePolicy.Next(ReportFrequency.Daily, new(7, 30), now));
        Assert.Equal(now.ToUniversalTime().AddMinutes(15), ReportSchedulePolicy.Next(ReportFrequency.Every15Minutes, new(6, 0), now));
        Assert.Equal(now.ToUniversalTime().AddHours(1), ReportSchedulePolicy.Next(ReportFrequency.Hourly, new(6, 0), now));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReportSchedulePolicy.Next((ReportFrequency)99, new(6, 0), now));
    }

    [Theory]
    [InlineData(BankReportKind.PaymentActivity)]
    [InlineData(BankReportKind.Reconciliation)]
    [InlineData(BankReportKind.Liquidity)]
    [InlineData(BankReportKind.Exceptions)]
    public async Task ReportBuilderScopesObservationsToBankAndPreservesBalances(BankReportKind kind)
    {
        var factory = Factory(); var own = Bank("Own bank"); var other = Bank("Other bank");
        var account = new Account { AccountNumber = "123456", Balance = 100, HeldBalance = 5,
            Customer = new Customer { BankId = own.Id, Name = "Own person" } };
        var foreign = new Account { AccountNumber = "654321", Balance = 999,
            Customer = new Customer { BankId = other.Id, Name = "Hidden person" } };
        var now = DateTimeOffset.UtcNow;
        var visible = Wire(own.Id, "Visible sender", now.AddHours(-1));
        var hidden = Wire(other.Id, "Hidden sender", now.AddHours(-1));
        var old = Wire(own.Id, "Outside window", now.AddHours(-30));
        var unreleased = new AchEntry { OriginatingBankId = other.Id, ReceivingBankId = own.Id,
            Status = AchEntryStatus.PendingApproval, CompanyName = "Unreleased foreign", CreatedDate = now.AddHours(-1) };
        await using (var db = factory.CreateDbContext())
        {
            db.Banks.AddRange(own, other); db.Accounts.AddRange(account, foreign);
            db.WireTransfers.AddRange(visible, hidden, old); db.AchEntries.Add(unreleased);
            db.SimulationStates.Add(new() { Name = "default" });
            await db.SaveChangesAsync();
        }
        var builder = new BankReportBuilder(factory, new(factory));
        var report = await builder.BuildAsync(new BankReportRun { BankId = own.Id, TaskName = "Test", RequestedBy = "Test", Kind = kind, RequestedAt = now, LookbackHours = 24 }, default);
        Assert.Equal(own.Id, report.BankId);
        var json = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.DoesNotContain("Hidden person", json); Assert.DoesNotContain("Hidden sender", json);
        Assert.DoesNotContain("Unreleased foreign", json); Assert.DoesNotContain("Outside window", json);
        if (kind is BankReportKind.PaymentActivity or BankReportKind.Exceptions) Assert.Contains("Visible sender", json);
        if (kind == BankReportKind.Liquidity) Assert.Contains("Own person", json);
        await using var verify = factory.CreateDbContext();
        Assert.Equal(100, (await verify.Accounts.SingleAsync(x => x.Id == account.Id)).Balance);
        Assert.Equal(5, (await verify.Accounts.SingleAsync(x => x.Id == account.Id)).HeldBalance);
    }

    [Fact]
    public async Task ReportRoutesHideOtherBankRunsAndFilterLibrary()
    {
        var factory = Factory(); var own = Bank("Own"); var other = Bank("Other");
        var now = DateTimeOffset.UtcNow;
        var mine = new BankReportRun { BankId = own.Id, TaskName = "My report", RequestedBy = "Ops", RequestedAt = now, Kind = BankReportKind.Liquidity };
        var theirs = new BankReportRun { BankId = other.Id, TaskName = "Foreign report", RequestedBy = "Ops", RequestedAt = now, Kind = BankReportKind.Liquidity };
        await using (var db = factory.CreateDbContext()) { db.Banks.AddRange(own, other); db.BankReportRuns.AddRange(mine, theirs); await db.SaveChangesAsync(); }
        var service = new BankReportJobService(factory, new(factory, new(factory)), NullLogger<BankReportJobService>.Instance);
        var controller = new ReportsController(factory, service) { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("bank_id", own.Id.ToString())], "test")) } } };
        Assert.IsType<NotFoundResult>(await controller.Details(theirs.Id, default));
        Assert.IsType<NotFoundResult>(await controller.Download(theirs.Id, default));
        Assert.IsType<NotFoundResult>(await controller.Status(theirs.Id, default));
        var view = Assert.IsType<ViewResult>(await controller.Index(BankReportKind.Liquidity, null, null, 1, default));
        Assert.Equal(mine.Id, Assert.Single(Assert.IsType<ReportLibraryViewModel>(view.Model).Runs).Id);
        Assert.IsType<BadRequestObjectResult>(await controller.Index(null, new(2026, 10, 29), new(2026, 10, 28), 1, default));
    }

    [Fact]
    public void ReportRoutesRequireOperationsAndMutationsRequireAntiforgery()
    {
        Assert.Equal("Operations", Assert.Single(typeof(ReportsController).GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>()).Roles);
        foreach (var action in new[] { "Save", "RunNow" })
        {
            var method = typeof(ReportsController).GetMethod(action)!;
            Assert.NotEmpty(method.GetCustomAttributes(typeof(HttpPostAttribute), true));
            Assert.NotEmpty(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
        }
    }

    [Fact]
    public void ModelHasDurableSchedulerAndOneActiveRunConstraint()
    {
        using var db = new BankingDbContext(new DbContextOptionsBuilder<BankingDbContext>().UseSqlServer("Server=unused;Database=unused").Options);
        var sql = db.Database.GenerateCreateScript();
        Assert.Contains("CREATE TABLE [BankReportTasks]", sql); Assert.Contains("CREATE TABLE [BankReportRuns]", sql);
        Assert.Contains("CREATE UNIQUE INDEX [IX_BankReportRuns_TaskId]", sql); Assert.Contains("WHERE [CompletedAt] IS NULL", sql);
        Assert.True(db.Model.FindEntityType(typeof(BankReportRun))!.FindProperty("LeaseToken")!.IsConcurrencyToken);
    }
    private static PooledDbContextFactory<BankingDbContext> Factory() => new(new DbContextOptionsBuilder<BankingDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static Bank Bank(string name) => new() { Name = name, RoutingNumber = Guid.NewGuid().ToString("N")[..9], Bic = Guid.NewGuid().ToString("N")[..11], FedParticipantId = "TEST", TownName = "Tulsa", CountryCode = "US" };
    private static WireTransfer Wire(Guid bankId, string sender, DateTimeOffset at) => new() { BankId = bankId, SenderName = sender, ReceiverName = "Receiver", BeneficiaryAccountNumber = "123", Status = WireStatus.Rejected, CreatedDate = at, Amount = 25 };
}
