using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class BankReportIntegrationTests
{
    [Fact, Trait("Category", "ExternalIntegration")]
    public async Task SqlSchedulerDeduplicatesRecoversLeasesAndStoresImmutableScopedReports()
    {
        if (Environment.GetEnvironmentVariable("BANKING_RUN_REPORT_INTEGRATION") != "1") return;
        var name = $"BankingReportTest_{Guid.NewGuid():N}";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = $"Server=localhost,11433;Database={name};User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True"
        }).Build();
        await using var services = new ServiceCollection().AddLogging().AddBankingInfrastructure(configuration).BuildServiceProvider();
        var factory = services.GetRequiredService<IDbContextFactory<BankingDbContext>>();
        try
        {
            await services.InitializeDatabaseAsync();
            await using (var db = await factory.CreateDbContextAsync())
            {
                // Only this test-owned database: exercise upgrade from a schema without report tables.
                await db.Database.ExecuteSqlRawAsync("DROP TABLE dbo.BankReportRuns; DROP TABLE dbo.BankReportTasks;");
                await services.GetRequiredService<LabSchemaUpgradeRunner>().UpgradeAsync(db);
                await services.GetRequiredService<LabSchemaUpgradeRunner>().UpgradeAsync(db);
                db.Banks.AddRange(Bank("One", "101000019", "ONEBUS33XXX"), Bank("Two", "103000648", "TWOBUS33XXX"));
                await db.SaveChangesAsync();
            }
            Guid bankId, otherId;
            await using (var db = await factory.CreateDbContextAsync()) { bankId = await db.Banks.Where(x => x.Name == "One").Select(x => x.Id).SingleAsync(); otherId = await db.Banks.Where(x => x.Name == "Two").Select(x => x.Id).SingleAsync(); }
            var jobs = services.GetRequiredService<BankReportJobService>();
            var taskId = await jobs.SaveTaskAsync(bankId, null, "Hourly liquidity", BankReportKind.Liquidity, ReportFrequency.Hourly, new(6, 0), 24, true, "Operations", default);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => jobs.RequestAsync(otherId, taskId, "Other", default));
            var requests = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => jobs.RequestAsync(bankId, taskId, "Manual", default)));
            Assert.Single(requests.Distinct());
            var now = DateTimeOffset.UtcNow;
            var claim = await jobs.ClaimAsync(now, default); Assert.NotNull(claim);
            Assert.Null(await jobs.ClaimAsync(now, default));
            var recovered = await jobs.ClaimAsync(now.AddMinutes(4), default); Assert.NotNull(recovered);
            Assert.Equal(claim.Id, recovered.Id); Assert.NotEqual(claim.LeaseToken, recovered.LeaseToken);
            Assert.False(await jobs.CompleteAsync(claim, "stale result", null, default));
            var builder = services.GetRequiredService<BankReportBuilder>();
            var payload = System.Text.Json.JsonSerializer.Serialize(await builder.BuildAsync(recovered, default));
            Assert.True(await jobs.CompleteAsync(recovered, payload, null, default));
            Assert.False(await jobs.CompleteAsync(recovered, "overwrite", null, default));
            await jobs.SaveTaskAsync(bankId, taskId, "Changed template", BankReportKind.Exceptions, ReportFrequency.Hourly, new(6, 0), 48, true, "Operations", default);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var run = await db.BankReportRuns.SingleAsync(x => x.Id == claim.Id);
                Assert.Equal(BankReportKind.Liquidity, run.Kind); Assert.Equal("Hourly liquidity", run.TaskName); Assert.Equal(payload, run.ReportJson);
                var task = await db.BankReportTasks.SingleAsync(x => x.Id == taskId); task.NextRunAt = now.AddHours(-5); await db.SaveChangesAsync();
            }
            var due = await Task.WhenAll(jobs.EnqueueDueAsync(now, default), jobs.EnqueueDueAsync(now, default));
            Assert.Equal(1, due.Sum());
            Assert.True(await jobs.ProcessOneAsync(default));
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal(2, await db.BankReportRuns.CountAsync(x => x.TaskId == taskId));
                Assert.All(await db.BankReportRuns.ToListAsync(), x => Assert.Equal(ReportRunStatus.Succeeded, x.Status));
                Assert.True(await db.BankReportTasks.Where(x => x.Id == taskId).Select(x => x.NextRunAt > now).SingleAsync());
            }
            await jobs.SaveTaskAsync(bankId, taskId, "Paused", BankReportKind.Reconciliation, ReportFrequency.Hourly, new(6, 0), 24, false, "Operations", default);
            Assert.Equal(0, await jobs.EnqueueDueAsync(now.AddDays(1), default));
            await jobs.RequestAsync(bankId, taskId, "Manual while paused", default);
            Assert.True(await jobs.ProcessOneAsync(default));
            var exhaustedId = await jobs.RequestAsync(bankId, taskId, "Lease exhaustion exercise", default);
            Assert.NotNull(await jobs.ClaimAsync(now, default));
            Assert.NotNull(await jobs.ClaimAsync(now.AddMinutes(4), default));
            Assert.NotNull(await jobs.ClaimAsync(now.AddMinutes(8), default));
            Assert.Null(await jobs.ClaimAsync(now.AddMinutes(12), default));
            await using (var db = await factory.CreateDbContextAsync())
            {
                var exhausted = await db.BankReportRuns.SingleAsync(x => x.Id == exhaustedId);
                Assert.Equal(ReportRunStatus.Failed, exhausted.Status);
                Assert.Equal(3, exhausted.Attempts); Assert.NotNull(exhausted.CompletedAt); Assert.NotNull(exhausted.Error);
            }
            var retryId = await jobs.RequestAsync(bankId, taskId, "Explicit retry", default);
            Assert.NotEqual(exhaustedId, retryId);
            Assert.True(await jobs.ProcessOneAsync(default));
        }
        finally
        {
            await using var cleanup = await factory.CreateDbContextAsync();
            await cleanup.Database.EnsureDeletedAsync();
        }
    }
    private static Bank Bank(string name, string routing, string bic) => new() { Name = name, RoutingNumber = routing, Bic = bic, FedParticipantId = name, TownName = "Tulsa", CountryCode = "US" };
}
