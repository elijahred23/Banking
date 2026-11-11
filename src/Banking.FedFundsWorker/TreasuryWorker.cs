using Banking.Infrastructure;

namespace Banking.FedFundsWorker;

/// <summary>SQL-backed scheduled recovery, accrual and maturity processing using the shared simulation clock.</summary>
public sealed class TreasuryWorker(FedFundsTradeService treasury, ILogger<TreasuryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try { await treasury.ProcessAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Treasury processing failed; durable obligations will be retried on the next scan"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
