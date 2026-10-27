using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class WireTrafficServiceTests
{
    [Fact]
    public async Task Ticks_persist_approval_event_and_enqueue_once_without_touching_other_banks()
    {
        var options = new DbContextOptionsBuilder<BankingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options;
        var factory = new Factory(options);
        var bankId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var generated = WireTraffic.Create(Account(bankId), Account(otherId), now);
        var other = WireTraffic.Create(Account(otherId), Account(bankId), now);
        await using (var db = factory.CreateDbContext())
        {
            db.SimulationStates.Add(new SimulationState { Name = WireTraffic.StateName(bankId),
                IsPaused = true, CurrentUtc = now.AddHours(1) });
            db.WireTransfers.AddRange(generated, other);
            await db.SaveChangesAsync();
        }
        var service = new WireTrafficService(factory);
        await service.TickAsync(bankId, default);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Empty(await db.OutboxMessages.ToListAsync());
            var state = await db.SimulationStates.SingleAsync();
            state.IsPaused = false;
            await db.SaveChangesAsync();
        }
        await service.TickAsync(bankId, default);
        await service.TickAsync(bankId, default);
        await using (var db = factory.CreateDbContext())
        {
            var wire = await db.WireTransfers.Include(x => x.Events).SingleAsync(x => x.Id == generated.Id);
            Assert.Equal(WireStatus.Created, wire.Status);
            Assert.Single(wire.Events, x => x.EventType == "Approved");
            Assert.Single(await db.OutboxMessages.ToListAsync());
            Assert.Equal(bankId, (await db.OutboxMessages.SingleAsync()).BankId);
            Assert.Equal(WireStatus.PendingApproval, (await db.WireTransfers.SingleAsync(x => x.Id == other.Id)).Status);
        }
    }

    private static Account Account(Guid bankId) => new()
    { AccountNumber = Guid.NewGuid().ToString("N"), Balance = 1000m,
        Customer = new Customer { BankId = bankId, Name = "Teaching customer" } };

    private sealed class Factory(DbContextOptions<BankingDbContext> options) : IDbContextFactory<BankingDbContext>
    {
        public BankingDbContext CreateDbContext() => new(options);
    }
}
