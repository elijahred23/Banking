using Banking.Domain;
using Banking.FedwireSimulator;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class FedwireSimulatorTests
{
    [Theory]
    [InlineData(true, 100, 0, 100, ProcessingScenario.Standard, true)]
    [InlineData(true, 90, 10, 100, ProcessingScenario.Standard, true)]
    [InlineData(true, 90, 0, 100, ProcessingScenario.Standard, false)]
    [InlineData(true, 100, 0, 0, ProcessingScenario.Standard, false)]
    [InlineData(true, 100, 0, -1, ProcessingScenario.Standard, false)]
    [InlineData(false, 100, 0, 25, ProcessingScenario.Standard, false)]
    [InlineData(true, 100, 0, 25, ProcessingScenario.FedRejects, false)]
    public void Acceptance_requires_valid_xml_positive_amount_liquidity_and_allowed_scenario(
        bool validXml, decimal balance, decimal credit, decimal amount, ProcessingScenario scenario, bool expected)
    {
        var bank = new Bank { Name = "Test", RoutingNumber = "101000019", FedParticipantId = "TEST",
            Bic = "TESTUS44XXX", TownName = "Tulsa", CountryCode = "US",
            MasterAccountBalance = balance, FedwireCreditLimit = credit };
        Assert.Equal(expected, FedwirePaymentDecision.IsAccepted(validXml, bank, amount, scenario));
    }

    [Theory]
    [InlineData(ProcessingScenario.Standard, "ACSC", "Status:ACSC,Payment:ACSC")]
    [InlineData(ProcessingScenario.DuplicateDelivery, "ACSC", "Status:ACSC,Payment:ACSC,Status:ACSC,Payment:ACSC")]
    [InlineData(ProcessingScenario.OutOfOrderDelivery, "ACSC", "Status:ACSC,Payment:ACSC,Status:PDNG")]
    [InlineData(ProcessingScenario.PendingThenAccepted, "ACSC", "Status:PDNG,Status:ACSC,Payment:ACSC")]
    [InlineData(ProcessingScenario.DroppedDelivery, "ACSC", "Status:PDNG,Status:ACSC,Payment:ACSC")]
    [InlineData(ProcessingScenario.DuplicateDelivery, "RJCT", "Status:RJCT")]
    [InlineData(ProcessingScenario.OutOfOrderDelivery, "RJCT", "Status:RJCT")]
    [InlineData(ProcessingScenario.PendingThenAccepted, "RJCT", "Status:RJCT")]
    public async Task Delivery_scenarios_preserve_status_order_and_only_deliver_accepted_payments(
        ProcessingScenario scenario, string status, string expected)
    {
        var payment = Payment(scenario);
        var settlement = new StubSettlement(status);
        var bus = new RecordingBus();
        await Processor(settlement, bus).ProcessAsync(payment, default);
        Assert.Equal(1, settlement.Calls);
        Assert.Equal(expected, string.Join(',', bus.Messages.Select(x => $"{x.Kind}:{x.StatusCode}")));
        Assert.All(bus.Messages, message =>
        {
            Assert.Equal(payment.CorrelationId, message.CorrelationId);
            Assert.Equal("IMAD", message.Imad);
            Assert.Equal("OMAD", message.Omad);
        });
    }

    [Theory]
    [InlineData(FedMessageKind.Status, PaymentRail.Fedwire)]
    [InlineData(FedMessageKind.Payment, PaymentRail.FedNow)]
    [InlineData(FedMessageKind.Payment, PaymentRail.SwiftCbprPlus)]
    public async Task Unrelated_messages_are_ignored(FedMessageKind kind, PaymentRail rail)
    {
        var settlements = new StubSettlement("ACSC");
        var bus = new RecordingBus();
        await Processor(settlements, bus).ProcessAsync(Payment() with { Kind = kind, Rail = rail }, default);
        Assert.Equal(0, settlements.Calls);
        Assert.Empty(bus.Messages);
    }

    [Fact]
    public async Task Institution_payment_status_keeps_original_iso_definition()
    {
        var bus = new RecordingBus();
        await Processor(new StubSettlement("ACSC"), bus).ProcessAsync(
            Payment() with { XmlPayload = "pacs.009.001.08" }, default);
        Assert.Contains("<OrgnlMsgNmId>pacs.009.001.08</OrgnlMsgNmId>", bus.Messages[0].XmlPayload);
    }

    [Fact]
    public async Task Failed_settlement_does_not_publish_acceptance()
    {
        var bus = new RecordingBus();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Processor(new FailedSettlement(), bus).ProcessAsync(Payment(), default));
        Assert.Empty(bus.Messages);
    }

    [Fact, Trait("Category", "ExternalIntegration")]
    public async Task Sql_replay_reuses_settlement_without_moving_money_again()
    {
        if (Environment.GetEnvironmentVariable("BANKING_RUN_EXTERNAL_INTEGRATION") != "1") return;
        var database = $"BankingFedwireTest_{Guid.NewGuid():N}";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = $"Server=localhost,11433;Database={database};User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True"
        }).Build();
        await using var services = new ServiceCollection().AddLogging()
            .AddBankingInfrastructure(configuration).BuildServiceProvider();
        var factory = services.GetRequiredService<IDbContextFactory<BankingDbContext>>();
        try
        {
            await services.InitializeDatabaseAsync();
            var sender = new Bank { Name = "Sender", RoutingNumber = "101000019", Bic = "SENDUS44XXX",
                FedParticipantId = "SENDER", TownName = "Tulsa", CountryCode = "US", MasterAccountBalance = 100 };
            var receiver = new Bank { Name = "Receiver", RoutingNumber = "103000648", Bic = "RECVUS44XXX",
                FedParticipantId = "RECEIVER", TownName = "Tulsa", CountryCode = "US", MasterAccountBalance = 50 };
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Banks.AddRange(sender, receiver);
                await db.SaveChangesAsync();
            }
            var iso = new IsoMessageService();
            var wire = new WireTransfer { Amount = 25, SenderName = "Sender", ReceiverName = "Receiver",
                BeneficiaryAccountNumber = "654321" };
            var payment = Payment() with { CorrelationId = wire.CorrelationId,
                SenderBankId = sender.Id, ReceiverBankId = receiver.Id,
                XmlPayload = iso.CreatePacs008(wire, sender, receiver, "123456", "654321") };
            var service = new FedwireSettlementService(factory, iso);
            var first = await service.SettleAsync(payment, default);
            var replay = await service.SettleAsync(payment, default);
            Assert.Equal("ACSC", first.StatusCode);
            Assert.Equal(first.Id, replay.Id);
            Assert.Equal(first.Imad, replay.Imad);
            await using var verification = await factory.CreateDbContextAsync();
            Assert.Equal(75m, (await verification.Banks.FindAsync(sender.Id))!.MasterAccountBalance);
            Assert.Equal(75m, (await verification.Banks.FindAsync(receiver.Id))!.MasterAccountBalance);
            Assert.Single(await verification.FedSettlements.ToListAsync());
            var receipts = await verification.BalanceReceipts.ToListAsync();
            Assert.Equal(2, receipts.Count);
            Assert.Equal(0m, receipts.Sum(x => x.Delta));
            var rejected = await service.SettleAsync(payment with { CorrelationId = Guid.NewGuid(),
                Scenario = ProcessingScenario.FedRejects }, default);
            Assert.Equal("RJCT", rejected.StatusCode);
            Assert.All(await verification.Banks.AsNoTracking().Select(x => x.MasterAccountBalance).ToListAsync(),
                balance => Assert.Equal(75m, balance));
            Assert.Equal(2, await verification.BalanceReceipts.CountAsync());
        }
        finally
        {
            await using var cleanup = await factory.CreateDbContextAsync();
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    private static FedEnvelope Payment(ProcessingScenario scenario = ProcessingScenario.Standard) =>
        new(FedMessageKind.Payment, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            25, "payment", Scenario: scenario);

    private static FedwirePaymentProcessor Processor(IFedwireSettlementService service, RecordingBus bus) =>
        new(service, new FedwireResponsePublisher(bus, new IsoMessageService()),
            NullLogger<FedwirePaymentProcessor>.Instance);

    private sealed class StubSettlement(string status) : IFedwireSettlementService
    {
        public int Calls { get; private set; }
        public Task<FedSettlement> SettleAsync(FedEnvelope payment, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new FedSettlement { CorrelationId = payment.CorrelationId,
                StatusCode = status, Imad = "IMAD", Omad = "OMAD" });
        }
    }

    private sealed class FailedSettlement : IFedwireSettlementService
    {
        public Task<FedSettlement> SettleAsync(FedEnvelope payment, CancellationToken token) =>
            throw new InvalidOperationException("Settlement unavailable");
    }

    private sealed class RecordingBus : IMessageBus
    {
        public List<FedEnvelope> Messages { get; } = [];
        public Task PublishAsync<T>(string queue, T message, CancellationToken cancellationToken = default,
            string? messageId = null)
        {
            Assert.Equal(Queues.FedInbound, queue);
            Messages.Add(Assert.IsType<FedEnvelope>(message));
            return Task.CompletedTask;
        }
        public Task ConsumeAsync<T>(string queue, Func<T, CancellationToken, Task> handler,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
