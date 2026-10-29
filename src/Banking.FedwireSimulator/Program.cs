using Banking.FedwireSimulator;
using Banking.Infrastructure;

await BankingWorkerHost.RunAsync<Worker>(args, services =>
{
    services.AddSingleton<IFedwireSettlementService, FedwireSettlementService>();
    services.AddSingleton<FedwireResponsePublisher>();
    services.AddSingleton<FedwirePaymentProcessor>();
});
