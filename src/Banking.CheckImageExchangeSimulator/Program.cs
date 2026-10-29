using Banking.CheckImageExchangeSimulator;
using Banking.Infrastructure;

await BankingWorkerHost.RunAsync<Worker>(args);
