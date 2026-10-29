using Banking.FedwireSimulator;
using Banking.Infrastructure;

await BankingWorkerHost.RunAsync<Worker>(args);
