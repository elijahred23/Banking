using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Banking.Infrastructure;

/// <summary>Common composition for queue workers and simulator hosts.</summary>
public static class BankingWorkerHost
{
    public static async Task RunAsync<TWorker>(string[] args,
        Action<IServiceCollection>? configureServices = null)
        where TWorker : class, IHostedService
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddBankingInfrastructure(builder.Configuration);
        configureServices?.Invoke(builder.Services);
        builder.Services.AddHostedService<TWorker>();
        using var host = builder.Build();
        await host.RunAsync();
    }
}
