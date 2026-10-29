using System.Text.Json;
using Banking.Domain;
using Banking.Infrastructure;
using Banking.Infrastructure.Checks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class BackendRefactoringTests
{
    [Theory]
    [InlineData("101000019", true, true)]
    [InlineData("101000010", false, false)]
    [InlineData("990000000", true, false)]
    [InlineData("123", false, false)]
    public void Shared_checksum_preserves_rail_specific_validation(string routing, bool check, bool ach)
    {
        Assert.Equal(check, AbaRoutingValidator.IsValid(routing));
        Assert.Equal(ach, AbaRoutingNumberValidator.IsValid(routing));
    }

    [Fact]
    public void Traffic_state_names_keep_bank_and_rail_boundaries()
    {
        var bankId = Guid.NewGuid();
        Assert.True(WireTraffic.TryGetBankId(WireTraffic.StateName(bankId), out var wireBank));
        Assert.Equal(bankId, wireBank);
        Assert.True(AchTraffic.TryGetBankId($"{AchTraffic.BankPrefix(bankId)}Payroll", out var achBank));
        Assert.Equal(bankId, achBank);
        Assert.False(WireTraffic.TryGetBankId($"{AchTraffic.BankPrefix(bankId)}Payroll", out _));
        Assert.False(AchTraffic.TryGetBankId(WireTraffic.StateName(bankId), out _));
        Assert.False(WireTraffic.TryGetBankId($"{WireTraffic.StateName(bankId)}:Payroll", out _));
        Assert.False(AchTraffic.TryGetBankId($"{AchTraffic.Prefix}invalid:Payroll", out _));
    }

    [Fact]
    public async Task Outbox_failure_is_durable_and_retry_reuses_transport_identity()
    {
        var factory = new TestFactory();
        var failed = Outbox.Message(Queues.WireCreated, new WireCreated(Guid.NewGuid()), Guid.NewGuid());
        var healthy = Outbox.Message(Queues.WireCreated, new WireCreated(Guid.NewGuid()));
        var deferred = Outbox.Message(Queues.WireCreated, new WireCreated(Guid.NewGuid()));
        deferred.NextAttemptDate = DateTimeOffset.UtcNow.AddHours(1);
        await using (var db = factory.CreateDbContext())
        {
            db.OutboxMessages.AddRange(failed, healthy, deferred);
            await db.SaveChangesAsync();
        }
        var bus = new RecordingBus { FailingMessageId = failed.Id.ToString("N") };
        var dispatcher = new OutboxDispatcher(factory, bus, NullLogger<OutboxDispatcher>.Instance);
        await dispatcher.PublishBatchAsync(default);
        await using (var db = factory.CreateDbContext())
        {
            var retry = await db.OutboxMessages.FindAsync(failed.Id);
            Assert.NotNull(retry);
            Assert.Null(retry.PublishedDate);
            Assert.Equal(1, retry.Attempts);
            Assert.Equal(1000, retry.LastError!.Length);
            Assert.True(retry.NextAttemptDate > DateTimeOffset.UtcNow);
            Assert.NotNull((await db.OutboxMessages.FindAsync(healthy.Id))!.PublishedDate);
            Assert.Null((await db.OutboxMessages.FindAsync(deferred.Id))!.PublishedDate);
            retry.NextAttemptDate = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        bus.FailingMessageId = null;
        await dispatcher.PublishBatchAsync(default);
        await using (var db = factory.CreateDbContext())
        {
            var published = (await db.OutboxMessages.FindAsync(failed.Id))!;
            Assert.NotNull(published.PublishedDate);
            Assert.Null(published.LastError);
            Assert.Null(published.NextAttemptDate);
            Assert.Equal(failed.BankId, published.BankId);
        }
        var deliveries = bus.Deliveries.Where(x => x.Id == failed.Id.ToString("N")).ToList();
        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, x => Assert.Equal(failed.Payload, x.Payload));
        Assert.Single(bus.Deliveries.Where(x => x.Id == healthy.Id.ToString("N")));
        Assert.DoesNotContain(bus.Deliveries, x => x.Id == deferred.Id.ToString("N"));
    }

    [Fact]
    public async Task Outbox_cancellation_does_not_become_a_failed_delivery()
    {
        var factory = new TestFactory();
        var item = Outbox.Message(Queues.WireCreated, new WireCreated(Guid.NewGuid()));
        await using (var db = factory.CreateDbContext())
        {
            db.OutboxMessages.Add(item);
            await db.SaveChangesAsync();
        }
        var dispatcher = new OutboxDispatcher(factory, new RecordingBus { Cancel = true },
            NullLogger<OutboxDispatcher>.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.PublishBatchAsync(default));
        await using var persisted = factory.CreateDbContext();
        var pending = (await persisted.OutboxMessages.FindAsync(item.Id))!;
        Assert.Equal(0, pending.Attempts);
        Assert.Null(pending.PublishedDate);
        Assert.Null(pending.LastError);
    }

    private sealed class TestFactory : IDbContextFactory<BankingDbContext>
    {
        private readonly DbContextOptions<BankingDbContext> options =
            new DbContextOptionsBuilder<BankingDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        public BankingDbContext CreateDbContext() => new(options);
    }

    private sealed class RecordingBus : IMessageBus
    {
        public string? FailingMessageId { get; set; }
        public bool Cancel { get; init; }
        public List<(string? Id, string Payload)> Deliveries { get; } = [];

        public Task PublishAsync<T>(string queue, T message, CancellationToken cancellationToken = default,
            string? messageId = null)
        {
            if (Cancel) throw new OperationCanceledException();
            Deliveries.Add((messageId, JsonSerializer.Serialize(message)));
            if (messageId == FailingMessageId) throw new InvalidOperationException(new string('x', 1100));
            return Task.CompletedTask;
        }

        public Task ConsumeAsync<T>(string queue, Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
