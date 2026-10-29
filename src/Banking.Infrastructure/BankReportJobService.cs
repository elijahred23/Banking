using System.Data;
using System.Text.Json;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Banking.Infrastructure;

public sealed class BankReportJobService(IDbContextFactory<BankingDbContext> factory,
    BankReportBuilder builder, ILogger<BankReportJobService> logger)
{
    public async Task<Guid> SaveTaskAsync(Guid bankId, Guid? id, string name, BankReportKind kind,
        ReportFrequency frequency, TimeOnly dailyAtUtc, int lookbackHours, bool enabled,
        string actor, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 || !Enum.IsDefined(kind)
            || !Enum.IsDefined(frequency) || lookbackHours is < 1 or > 744)
            throw new ArgumentException("Choose a valid name, report type, schedule, and 1–744 hour window.");
        var taskId = id ?? Guid.NewGuid();
        return await TransactionAsync(async db =>
        {
            var task = await db.BankReportTasks.SingleOrDefaultAsync(x => x.Id == taskId && x.BankId == bankId, token);
            if (id is not null && task is null) throw new KeyNotFoundException();
            var now = DateTimeOffset.UtcNow;
            if (task is null)
            {
                task = new() { Id = taskId, BankId = bankId, Name = name.Trim(), CreatedBy = actor[..Math.Min(120, actor.Length)] };
                db.BankReportTasks.Add(task);
            }
            if (task.NextRunAt == default || task.Frequency != frequency || task.DailyAtUtc != dailyAtUtc || !task.Enabled && enabled)
                task.NextRunAt = ReportSchedulePolicy.Next(frequency, dailyAtUtc, now);
            task.Name = name.Trim(); task.Kind = kind; task.Frequency = frequency;
            task.DailyAtUtc = dailyAtUtc; task.LookbackHours = lookbackHours; task.Enabled = enabled; task.UpdatedAt = now;
            await db.SaveChangesAsync(token);
            return task.Id;
        }, token);
    }

    public Task<Guid> RequestAsync(Guid bankId, Guid taskId, string actor, CancellationToken token) =>
        TransactionAsync(async db =>
        {
            var task = await db.BankReportTasks.SingleOrDefaultAsync(x => x.Id == taskId && x.BankId == bankId, token)
                ?? throw new KeyNotFoundException();
            var active = await db.BankReportRuns.SingleOrDefaultAsync(x => x.TaskId == taskId && x.BankId == bankId && x.CompletedAt == null, token);
            if (active is not null) return active.Id;
            var run = NewRun(task, actor, DateTimeOffset.UtcNow, null);
            db.BankReportRuns.Add(run);
            await db.SaveChangesAsync(token);
            return run.Id;
        }, token);

    public Task<int> EnqueueDueAsync(DateTimeOffset now, CancellationToken token) => TransactionAsync(async db =>
    {
        var tasks = await db.BankReportTasks.Where(x => x.Enabled && x.NextRunAt <= now)
            .OrderBy(x => x.NextRunAt).Take(20).ToListAsync(token);
        var count = 0;
        foreach (var task in tasks)
        {
            if (!await db.BankReportRuns.AnyAsync(x => x.TaskId == task.Id && x.BankId == task.BankId && x.CompletedAt == null, token))
            {
                db.BankReportRuns.Add(NewRun(task, "Scheduled job", now, task.NextRunAt)); count++;
            }
            // Coalesce downtime into one current report instead of inventing historical snapshots.
            task.NextRunAt = ReportSchedulePolicy.Next(task.Frequency, task.DailyAtUtc, now);
            task.UpdatedAt = now;
        }
        await db.SaveChangesAsync(token);
        return count;
    }, token);

    public Task<BankReportRun?> ClaimAsync(DateTimeOffset now, CancellationToken token) => TransactionAsync(async db =>
    {
        var run = await db.BankReportRuns.Where(x => x.CompletedAt == null
                && (x.Status == ReportRunStatus.Queued || x.Status == ReportRunStatus.Running && x.LeaseUntil <= now))
            .OrderBy(x => x.RequestedAt).FirstOrDefaultAsync(token);
        if (run is null) return null;
        if (run.Attempts >= 3)
        {
            run.Status = ReportRunStatus.Failed; run.CompletedAt = now;
            run.Error = "The worker lease expired three times. Inspect service health and use Run now to create a new run.";
            run.LeaseToken = null; run.LeaseUntil = null;
            await db.SaveChangesAsync(token);
            return null;
        }
        run.Status = ReportRunStatus.Running; run.StartedAt ??= now; run.Attempts++;
        run.LeaseToken = Guid.NewGuid(); run.LeaseUntil = now.AddMinutes(3);
        await db.SaveChangesAsync(token);
        return run;
    }, token);

    public async Task<bool> ProcessOneAsync(CancellationToken token)
    {
        var run = await ClaimAsync(DateTimeOffset.UtcNow, token);
        if (run is null) return false;
        string? payload = null; string? error = null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromMinutes(2));
            payload = JsonSerializer.Serialize(await builder.BuildAsync(run, limit.Token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Report run {RunId} failed for bank {BankId}", run.Id, run.BankId);
            error = $"Report generation failed ({exception.GetType().Name}). Inspect service logs for run {run.Id} and use Run now to retry.";
        }
        await CompleteAsync(run, payload, error, token);
        return true;
    }

    public async Task<bool> CompleteAsync(BankReportRun claim, string? payload, string? error, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var run = await db.BankReportRuns.SingleOrDefaultAsync(x => x.Id == claim.Id && x.BankId == claim.BankId
            && x.LeaseToken == claim.LeaseToken && x.Status == ReportRunStatus.Running && x.CompletedAt == null, token);
        if (run is null || claim.LeaseToken is null) return false;
        run.ReportJson = payload; run.Error = error;
        run.Status = error is null && payload is not null ? ReportRunStatus.Succeeded : ReportRunStatus.Failed;
        run.CompletedAt = DateTimeOffset.UtcNow; run.LeaseUntil = null; run.LeaseToken = null;
        try { await db.SaveChangesAsync(token); return true; }
        catch (DbUpdateConcurrencyException) { return false; } // A recovered worker owns completion now.
    }

    private static BankReportRun NewRun(BankReportTask task, string actor, DateTimeOffset now, DateTimeOffset? scheduledFor) => new()
    {
        BankId = task.BankId, TaskId = task.Id, TaskName = task.Name, Kind = task.Kind,
        LookbackHours = task.LookbackHours, RequestedBy = actor[..Math.Min(120, actor.Length)],
        RequestedAt = now, ScheduledFor = scheduledFor
    };

    private async Task<T> TransactionAsync<T>(Func<BankingDbContext, Task<T>> action, CancellationToken token)
    {
        await using var probe = await factory.CreateDbContextAsync(token);
        return await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var result = await action(db);
            await transaction.CommitAsync(token);
            return result;
        });
    }
}
