using System.Text.Json;
using Banking.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Banking.Infrastructure;

public sealed class RabbitMqMessageBus(IConfiguration configuration,
    MessageReliabilityStore reliability,
    Microsoft.Extensions.Logging.ILogger<RabbitMqMessageBus> logger) : IMessageBus, IAsyncDisposable
{
    private const int MaximumDeliveryAttempts = 3;
    private readonly ConnectionFactory _factory = new()
    {
        HostName = configuration["RabbitMq:HostName"]
            ?? throw new InvalidOperationException("RabbitMq:HostName is required."),
        UserName = configuration["RabbitMq:UserName"]
            ?? throw new InvalidOperationException("RabbitMq:UserName is required."),
        Password = configuration["RabbitMq:Password"]
            ?? throw new InvalidOperationException("RabbitMq:Password is required."),
        AutomaticRecoveryEnabled = true,
        NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
    };
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private IConnection? _publishConnection;
    private IChannel? _publishChannel;

    public async Task PublishAsync<T>(string queue, T message, CancellationToken cancellationToken = default,
        string? messageId = null)
    {
        await _publishLock.WaitAsync(cancellationToken);
        try
        {
            _publishConnection ??= await _factory.CreateConnectionAsync(cancellationToken);
            _publishChannel ??= await _publishConnection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true), cancellationToken);
            await _publishChannel.QueueDeclareAsync(queue, durable: true, exclusive: false,
                autoDelete: false, cancellationToken: cancellationToken);
            var body = JsonSerializer.SerializeToUtf8Bytes(message);
            var properties = new BasicProperties
            {
                Persistent = true,
                ContentType = "application/json",
                MessageId = messageId ?? Guid.NewGuid().ToString("N")
            };
            await _publishChannel.BasicPublishAsync(string.Empty, queue, mandatory: true,
                basicProperties: properties, body: body, cancellationToken: cancellationToken);
        }
        finally { _publishLock.Release(); }
    }

    public async Task ConsumeAsync<T>(string queue, Func<T, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        await using var connection = await _factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.BasicQosAsync(0, 1, false, cancellationToken);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, args) =>
        {
            var body = args.Body.ToArray();
            var messageId = args.BasicProperties.MessageId;
            if (string.IsNullOrWhiteSpace(messageId))
                messageId = MessageReliabilityStore.PayloadHash(body);
            InboxAttempt? attempt = null;
            try
            {
                attempt = await reliability.BeginAttemptAsync(queue, messageId, body, cancellationToken);
                if (attempt.AlreadyCompleted)
                {
                    await channel.BasicAckAsync(args.DeliveryTag, false, cancellationToken);
                    return;
                }
                var message = JsonSerializer.Deserialize<T>(body)
                    ?? throw new InvalidOperationException($"Empty {typeof(T).Name} message.");
                await handler(message, cancellationToken);
                await reliability.CompleteAsync(attempt.InboxId, cancellationToken);
                await channel.BasicAckAsync(args.DeliveryTag, false, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                var deadLettered = attempt is not null && await reliability.FailAsync(attempt.InboxId,
                    System.Text.Encoding.UTF8.GetString(body), ex, MaximumDeliveryAttempts, cancellationToken);
                logger.LogError(ex, "Failed processing {Queue}; attempt {Attempt}; dead lettered: {DeadLettered}",
                    queue, attempt?.Attempts ?? 0, deadLettered);
                await channel.BasicNackAsync(args.DeliveryTag, false, requeue: !deadLettered, cancellationToken);
            }
        };
        await channel.BasicConsumeAsync(queue, autoAck: false, consumer, cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_publishChannel is not null) await _publishChannel.DisposeAsync();
        if (_publishConnection is not null) await _publishConnection.DisposeAsync();
        _publishLock.Dispose();
    }
}
