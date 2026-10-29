using Banking.WireService;
using Banking.Infrastructure;

await BankingWorkerHost.RunAsync<Worker>(args);
