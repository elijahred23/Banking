using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.AchTrafficSimulator;

public sealed class Worker(IDbContextFactory<BankingDbContext> factory,
    AchTrafficService traffic, ILogger<Worker> logger) : TrafficSimulationWorker(factory, logger)
{
    protected override string StatePrefix => AchTraffic.Prefix;
    protected override bool TryResolveBankId(string name, out Guid bankId) =>
        AchTraffic.TryGetBankId(name, out bankId);
    protected override Task TickAsync(Guid bankId, CancellationToken token) => traffic.TickAsync(bankId, token);
}
