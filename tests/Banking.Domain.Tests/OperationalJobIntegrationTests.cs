using System.Text.Json;
using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class OperationalJobIntegrationTests
{
    [Fact, Trait("Category", "ExternalIntegration")]
    public async Task AllJobsAreScopedRepeatableAndPreserveApprovalAndAccountingBoundaries()
    {
        if (Environment.GetEnvironmentVariable("BANKING_RUN_JOB_INTEGRATION") != "1") return;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:DefaultConnection"] = $"Server=localhost,11433;Database=BankingJobTest_{Guid.NewGuid():N};User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True" }).Build();
        await using var services = new ServiceCollection().AddLogging().AddBankingInfrastructure(configuration).BuildServiceProvider();
        var factory = services.GetRequiredService<IDbContextFactory<BankingDbContext>>();
        try
        {
            await services.InitializeDatabaseAsync();
            var own = Bank("Own", "101000019", "OWNBUS33XXX"); var other = Bank("Other", "103000648", "OTHRUS33XXX");
            var ownAccount = new Account { AccountNumber = "123456", Balance = 1000, HeldBalance = 10, Customer = new Customer { BankId = own.Id, Name = "Own person" } };
            var otherAccount = new Account { AccountNumber = "654321", Balance = 999, Customer = new Customer { BankId = other.Id, Name = "Other person" } };
            var now = DateTimeOffset.UtcNow; var date = new DateOnly(2026, 10, 28);
            var awaiting = Wire(own.Id, other.Id, WireStatus.PendingApproval, 25, now);
            var stuck = Wire(own.Id, other.Id, WireStatus.SentToFed, 30, now);
            var final = Wire(own.Id, other.Id, WireStatus.Settled, 55, now);
            var duplicate1 = Wire(own.Id, other.Id, WireStatus.PendingApproval, 42, now);
            var duplicate2 = Wire(own.Id, other.Id, WireStatus.PendingApproval, 42, now);
            var foreignWire = Wire(other.Id, own.Id, WireStatus.PendingApproval, 88, now);
            var released1 = Entry(own.Id, other.Id, ownAccount.Id, 5, date, now);
            var released2 = Entry(own.Id, other.Id, ownAccount.Id, 7, date, now);
            var corruptAccount = Entry(own.Id, other.Id, otherAccount.Id, 13, date, now);
            var invalidRelease = Entry(own.Id, other.Id, ownAccount.Id, 9, date, now); invalidRelease.ApprovedBy = null; invalidRelease.ApprovedDate = null;
            var pending = Entry(own.Id, other.Id, ownAccount.Id, 11, date, now); pending.Status = AchEntryStatus.PendingApproval; pending.ApprovedBy = null; pending.ApprovedDate = null;
            var future = Entry(own.Id, other.Id, ownAccount.Id, 10, date.AddDays(5), now); future.Status = AchEntryStatus.Batched;
            var futureBatch = new AchBatch { OriginatingBankId = own.Id, CompanyId = "COMP", CompanyName = "Company", SecCode = AchStandardEntryClass.Ppd, EffectiveEntryDate = future.EffectiveEntryDate, Entries = [future] };
            var returned = Entry(own.Id, other.Id, ownAccount.Id, 3, date, now); returned.Status = AchEntryStatus.Returned;
            var overdue = new OperationsCase { BankId = own.Id, Rail = "ACH", CaseType = "Manual review", Reference = "keep-note", Summary = "Existing case", Notes = "Keep this evidence", DueDate = now.AddHours(-1), Severity = OperationsCaseSeverity.Low };
            var retry = new OutboxMessage { BankId = own.Id, Queue = Queues.WireCreated, Payload = "{}", Attempts = 2, NextAttemptDate = now.AddMinutes(-1), LastError = "Original error" };
            var futureRetry = new OutboxMessage { BankId = own.Id, Queue = Queues.WireCreated, Payload = "{}", Attempts = 1, NextAttemptDate = now.AddHours(1) };
            var foreignRetry = new OutboxMessage { BankId = other.Id, Queue = Queues.WireCreated, Payload = "{}", Attempts = 2, NextAttemptDate = now.AddMinutes(-1) };
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Banks.AddRange(own, other); db.Accounts.AddRange(ownAccount, otherAccount);
                db.WireTransfers.AddRange(awaiting, stuck, final, duplicate1, duplicate2, foreignWire);
                db.AchEntries.AddRange(released1, released2, invalidRelease, pending, returned, corruptAccount); db.AchBatches.Add(futureBatch);
                db.AchReturns.Add(new() { AchEntryId = returned.Id, ReturnCode = "R02", Reason = "Account closed" });
                db.AchNotificationsOfChange.Add(new() { AchEntryId = returned.Id, ChangeCode = "C01", Description = "Future account correction", CorrectedData = "masked" });
                db.OperationsCases.Add(overdue); db.OutboxMessages.AddRange(retry, futureRetry, foreignRetry);
                (await db.SimulationStates.SingleAsync(x => x.Name == "default")).CurrentUtc = new(date.ToDateTime(new(12, 0)), TimeSpan.Zero);
                await db.SaveChangesAsync();
            }
            var jobs = services.GetRequiredService<BankReportJobService>();
            var installations = await Task.WhenAll(jobs.InstallOperationalTasksAsync(own.Id, "Operations", default), jobs.InstallOperationalTasksAsync(own.Id, "Operations", default));
            Assert.Equal(12, installations.Sum());
            Assert.Equal(0, await jobs.InstallOperationalTasksAsync(own.Id, "Operations", default));
            List<BankReportTask> tasks;
            await using (var db = await factory.CreateDbContextAsync())
            {
                tasks = await db.BankReportTasks.OrderBy(x => x.Kind).ToListAsync();
                Assert.All(tasks, x => { Assert.Equal(own.Id, x.BankId); Assert.False(x.Enabled); });
            }
            async Task<BankReportRun> Run(BankReportTask task)
            {
                var id = await jobs.RequestAsync(own.Id, task.Id, "Operations", default);
                Assert.True(await jobs.ProcessOneAsync(default));
                await using var db = await factory.CreateDbContextAsync();
                var run = await db.BankReportRuns.AsNoTracking().SingleAsync(x => x.Id == id);
                Assert.True(run.Status == ReportRunStatus.Succeeded, run.Error);
                Assert.NotNull(run.ReportJson); return run;
            }
            foreach (var task in tasks) { await Run(task); }
            int cases, handoffs;
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal(1, await db.AchFiles.CountAsync(x => x.OriginatingBankId == own.Id));
                Assert.Null((await db.AchBatches.SingleAsync(x => x.Id == futureBatch.Id)).AchFileId);
                Assert.Equal(22, (await db.Accounts.SingleAsync(x => x.Id == ownAccount.Id)).HeldBalance);
                Assert.Equal(AchEntryStatus.Rejected, (await db.AchEntries.SingleAsync(x => x.Id == corruptAccount.Id)).Status);
                Assert.Equal(0, (await db.Accounts.SingleAsync(x => x.Id == otherAccount.Id)).HeldBalance);
                Assert.Equal(AchEntryStatus.PendingApproval, (await db.AchEntries.SingleAsync(x => x.Id == pending.Id)).Status);
                Assert.Equal(AchEntryStatus.Entered, (await db.AchEntries.SingleAsync(x => x.Id == invalidRelease.Id)).Status);
                Assert.Equal(WireStatus.PendingApproval, (await db.WireTransfers.SingleAsync(x => x.Id == awaiting.Id)).Status);
                Assert.Equal(OperationsCaseSeverity.High, (await db.OperationsCases.SingleAsync(x => x.Id == overdue.Id)).Severity);
                Assert.Equal("Keep this evidence", (await db.OperationsCases.SingleAsync(x => x.Id == overdue.Id)).Notes);
                Assert.All(await db.OperationsCases.ToListAsync(), x => Assert.Equal(own.Id, x.BankId));
                cases = await db.OperationsCases.CountAsync(); handoffs = await db.StudioNotes.CountAsync(); Assert.True(handoffs > 0);
                var reviewed = await db.OperationsCases.SingleAsync(x => x.CaseType == "Approval reminders" && x.Reference == $"wire-approval:{awaiting.Id:N}");
                reviewed.Status = OperationsCaseStatus.Resolved; reviewed.Resolution = "Human reviewed"; reviewed.ResolvedDate = now;
                await db.SaveChangesAsync();
                Assert.All(await db.StudioNotes.ToListAsync(), x => Assert.Equal("PaymentApprover", x.RecipientRole));
                var savedRetry = await db.OutboxMessages.SingleAsync(x => x.Id == retry.Id);
                Assert.Equal(2, savedRetry.Attempts); Assert.Equal("{}", savedRetry.Payload); Assert.Equal("Original error", savedRetry.LastError);
                Assert.Equal(futureRetry.NextAttemptDate, (await db.OutboxMessages.SingleAsync(x => x.Id == futureRetry.Id)).NextAttemptDate);
                Assert.Equal(foreignRetry.NextAttemptDate, (await db.OutboxMessages.SingleAsync(x => x.Id == foreignRetry.Id)).NextAttemptDate);
                Assert.Equal(1, await db.OutboxMessages.CountAsync(x => x.Queue == Queues.AchBatchReady));
            }
            foreach (var task in tasks) { await Run(task); }
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal(cases, await db.OperationsCases.CountAsync()); Assert.Equal(handoffs, await db.StudioNotes.CountAsync());
                Assert.Equal(OperationsCaseStatus.Resolved, (await db.OperationsCases.SingleAsync(x => x.Reference == $"wire-approval:{awaiting.Id:N}")).Status);
                Assert.Equal(1, await db.AchFiles.CountAsync()); Assert.Empty(await db.LedgerEntries.ToListAsync()); Assert.Empty(await db.AchLedgerEntries.ToListAsync());
                Assert.Equal(1000, (await db.Accounts.SingleAsync(x => x.Id == ownAccount.Id)).Balance);
                Assert.Equal(999, (await db.Accounts.SingleAsync(x => x.Id == otherAccount.Id)).Balance);
                Assert.Equal(22, (await db.Accounts.SingleAsync(x => x.Id == ownAccount.Id)).HeldBalance);
            }
            var recoverTask = tasks.Single(x => x.Kind == BankReportKind.StuckPayments);
            var recoverId = await jobs.RequestAsync(own.Id, recoverTask.Id, "Operations", default);
            var claim = (await jobs.ClaimAsync(DateTimeOffset.UtcNow, default))!;
            var document = await services.GetRequiredService<BankOperationalJobService>().ExecuteAsync(claim, default);
            var newClaim = (await jobs.ClaimAsync(DateTimeOffset.UtcNow.AddMinutes(4), default))!;
            var recovered = await services.GetRequiredService<BankOperationalJobService>().ExecuteAsync(newClaim, default);
            Assert.Equal(JsonSerializer.Serialize(document), JsonSerializer.Serialize(recovered));
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => services.GetRequiredService<BankOperationalJobService>().ExecuteAsync(claim, default));
            Assert.False(await jobs.CompleteAsync(claim, "stale", null, default));
            Assert.True(await jobs.CompleteAsync(newClaim, null, "Ambiguous response", default));
            await using (var db = await factory.CreateDbContextAsync())
            {
                var saved = await db.BankReportRuns.SingleAsync(x => x.Id == recoverId);
                Assert.Equal(ReportRunStatus.Succeeded, saved.Status); Assert.Null(saved.Error); Assert.NotNull(saved.ReportJson);
                Assert.Equal(cases, await db.OperationsCases.CountAsync());
            }
        }
        finally { await using var db = await factory.CreateDbContextAsync(); await db.Database.EnsureDeletedAsync(); }
    }
    private static Bank Bank(string name, string routing, string bic) => new() { Name = name, RoutingNumber = routing, Bic = bic, FedParticipantId = name, TownName = "Tulsa", CountryCode = "US", MasterAccountBalance = 1, AchExposureLimit = 10000 };
    private static WireTransfer Wire(Guid bank, Guid receiver, WireStatus status, decimal amount, DateTimeOffset now) => new() { BankId = bank, SenderBankId = bank, ReceiverBankId = receiver, Direction = WireDirection.Outgoing, SenderName = "Sender", ReceiverName = "Receiver", BeneficiaryAccountNumber = "654321", Amount = amount, Status = status, CreatedBy = "Maker", CreatedDate = now.AddHours(-2) };
    private static AchEntry Entry(Guid bank, Guid receiver, Guid account, decimal amount, DateOnly date, DateTimeOffset now) => new() { OriginatingBankId = bank, ReceivingBankId = receiver, OriginatingAccountId = account, CompanyName = "Company", CompanyId = "COMP", ReceiverName = "Receiver", ReceivingAccountNumber = "654321", ReceivingRoutingNumber = "103000648", TransactionCode = AchTransactionCode.CheckingCredit, SecCode = AchStandardEntryClass.Ppd, Amount = amount, EffectiveEntryDate = date, Status = AchEntryStatus.Entered, CreatedBy = "Maker", ApprovedBy = "Approver", ApprovedDate = now.AddHours(-2), CreatedDate = now.AddHours(-3) };
}
