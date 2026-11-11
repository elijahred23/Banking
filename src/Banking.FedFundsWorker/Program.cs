using Banking.Infrastructure;
using Banking.FedFundsWorker;

await BankingWorkerHost.RunAsync<TreasuryWorker>(args);
