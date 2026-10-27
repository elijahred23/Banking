using System.Data;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed class AchTrafficService(IDbContextFactory<BankingDbContext> factory)
{
    public async Task TickAsync(Guid bankId, CancellationToken token)
    {
        await using var probe = await factory.CreateDbContextAsync(token);
        await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var prefix = AchTraffic.BankPrefix(bankId);
            var state = await db.SimulationStates.SingleOrDefaultAsync(x => x.Name.StartsWith(prefix) && !x.IsPaused, token);
            if (state is null) return;
            var now = DateTimeOffset.UtcNow;
            var pending = await db.AchEntries.Where(x => x.OriginatingBankId == bankId
                && x.CreatedBy == AchTraffic.Maker && x.Status == AchEntryStatus.PendingApproval).ToListAsync(token);
            foreach (var entry in pending)
            {
                AchTraffic.Approve(entry, now);
                db.OutboxMessages.Add(Outbox.Message(Queues.AchEntryCreated, new AchEntryCreated(entry.Id), bankId));
            }
            var outstanding = await db.AchEntries.CountAsync(x => x.OriginatingBankId == bankId
                && x.CreatedBy == AchTraffic.Maker && x.Status != AchEntryStatus.Posted
                && x.Status != AchEntryStatus.Returned && x.Status != AchEntryStatus.Rejected
                && x.Status != AchEntryStatus.NocReceived, token);
            if (state.CurrentUtc <= now && outstanding <= 20)
            {
                var mode = AchTraffic.Mode(state.Name);
                if (mode == AchTrafficMode.Mixed) mode = (AchTrafficMode)Random.Shared.Next(3);
                var companyNumber = mode == AchTrafficMode.Payroll ? 1 : mode == AchTrafficMode.Suppliers ? 2 : 3;
                var routing = await db.Banks.Where(x => x.Id == bankId).Select(x => x.RoutingNumber).SingleAsync(token);
                var number = TrafficPopulation.AccountNumber(routing, companyNumber);
                var company = await db.Accounts.Include(x => x.Customer).ThenInclude(x => x.Bank)
                    .SingleOrDefaultAsync(x => x.Customer.BankId == bankId && x.AccountNumber == number
                        && x.Status == AccountStatus.Open, token);
                var accounts = await db.Accounts.Include(x => x.Customer).ThenInclude(x => x.Bank)
                    .Where(x => x.Customer.BankId != bankId && x.Status == AccountStatus.Open
                        && x.Customer.Bank.CountryCode == "US").ToListAsync(token);
                var candidates = accounts.Where(x => mode == AchTrafficMode.Suppliers
                    ? TrafficPopulation.IsSupplier(x) : TrafficPopulation.IsPersonChecking(x))
                    .OrderBy(_ => Random.Shared.Next()).Take(4).ToList();
                var clock = await db.SimulationStates.SingleAsync(x => x.Name == "default", token);
                if (company is not null && candidates.Count > 0)
                    db.AchEntries.AddRange(AchTraffic.CreateRun(company, candidates, mode,
                        DateOnly.FromDateTime(clock.CurrentUtc.UtcDateTime), now));
                state.CurrentUtc = now.AddSeconds(60d / (double)Math.Clamp(state.SpeedMultiplier, 1m, 4m));
            }
            state.UpdatedDate = now;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });
    }
}
