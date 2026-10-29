using Banking.WireTrafficSimulator;
using Banking.Infrastructure;

await BankingWorkerHost.RunAsync<Worker>(args, services => services.AddSingleton<WireTrafficService>());
