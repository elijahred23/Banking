using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class WireLiquidityQueueTests
{
    [Fact]
    public async Task Waiting_wire_releases_once_after_funding_with_atomic_hold_and_outbox()
    {
        var factory = new Factory(new DbContextOptionsBuilder<BankingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var sender = Bank("Origin", "021000021");
        var receiver = Bank("Destination", "011000015");
        var account = new Account { AccountNumber = "12345678", Balance = 50,
            Customer = new Customer { BankId = sender.Id, Name = "Sender" } };
        var wire = new WireTransfer { BankId = sender.Id, SenderBankId = sender.Id,
            ReceiverBankId = receiver.Id, FromAccountId = account.Id, Amount = 100,
            SenderName = "Sender", ReceiverName = "Receiver", BeneficiaryAccountNumber = "87654321",
            Rail = PaymentRail.Fedwire, Status = WireStatus.Created, ComplianceStatus = "ClearedByOfficer" };
        await using (var db = factory.CreateDbContext())
        {
            db.Banks.AddRange(sender, receiver);
            db.Accounts.Add(account);
            db.WireTransfers.Add(wire);
            await db.SaveChangesAsync();
        }
        var bus = new Bus();
        var iso = new IsoMessageService();
        using var worker = new Banking.WireService.Worker(bus, factory, iso,
            new CbprPlusMessageService(iso), new PaymentRouteResolver(factory),
            new WireComplianceService(), NullLogger<Banking.WireService.Worker>.Instance);
        await worker.StartAsync(default);
        await bus.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await bus.Handler!(new WireCreated(wire.Id), default);
        await bus.Handler(new WireCreated(wire.Id), default);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(WireStatus.Created, (await db.WireTransfers.FindAsync(wire.Id))!.Status);
            Assert.Single(await db.WireEvents.Where(x => x.EventType == "AwaitingLiquidity").ToListAsync());
            Assert.Empty(await db.OutboxMessages.ToListAsync());
            var funded = (await db.Accounts.FindAsync(account.Id))!;
            Assert.Equal(0, funded.HeldBalance);
            funded.Balance = 150;
            await db.SaveChangesAsync();
        }
        // No new transport message: the durable waiting scan must discover incoming funding.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (true)
        {
            await using var check = factory.CreateDbContext();
            if ((await check.WireTransfers.FindAsync(wire.Id))!.Status == WireStatus.ReadyForFed) break;
            await Task.Delay(100, timeout.Token);
        }
        await bus.Handler(new WireCreated(wire.Id), default);
        await using (var db = factory.CreateDbContext())
        {
            Assert.Equal(WireStatus.ReadyForFed, (await db.WireTransfers.FindAsync(wire.Id))!.Status);
            var funded = (await db.Accounts.FindAsync(account.Id))!;
            Assert.Equal(150, funded.Balance);
            Assert.Equal(100, funded.HeldBalance);
            Assert.Single(await db.IsoMessages.ToListAsync());
            Assert.Equal(3, await db.OutboxMessages.CountAsync());
            Assert.Empty(await db.LedgerEntries.ToListAsync());
        }
        await worker.StopAsync(default);
    }

    private static Bank Bank(string name, string routing) => new() { Name = name,
        RoutingNumber = routing, FedParticipantId = routing, Bic = "TESTUS33XXX",
        TownName = "Tulsa", CountryCode = "US", MasterAccountBalance = 1_000_000 };

    private sealed class Factory(DbContextOptions<BankingDbContext> options) : IDbContextFactory<BankingDbContext>
    {
        public BankingDbContext CreateDbContext() => new(options);
    }

    private sealed class Bus : IMessageBus
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<WireCreated, CancellationToken, Task>? Handler { get; private set; }
        public Task PublishAsync<T>(string queue, T message, CancellationToken cancellationToken = default,
            string? messageId = null) => throw new InvalidOperationException("Use the durable outbox.");
        public Task ConsumeAsync<T>(string queue, Func<T, CancellationToken, Task> handler, CancellationToken ct)
        {
            Handler = (message, token) => handler((T)(object)message, token);
            Ready.SetResult();
            return Task.Delay(Timeout.Infinite, ct);
        }
    }
}
