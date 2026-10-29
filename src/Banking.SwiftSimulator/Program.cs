using Banking.SwiftSimulator;
using Banking.Infrastructure;

await BankingWorkerHost.RunAsync<Worker>(args);
