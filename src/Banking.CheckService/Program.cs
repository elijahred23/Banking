using Banking.CheckService;
using Banking.Infrastructure;

await BankingWorkerHost.RunAsync<Worker>(args);
