using Banking.AchService;
using Banking.Infrastructure;

await BankingWorkerHost.RunAsync<Worker>(args);
