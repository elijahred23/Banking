using Banking.Infrastructure;

namespace Banking.Web;

public sealed class BankReportWorker(BankReportJobService jobs, ILogger<BankReportWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        RunLoopAsync(async token => { await jobs.EnqueueDueAsync(DateTimeOffset.UtcNow, token); return false; }, stoppingToken),
        RunLoopAsync(jobs.ProcessOneAsync, stoppingToken));

    private async Task RunLoopAsync(Func<CancellationToken, Task<bool>> work, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var worked = false;
            try { worked = await work(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Bank report worker tick failed; durable work will be retried"); }
            if (worked) continue;
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
}
