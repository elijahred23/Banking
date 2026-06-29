using System.Collections.Concurrent;
using Banking.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web.Realtime;

public sealed class WireUpdateBroadcaster(
    IDbContextFactory<BankingDbContext> dbFactory,
    IHubContext<WireUpdatesHub> hub,
    WireUpdateTracker tracker,
    ILogger<WireUpdateBroadcaster> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, string> _versions = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(750));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var wireIds = tracker.WireIds;
                foreach (var staleId in _versions.Keys.Except(wireIds)) _versions.TryRemove(staleId, out _);
                if (wireIds.Count == 0) continue;

                try
                {
                    await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                    foreach (var wireId in wireIds)
                    {
                        var version = await VersionAsync(db, wireId, stoppingToken);
                        if (version is null) continue;
                        if (_versions.TryGetValue(wireId, out var previous) && previous != version)
                            await hub.Clients.Group(WireUpdatesHub.Group(wireId))
                                .SendAsync("wireUpdated", wireId, stoppingToken);
                        _versions[wireId] = version;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not check watched wires for live updates");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private static async Task<string?> VersionAsync(BankingDbContext db, Guid wireId,
        CancellationToken token)
    {
        var wire = await db.WireTransfers.AsNoTracking()
            .Where(x => x.Id == wireId)
            .Select(x => new { x.Status, x.Imad, x.Omad })
            .SingleOrDefaultAsync(token);
        if (wire is null) return null;
        var eventVersion = await db.WireEvents.Where(x => x.WireTransferId == wireId)
            .GroupBy(_ => 1).Select(x => new { Count = x.Count(), Latest = x.Max(y => y.CreatedDate) })
            .SingleOrDefaultAsync(token);
        var messageVersion = await db.IsoMessages.Where(x => x.WireTransferId == wireId)
            .GroupBy(_ => 1).Select(x => new { Count = x.Count(), Latest = x.Max(y => y.CreatedDate) })
            .SingleOrDefaultAsync(token);
        var deliveryVersion = await db.MessageDeliveries.Where(x => x.WireTransferId == wireId)
            .GroupBy(_ => 1).Select(x => new { Count = x.Count(), Latest = x.Max(y => y.UpdatedDate) })
            .SingleOrDefaultAsync(token);
        var caseVersion = await db.WireCases.Where(x => x.WireTransferId == wireId)
            .GroupBy(_ => 1).Select(x => new { Count = x.Count(), Latest = x.Max(y => y.UpdatedDate) })
            .SingleOrDefaultAsync(token);
        return $"{wire.Status}|{wire.Imad}|{wire.Omad}|{eventVersion}|{messageVersion}|{deliveryVersion}|{caseVersion}";
    }
}
