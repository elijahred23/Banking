using System.Text.Json;
using Banking.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Banking.Infrastructure;

public sealed class RabbitMqMessageBus(IConfiguration configuration,
    Microsoft.Extensions.Logging.ILogger<RabbitMqMessageBus> logger) : IMessageBus, IAsyncDisposable
{
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

    public async Task PublishAsync<T>(string queue, T message, CancellationToken cancellationToken = default)
    {
        await _publishLock.WaitAsync(cancellationToken);
        try
        {
            _publishConnection ??= await _factory.CreateConnectionAsync(cancellationToken);
            _publishChannel ??= await _publishConnection.CreateChannelAsync(cancellationToken: cancellationToken);
            await _publishChannel.QueueDeclareAsync(queue, durable: true, exclusive: false,
                autoDelete: false, cancellationToken: cancellationToken);
            var body = JsonSerializer.SerializeToUtf8Bytes(message);
            var properties = new BasicProperties { Persistent = true, ContentType = "application/json" };
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
            try
            {
                var message = JsonSerializer.Deserialize<T>(args.Body.Span)
                    ?? throw new InvalidOperationException($"Empty {typeof(T).Name} message.");
                await handler(message, cancellationToken);
                await channel.BasicAckAsync(args.DeliveryTag, false, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                var retry = !args.Redelivered;
                logger.LogError(ex, "Failed processing {Queue}; requeue: {Retry}", queue, retry);
                await channel.BasicNackAsync(args.DeliveryTag, false, requeue: retry, cancellationToken);
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
