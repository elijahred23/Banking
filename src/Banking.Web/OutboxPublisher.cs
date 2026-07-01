using System.Text.Json;
using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.Web;

public sealed class OutboxPublisher(IDbContextFactory<BankingDbContext> dbFactory,
    IMessageBus bus, ILogger<OutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do { await PublishBatchAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PublishBatchAsync(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var now = DateTimeOffset.UtcNow;
        var pending = await db.OutboxMessages.Where(x => x.PublishedDate == null
                && (x.NextAttemptDate == null || x.NextAttemptDate <= now))
            .OrderBy(x => x.CreatedDate).Take(20).ToListAsync(token);
        foreach (var item in pending)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<JsonElement>(item.Payload);
                await bus.PublishAsync(item.Queue, payload, token);
                item.PublishedDate = DateTimeOffset.UtcNow;
                item.NextAttemptDate = null;
                item.LastError = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                item.Attempts++;
                item.NextAttemptDate = DateTimeOffset.UtcNow.AddSeconds(
                    Math.Min(60, Math.Pow(2, Math.Min(item.Attempts, 6))));
                item.LastError = ex.Message.Length <= 1000 ? ex.Message : ex.Message[..1000];
                logger.LogWarning(ex, "Outbox message {MessageId} could not be published", item.Id);
            }
            await db.SaveChangesAsync(token);
        }
    }
}
