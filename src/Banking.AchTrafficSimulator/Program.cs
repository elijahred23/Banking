using Banking.AchTrafficSimulator;
using Banking.Infrastructure;

await BankingWorkerHost.RunAsync<Worker>(args, services => services.AddSingleton<AchTrafficService>());
