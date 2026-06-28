using Banking.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Banking.Infrastructure;

public static class ServiceRegistration
{
    public static IServiceCollection AddBankingInfrastructure(this IServiceCollection services,
        IConfiguration configuration)
    {
        var sql = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");
        services.AddDbContextFactory<BankingDbContext>(options =>
            options.UseSqlServer(sql, sqlOptions => sqlOptions.EnableRetryOnFailure()));
        services.AddSingleton<IMessageBus, RabbitMqMessageBus>();
        services.AddSingleton<IIsoMessageService, IsoMessageService>();
        return services;
    }

    public static async Task InitializeDatabaseAsync(this IServiceProvider services,
        bool seed = false, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BankingDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        if (seed && !await db.Banks.AnyAsync(cancellationToken))
        {
            var bankers = Bank("Bankers Bank", "101000019", "BANKERS", 50_000_000m,
                ("John Smith", "123456", 125_000m));
            var first = Bank("First Oklahoma Bank", "103000648", "FIRSTOK", 40_000_000m,
                ("Mary Jones", "654321", 25_000m));
            var community = Bank("Community National Bank", "111901234", "COMMUNITY", 25_000_000m,
                ("Alice Carter", "445566", 80_000m));
            var redRiver = Bank("Red River Bank", "111000753", "REDRIVER", 30_000_000m,
                ("Robert Lee", "778899", 55_000m));
            db.Banks.AddRange(bankers, first, community, redRiver);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static Bank Bank(string name, string routing, string participant, decimal balance,
        (string Name, string Account, decimal Balance) customer) => new()
    {
        Name = name, RoutingNumber = routing, FedParticipantId = participant,
        MasterAccountBalance = balance,
        Customers = [new Customer { Name = customer.Name,
            Accounts = [new Account { AccountNumber = customer.Account, Balance = customer.Balance }] }]
    };
}
