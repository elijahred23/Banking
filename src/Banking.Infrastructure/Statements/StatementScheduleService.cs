using System.Text.Json;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Banking.Infrastructure;

public sealed class StatementScheduleService(IDbContextFactory<BankingDbContext> factory, ILogger<StatementScheduleService> logger)
{
    public Task SaveAsync(Guid issuerBankId, Guid accountId, string timeZone, TimeOnly localTime,
        DateOnly[] holidays, bool enabled, CancellationToken ct) => StatementTransactions.Run(factory, async db => {
            var account = await StatementTransactions.Account(db, issuerBankId, accountId, true, ct);
            if (timeZone.Length > 100 || holidays.Length > 500) throw new ArgumentException("Use a valid time zone and at most 500 holidays.");
            var now = await StatementTransactions.Now(db, ct);
            var next = StatementPolicy.NextRun(now, timeZone, localTime, holidays);
            var schedule = await db.StatementSchedules.SingleOrDefaultAsync(x => x.AccountId == account.Id, ct);
            if (schedule is null)
            {
                schedule = new StatementSchedule { AccountId = account.Id, PeriodStart = now, NextRunAt = next, CreatedAt = now };
                db.StatementSchedules.Add(schedule);
            }
            // Preserve an overdue cursor and its period when editing or resuming: missed statements are not discarded.
            else if (schedule.NextRunAt > now) schedule.NextRunAt = next;
            if (schedule.LeaseToken is not null && schedule.LeaseUntil > DateTimeOffset.UtcNow)
                throw new ArgumentException("This schedule is currently leased. Retry after the active run finishes.");
            schedule.TimeZone = timeZone; schedule.LocalTime = localTime; schedule.HolidaysJson = JsonSerializer.Serialize(holidays);
            schedule.Enabled = enabled;
            return true;
        }, ct);

    public async Task ProcessDueAsync(CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            var claimed = await StatementTransactions.Run(factory, async db => {
                var now = await StatementTransactions.Now(db, ct);
                var wall = DateTimeOffset.UtcNow;
                var schedule = await db.StatementSchedules.Where(x => x.Enabled && x.NextRunAt <= now
                    && (x.LeaseUntil == null || x.LeaseUntil <= wall)).OrderBy(x => x.NextRunAt).FirstOrDefaultAsync(ct);
                if (schedule is null) return null;
                schedule.LeaseToken = Guid.NewGuid(); schedule.LeaseUntil = wall.AddMinutes(2);
                return new Claim(schedule.Id, schedule.LeaseToken.Value);
            }, ct);
            if (claimed is null) break;
            try
            {
                await StatementTransactions.Run(factory, async db => {
                    var schedule = await db.StatementSchedules.Include(x => x.Account)
                        .SingleAsync(x => x.Id == claimed.Id && x.LeaseToken == claimed.Token, ct);
                    await StatementGenerationService.Generate(db, schedule.Account, StatementKind.Daily,
                        schedule.PeriodStart, schedule.NextRunAt, null, null, ct);
                    var end = schedule.NextRunAt;
                    schedule.PeriodStart = end;
                    schedule.NextRunAt = StatementPolicy.NextRun(end, schedule.TimeZone, schedule.LocalTime,
                        JsonSerializer.Deserialize<DateOnly[]>(schedule.HolidaysJson) ?? []);
                    schedule.LeaseToken = null; schedule.LeaseUntil = null; schedule.LastError = null;
                    return true;
                }, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Statement schedule {ScheduleId} failed; cursor retained for recovery", claimed.Id);
                await StatementTransactions.Run(factory, async db => {
                    var schedule = await db.StatementSchedules.SingleAsync(x => x.Id == claimed.Id, ct);
                    if (schedule.LeaseToken == claimed.Token) { schedule.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(30); schedule.LastError = ex.Message[..Math.Min(1000, ex.Message.Length)]; }
                    return true;
                }, ct);
            }
        }
    }
    private sealed record Claim(Guid Id, Guid Token);
}
