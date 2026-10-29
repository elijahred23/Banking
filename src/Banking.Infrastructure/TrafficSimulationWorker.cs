using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Banking.Infrastructure;

/// <summary>Polls enabled traffic states; each bank tick fails and retries independently.</summary>
public abstract class TrafficSimulationWorker(IDbContextFactory<BankingDbContext> factory,
    ILogger logger) : BackgroundService
{
    protected abstract string StatePrefix { get; }
    protected abstract bool TryResolveBankId(string name, out Guid bankId);
    protected abstract Task TickAsync(Guid bankId, CancellationToken token);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync(stoppingToken);
                var names = await db.SimulationStates.AsNoTracking()
                    .Where(x => x.Name.StartsWith(StatePrefix) && !x.IsPaused)
                    .Select(x => x.Name).ToListAsync(stoppingToken);
                foreach (var name in names)
                {
                    if (!TryResolveBankId(name, out var bankId)) continue;
                    try { await TickAsync(bankId, stoppingToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { logger.LogWarning(ex, "Traffic tick failed for {BankId}; will retry", bankId); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogWarning(ex, "Traffic simulator waiting for database"); }
        }
    }
}
