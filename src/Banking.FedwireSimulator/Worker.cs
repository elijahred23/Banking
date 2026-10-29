using Banking.Domain;

namespace Banking.FedwireSimulator;

public sealed class Worker(IMessageBus bus, FedwirePaymentProcessor processor) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.ConsumeAsync<FedEnvelope>(Queues.FedOutbound, processor.ProcessAsync, stoppingToken);
}
