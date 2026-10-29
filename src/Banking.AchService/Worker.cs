using System.Data;
using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.AchService;

public sealed class Worker(IMessageBus bus, IDbContextFactory<BankingDbContext> dbFactory,
    NachaFileWriter writer, IConfiguration configuration, SimulationStateService simulation,
    ILogger<Worker> logger) : BackgroundService
{
    private readonly AchBatchProcessor processor = new(writer);
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        bus.ConsumeAsync<AchEntryCreated>(Queues.AchEntryCreated, ValidateAndBatchAsync, stoppingToken),
        RunCutoffAsync(stoppingToken));

    private async Task ValidateAndBatchAsync(AchEntryCreated command, CancellationToken token)
    {
        await using var probe = await dbFactory.CreateDbContextAsync(token);
        await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var bankId = await db.AchEntries.Where(x => x.Id == command.EntryId).Select(x => (Guid?)x.OriginatingBankId).SingleOrDefaultAsync(token);
            if (bankId is null) return;
            var date = DateOnly.FromDateTime((await simulation.UtcNowAsync(token)).UtcDateTime);
            var result = await processor.PrepareAsync(db, bankId.Value, command.EntryId, date, token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            if (result is not null) logger.LogInformation("ACH {EntryId}: {Status}: {Detail}", command.EntryId, result.Status, result.Detail);
        });
    }

    private async Task RunCutoffAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("Ach:BatchCutoffSeconds", 5))));
        while (await timer.WaitForNextTickAsync(token))
        {
            try { await CloseOpenBatchesAsync(token); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { logger.LogError(exception, "ACH cutoff failed; open batches remain durable for the next attempt"); }
        }
    }

    private async Task CloseOpenBatchesAsync(CancellationToken token)
    {
        await using var probe = await dbFactory.CreateDbContextAsync(token);
        var bankIds = await probe.AchBatches.Where(x => x.Status == "Open").Select(x => x.OriginatingBankId).Distinct().ToListAsync(token);
        foreach (var bankId in bankIds)
            await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var db = await dbFactory.CreateDbContextAsync(token);
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
                var date = DateOnly.FromDateTime((await simulation.UtcNowAsync(token)).UtcDateTime);
                var results = await processor.CloseAsync(db, bankId, date, token);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                foreach (var row in results) logger.LogInformation("ACH cutoff {Reference}: {Detail}", row.Reference, row.Detail);
            });
    }
}
