using Banking.MessageManager;
using Banking.Infrastructure;

await BankingWorkerHost.RunAsync<Worker>(args);
