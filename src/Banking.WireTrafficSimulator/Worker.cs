using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.WireTrafficSimulator;

public sealed class Worker(IDbContextFactory<BankingDbContext> factory,
    WireTrafficService traffic, ILogger<Worker> logger) : TrafficSimulationWorker(factory, logger)
{
    protected override string StatePrefix => WireTraffic.Prefix;
    protected override bool TryResolveBankId(string name, out Guid bankId) =>
        WireTraffic.TryGetBankId(name, out bankId);
    protected override Task TickAsync(Guid bankId, CancellationToken token) => traffic.TickAsync(bankId, token);
}
