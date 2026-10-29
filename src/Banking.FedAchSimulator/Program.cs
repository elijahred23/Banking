using Banking.FedAchSimulator;
using Banking.Infrastructure;

await BankingWorkerHost.RunAsync<Worker>(args);
