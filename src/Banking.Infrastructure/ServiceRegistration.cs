using Banking.Domain;
using Banking.Infrastructure.Checks;
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
        services.AddSingleton<MessageReliabilityStore>();
        services.AddSingleton<OutboxDispatcher>();
        services.AddSingleton<PaymentReconciliationService>();
        services.AddSingleton<AchBatchProcessor>();
        services.AddSingleton<BankOperationalJobService>();
        services.AddSingleton<BankReportBuilder>();
        services.AddSingleton<BankReportJobService>();
        services.AddSingleton<SimulationStateService>();
        services.AddSingleton<UnifiedJournalService>();
        services.AddSingleton<LabSchemaUpgradeRunner>();
        services.AddSingleton<IQueueOperationsMonitor, RabbitMqOperationsMonitor>();
        services.AddSingleton<IIsoMessageService, IsoMessageService>();
        services.AddSingleton<IWireIsoMessageService, WireIsoMessageService>();
        services.AddSingleton<INonValueMessageWorkflowService, NonValueMessageWorkflowService>();
        services.AddSingleton<IFedNowMessageService, FedNowMessageService>();
        services.AddSingleton<ICbprPlusMessageService, CbprPlusMessageService>();
        services.AddSingleton<IPaymentRouteResolver, PaymentRouteResolver>();
        services.AddSingleton<IWireComplianceService, WireComplianceService>();
        services.AddSingleton<NachaFileWriter>();
        services.AddSingleton<NachaFileParser>();
        services.AddSingleton<X937CashLetterWriter>();
        return services;
    }

    public static Task InitializeDatabaseAsync(this IServiceProvider services,
        bool seed = false, CancellationToken cancellationToken = default) =>
        DatabaseInitializer.InitializeAsync(services, seed, cancellationToken);
}
