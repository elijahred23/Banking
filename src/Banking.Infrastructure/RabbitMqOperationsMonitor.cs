using Banking.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Banking.Infrastructure;

public sealed record QueueMetric(string Name, uint? MessageCount, uint? ConsumerCount, bool Exists);

public sealed record QueueMonitorSnapshot(
    bool BrokerAvailable,
    IReadOnlyList<QueueMetric> Queues,
    string? Error);

public interface IQueueOperationsMonitor
{
    Task<QueueMonitorSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

public sealed class RabbitMqOperationsMonitor(
    IConfiguration configuration,
    ILogger<RabbitMqOperationsMonitor> logger) : IQueueOperationsMonitor
{
    private static readonly string[] MonitoredQueues =
    [
        Queues.FedOutbound, Queues.FedInbound,
        Queues.FedNowOutbound, Queues.FedNowInbound,
        Queues.AchEntryCreated, Queues.AchBatchReady, Queues.AchOutbound, Queues.AchInbound,
        Queues.AchReturnInbound, Queues.AchNocInbound, Queues.AchSettled,
        Queues.CheckDepositCreated, Queues.CheckCashLetterReady, Queues.CheckOutbound,
        Queues.CheckInbound, Queues.CheckReturnInbound, Queues.CheckSettled,
        Queues.SwiftOutbound, Queues.SwiftInbound
    ];

    private readonly ConnectionFactory _factory = new()
    {
        HostName = configuration["RabbitMq:HostName"] ?? "localhost",
        UserName = configuration["RabbitMq:UserName"] ?? "guest",
        Password = configuration["RabbitMq:Password"] ?? "guest",
        VirtualHost = configuration["RabbitMq:VirtualHost"] ?? "/"
    };

    public async Task<QueueMonitorSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await using var connection = await _factory.CreateConnectionAsync(
                "banking-operations-dashboard", timeout.Token);
            var metrics = new List<QueueMetric>(MonitoredQueues.Length);
            foreach (var queue in MonitoredQueues)
            {
                try
                {
                    // A failed passive declaration closes its channel, so each probe gets its own channel.
                    await using var channel = await connection.CreateChannelAsync(cancellationToken: timeout.Token);
                    var result = await channel.QueueDeclarePassiveAsync(queue, timeout.Token);
                    metrics.Add(new QueueMetric(queue, result.MessageCount, result.ConsumerCount, true));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    metrics.Add(new QueueMetric(queue, null, null, false));
                }
            }

            return new QueueMonitorSnapshot(true, metrics, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RabbitMQ operations snapshot is unavailable.");
            return new QueueMonitorSnapshot(false,
                MonitoredQueues.Select(x => new QueueMetric(x, null, null, false)).ToList(),
                "RabbitMQ is unavailable");
        }
    }
}
