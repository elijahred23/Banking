using Banking.Domain;
using Banking.Infrastructure;

namespace Banking.Web;

public sealed class StatementWorker(IMessageBus bus, CorrespondentPostingService postings, StatementGenerationService generation,
    CorrespondentPlatformService platform, StatementReconciliationService reconciliation, StatementScheduleService schedules, ILogger<StatementWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken token) => Task.WhenAll(
        Loop(async ct => { await postings.ProcessPaymentsAsync(ct); await platform.ProcessDueAsync(ct); await schedules.ProcessDueAsync(ct); }, token),
        bus.ConsumeAsync<CorrespondentPostingNotice>(StatementQueues.Posting, async (message, ct) => {
            await postings.MirrorAsync(message, ct);
            await generation.NotifyAsync(message, ct);
        }, token),
        bus.ConsumeAsync<StatementReady>(StatementQueues.Delivery, async (message, ct) => {
            await reconciliation.DeliverAsync(message, ct);
            await AutoReconcile(message, ct);
        }, token));
    private async Task AutoReconcile(StatementReady message, CancellationToken ct)
    {
        try { await reconciliation.ReconcileAsync(message.RecipientBankId, message.StatementId, ct); }
        catch (ArgumentException) { /* Single-entry notifications have no full-period reconciliation. */ }
    }
    private async Task Loop(Func<CancellationToken, Task> work, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await work(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Statement processing tick failed; durable records retained"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
}
