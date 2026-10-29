using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed class SimulationStateService(IDbContextFactory<BankingDbContext> dbFactory)
{
    public async Task<SimulationState> GetAsync(CancellationToken token = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        return await db.SimulationStates.AsNoTracking().SingleAsync(x => x.Name == "default", token);
    }

    public async Task<DateTimeOffset> UtcNowAsync(CancellationToken token = default) =>
        (await GetAsync(token)).CurrentUtc;

    public async Task<SimulationState> AdvanceAsync(TimeSpan duration, CancellationToken token = default)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(31))
            throw new ArgumentOutOfRangeException(nameof(duration));
        await using var probe = await dbFactory.CreateDbContextAsync(token);
        SimulationState result = null!;
        await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token);
            var state = await db.SimulationStates.SingleAsync(x => x.Name == "default", token);
            state.CurrentUtc = state.CurrentUtc.Add(duration);
            state.UpdatedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            result = state;
        });
        return result;
    }

    public async Task<SimulationState> AdvanceToNextAchDateAsync(Guid bankId, CancellationToken token = default)
    {
        await using var probe = await dbFactory.CreateDbContextAsync(token);
        SimulationState result = null!;
        await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token);
            var state = await db.SimulationStates.SingleAsync(x => x.Name == "default", token);
            var date = DateOnly.FromDateTime(state.CurrentUtc.UtcDateTime);
            var dates = await db.AchEntries.Where(x => x.OriginatingBankId == bankId
                && x.Status == AchEntryStatus.Batched && x.EffectiveEntryDate > date)
                .Select(x => x.EffectiveEntryDate).Distinct().ToListAsync(token);
            state.CurrentUtc = LabClockPolicy.Next(state.CurrentUtc, dates).At;
            state.UpdatedDate = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            result = state;
        });
        return result;
    }

}
