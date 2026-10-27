using System.Security.Claims;
using System.Text;
using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Controllers;
using Banking.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class NachaInspectionTests
{
    [Fact]
    public async Task Inspection_is_bank_scoped_read_only_and_follows_draft_to_exact_saved_file()
    {
        var factory = new Factory(new DbContextOptionsBuilder<BankingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var bank = new Bank { Name = "Origin bank", RoutingNumber = "101000019", FedParticipantId = "LAB",
            Bic = "LABBUS33XXX", TownName = "Tulsa", CountryCode = "US" };
        var other = new Bank { Name = "Other bank", RoutingNumber = "103000648", FedParticipantId = "OTHER",
            Bic = "OTHRUS33XXX", TownName = "Tulsa", CountryCode = "US" };
        var batch = new AchBatch { OriginatingBankId = bank.Id, CompanyName = "LAB PAYROLL", CompanyId = "1234567890",
            EffectiveEntryDate = new DateOnly(2026, 7, 6), SecCode = AchStandardEntryClass.Ppd,
            Entries = [new AchEntry { OriginatingBankId = bank.Id, ReceiverName = "Employee",
                ReceivingRoutingNumber = other.RoutingNumber, ReceivingAccountNumber = "654321", Amount = 123.45m,
                TransactionCode = AchTransactionCode.CheckingCredit, Status = AchEntryStatus.Batched }] };
        await using (var db = factory.CreateDbContext())
        {
            db.Banks.AddRange(bank, other); db.AchBatches.Add(batch); await db.SaveChangesAsync();
        }
        var controller = Controller(factory, bank.Id);
        var view = Assert.IsType<ViewResult>(await controller.Explorer(null, default, batch.Id));
        var draft = Assert.IsType<AchNachaSandboxViewModel>(view.Model);
        Assert.Equal(batch.Id, draft.DraftBatchId);
        Assert.Null(draft.FileId);
        Assert.True(new NachaFileParser().Validate(draft.Payload).IsValid);
        var draftDownload = Assert.IsType<FileContentResult>(await controller.DownloadNacha(null, batch.Id, default));
        Assert.StartsWith("NACHA-DRAFT-", draftDownload.FileDownloadName);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Empty(await db.AchFiles.ToListAsync());
            Assert.Null((await db.AchEntries.SingleAsync()).TraceNumber);
            Assert.Equal(AchEntryStatus.Batched, (await db.AchEntries.SingleAsync()).Status);
        }
        var denied = Controller(factory, other.Id);
        Assert.IsType<NotFoundResult>(await denied.Explorer(null, default, batch.Id));
        Assert.IsType<NotFoundResult>(await denied.DownloadNacha(null, batch.Id, default));
        var file = new AchFile { OriginatingBankId = bank.Id, RawNachaPayload = draft.Payload, Status = "Ready" };
        await using (var db = factory.CreateDbContext())
        {
            db.AchFiles.Add(file);
            (await db.AchBatches.SingleAsync()).AchFileId = file.Id;
            await db.SaveChangesAsync();
        }
        var savedView = Assert.IsType<ViewResult>(await controller.Explorer(null, default, batch.Id));
        var saved = Assert.IsType<AchNachaSandboxViewModel>(savedView.Model);
        Assert.Null(saved.DraftBatchId);
        Assert.Equal(file.Id, saved.FileId);
        var download = Assert.IsType<FileContentResult>(await controller.DownloadNacha(null, batch.Id, default));
        Assert.Equal($"NACHA-{file.Id:N}.ach", download.FileDownloadName);
        Assert.Equal(Encoding.UTF8.GetBytes(file.RawNachaPayload), download.FileContents);
        Assert.IsType<NotFoundResult>(await denied.DownloadNacha(file.Id, null, default));
    }

    private static AchController Controller(Factory factory, Guid bankId)
    {
        var controller = new AchController(factory, new SimulationStateService(factory), new NachaFileParser());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("bank_id", bankId.ToString())], "lab")) } };
        return controller;
    }
    private sealed class Factory(DbContextOptions<BankingDbContext> options) : IDbContextFactory<BankingDbContext>
    { public BankingDbContext CreateDbContext() => new(options); }
}
