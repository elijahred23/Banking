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
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var state = await db.SimulationStates.SingleAsync(x => x.Name == "default", token);
        state.CurrentUtc = state.CurrentUtc.Add(duration);
        state.UpdatedDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
        return state;
    }
}
