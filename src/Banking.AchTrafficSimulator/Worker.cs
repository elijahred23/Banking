using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.AchTrafficSimulator;

public sealed class Worker(IDbContextFactory<BankingDbContext> factory,
    AchTrafficService traffic, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                var names = await db.SimulationStates.AsNoTracking()
                    .Where(x => x.Name.StartsWith(AchTraffic.Prefix) && !x.IsPaused)
                    .Select(x => x.Name).ToListAsync(stoppingToken);
                foreach (var name in names)
                    if (Guid.TryParse(name[AchTraffic.Prefix.Length..].Split(':')[0], out var bankId))
                    {
                        try { await traffic.TickAsync(bankId, stoppingToken); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        { logger.LogWarning(ex, "Traffic tick failed for {BankId}; will retry", bankId); }
                    }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogWarning(ex, "Traffic simulator waiting for database"); }
        }
    }
}
