using Banking.FedNowSimulator;
using Banking.Infrastructure;

await BankingWorkerHost.RunAsync<Worker>(args);
