using System.Data;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

/// <summary>Durable bank-scoped schedules use SimulationStates under wire-traffic:{bankId}.
/// CurrentUtc is the next generation time; SpeedMultiplier is wires/minute (1–12).
/// The default learning clock is never changed.</summary>
public sealed class WireTrafficService(IDbContextFactory<BankingDbContext> factory)
{
    public async Task TickAsync(Guid bankId, CancellationToken token)
    {
        await using var probe = await factory.CreateDbContextAsync(token);
        await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var name = WireTraffic.StateName(bankId);
            var state = await db.SimulationStates.SingleOrDefaultAsync(x => x.Name == name, token);
            if (state is null || state.IsPaused) return;
            var now = DateTimeOffset.UtcNow;
            // Release only this bank's simulator-made wires, on a later tick than creation.
            var pending = await db.WireTransfers.Include(x => x.Events).Where(x =>
                x.BankId == bankId && x.CreatedBy == WireTraffic.Maker
                && x.Status == WireStatus.PendingApproval).ToListAsync(token);
            foreach (var wire in pending)
            {
                WireTraffic.Approve(wire, now);
                db.WireEvents.Add(wire.Events[^1]);
                db.OutboxMessages.Add(Outbox.Message(Queues.WireCreated, new WireCreated(wire.Id), bankId));
            }
            var outstanding = await db.WireTransfers.CountAsync(x => x.BankId == bankId
                && x.CreatedBy == WireTraffic.Maker && x.Status != WireStatus.Settled
                && x.Status != WireStatus.Rejected && x.Status != WireStatus.Returned, token);
            if (state.CurrentUtc <= now && outstanding < 5)
            {
                var sources = await db.Accounts.Include(x => x.Customer).ThenInclude(x => x.Bank)
                    .Where(x => x.Customer.BankId == bankId && x.Customer.Bank.CountryCode == "US"
                        && x.Status == AccountStatus.Open).ToListAsync(token);
                var destinations = await db.Accounts.Include(x => x.Customer).ThenInclude(x => x.Bank)
                    .Where(x => x.Customer.BankId != bankId && x.Customer.Bank.CountryCode == "US"
                        && x.Status == AccountStatus.Open).ToListAsync(token);
                var eligible = sources.Where(x => WireTraffic.Amount(x) >= 1m
                    && PaymentRiskPolicy.WithinFedwireLiquidity(x.Customer.Bank, WireTraffic.Amount(x))).ToList();
                if (eligible.Count > 0 && destinations.Count > 0)
                    db.WireTransfers.Add(WireTraffic.Create(eligible[Random.Shared.Next(eligible.Count)],
                        destinations[Random.Shared.Next(destinations.Count)], now));
                state.CurrentUtc = now.AddSeconds(60d / (double)Math.Clamp(state.SpeedMultiplier, 1m, 12m));
            }
            state.UpdatedDate = now;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });
    }
}
