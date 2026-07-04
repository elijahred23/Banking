using System.Security.Claims;
using System.Text.Json;
using Banking.Domain;
using Banking.Infrastructure;
using Banking.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class ReliabilityAndControlTests
{
    [Fact]
    public void Wire_state_machine_rejects_invalid_transitions_and_exposes_next_actions()
    {
        var wire = Wire(WireStatus.PendingApproval);
        Assert.Contains(WireStatus.Created, WireWorkflow.AllowedNext(wire.Status));
        Assert.Throws<InvalidOperationException>(() => WireWorkflow.Transition(wire, WireStatus.Settled));
        WireWorkflow.Transition(wire, WireStatus.Created);
        Assert.Equal(WireStatus.Created, wire.Status);
    }

    [Fact]
    public void Journal_control_requires_balanced_one_sided_lines()
    {
        Assert.True(JournalControl.IsBalanced(
            [new JournalLine(25, 0), new JournalLine(0, 25)]));
        Assert.False(JournalControl.IsBalanced(
            [new JournalLine(25, 0), new JournalLine(0, 24)]));
        Assert.Throws<InvalidOperationException>(() => JournalControl.RequireBalanced(
            [new JournalLine(10, 10), new JournalLine(0, 0)]));
    }

    [Fact]
    public void Customer_funds_post_only_at_acceptance_and_release_on_rejection()
    {
        var account = new Account { Id = Guid.NewGuid(), AccountNumber = "123", Balance = 100, HeldBalance = 25 };
        var accepted = Wire(WireStatus.PendingAtFed);
        accepted.FromAccountId = account.Id;
        WirePostingPolicy.SettleCustomerPayment(accepted, account);
        Assert.Equal(WireStatus.Settled, accepted.Status);
        Assert.Equal(75, account.Balance);
        Assert.Equal(0, account.HeldBalance);

        account.HeldBalance = 25;
        var rejected = Wire(WireStatus.SentToFed);
        rejected.FromAccountId = account.Id;
        WirePostingPolicy.RejectCustomerPayment(rejected, account);
        Assert.Equal(WireStatus.Rejected, rejected.Status);
        Assert.Equal(75, account.Balance);
        Assert.Equal(0, account.HeldBalance);
    }

    [Fact]
    public void Sql_model_contains_inbox_dead_letters_and_bank_scoped_retry_indexes()
    {
        var options = new DbContextOptionsBuilder<BankingDbContext>()
            .UseSqlServer("Server=localhost;Database=unused;User Id=unused;Password=unused;TrustServerCertificate=True")
            .Options;
        using var db = new BankingDbContext(options);
        var script = db.Database.GenerateCreateScript();
        Assert.Contains("CREATE TABLE [ConsumerInboxMessages]", script);
        Assert.Contains("CREATE TABLE [DeadLetterMessages]", script);
        Assert.Contains("IX_ConsumerInboxMessages_Queue_MessageId", script);
        Assert.Contains("IX_DeadLetterMessages_BankId_ReplayedDate_FailedDate", script);
    }

    [Fact]
    public async Task Inbox_completion_closes_the_commit_ack_crash_window_for_redelivery()
    {
        var factory = Factory();
        var bank = Bank();
        var wire = Wire(WireStatus.Created, bank.Id);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Banks.Add(bank);
            db.WireTransfers.Add(wire);
            await db.SaveChangesAsync();
        }
        var store = new MessageReliabilityStore(factory);
        var body = JsonSerializer.SerializeToUtf8Bytes(new WireCreated(wire.Id));
        var first = await store.BeginAttemptAsync(Queues.WireCreated, "stable-id", body, default);
        Assert.False(first.AlreadyCompleted);
        await store.CompleteAsync(first.InboxId, default);
        var redelivery = await store.BeginAttemptAsync(Queues.WireCreated, "stable-id", body, default);
        Assert.True(redelivery.AlreadyCompleted);
        Assert.Equal(1, redelivery.Attempts);
    }

    [Fact]
    public async Task Third_failure_is_durably_dead_lettered_with_bank_ownership()
    {
        var factory = Factory();
        var bank = Bank();
        var wire = Wire(WireStatus.Created, bank.Id);
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Banks.Add(bank);
            db.WireTransfers.Add(wire);
            await db.SaveChangesAsync();
        }
        var store = new MessageReliabilityStore(factory);
        var body = JsonSerializer.SerializeToUtf8Bytes(new WireCreated(wire.Id));
        for (var i = 1; i <= 3; i++)
        {
            var attempt = await store.BeginAttemptAsync(Queues.WireCreated, "failed-id", body, default);
            var dead = await store.FailAsync(attempt.InboxId, JsonSerializer.Serialize(new WireCreated(wire.Id)),
                new InvalidOperationException("simulated crash"), 3, default);
            Assert.Equal(i == 3, dead);
        }
        await using var verify = await factory.CreateDbContextAsync();
        var letter = await verify.DeadLetterMessages.SingleAsync();
        Assert.Equal(bank.Id, letter.BankId);
        Assert.Equal(3, letter.Attempts);
    }

    [Fact]
    public async Task Competing_wire_decisions_raise_a_concurrency_conflict()
    {
        var factory = Factory();
        var bank = Bank();
        var wire = Wire(WireStatus.PendingApproval, bank.Id);
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Banks.Add(bank);
            seed.WireTransfers.Add(wire);
            await seed.SaveChangesAsync();
        }
        await using var first = await factory.CreateDbContextAsync();
        await using var second = await factory.CreateDbContextAsync();
        var firstWire = await first.WireTransfers.SingleAsync();
        var secondWire = await second.WireTransfers.SingleAsync();
        WireWorkflow.Transition(firstWire, WireStatus.Created);
        WireWorkflow.Transition(secondWire, WireStatus.Rejected);
        await first.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public void Operations_controller_requires_the_operations_role_and_antiforgery_on_replay()
    {
        var controller = typeof(OperationsController);
        Assert.Equal("Operations", controller.GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Single().Roles);
        var replay = controller.GetMethod(nameof(OperationsController.ReplayDeadLetter))!;
        Assert.NotEmpty(replay.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true));
    }

    [Fact]
    public async Task Dead_letter_replay_cannot_cross_bank_scope()
    {
        var factory = Factory();
        var ownBank = Bank();
        var otherBank = Bank();
        var foreign = new DeadLetterMessage
        {
            BankId = otherBank.Id, Queue = Queues.WireCreated, MessageId = "foreign",
            Payload = "{}", Error = "failed", Attempts = 3
        };
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Banks.AddRange(ownBank, otherBank);
            db.DeadLetterMessages.Add(foreign);
            await db.SaveChangesAsync();
        }
        var controller = new OperationsController(factory, new EmptyQueueMonitor(),
            new PaymentReconciliationService(factory))
        {
            ControllerContext = new ControllerContext { HttpContext = HttpContext(ownBank.Id) }
        };
        var result = await controller.ReplayDeadLetter(foreign.Id, default);
        Assert.IsType<NotFoundResult>(result);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Empty(verify.OutboxMessages);
    }

    private static PooledDbContextFactory<BankingDbContext> Factory()
    {
        var options = new DbContextOptionsBuilder<BankingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new PooledDbContextFactory<BankingDbContext>(options);
    }

    private static DefaultHttpContext HttpContext(Guid bankId)
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("bank_id", bankId.ToString()), new Claim(ClaimTypes.Role, "Operations")], "test"));
        return context;
    }

    private static Bank Bank() => new()
    {
        Name = $"Bank {Guid.NewGuid():N}", RoutingNumber = Random.Shared.Next(100_000_000, 999_999_999).ToString(),
        FedParticipantId = Guid.NewGuid().ToString("N")[..12], Bic = Guid.NewGuid().ToString("N")[..11],
        TownName = "Tulsa", CountryCode = "US"
    };

    private static WireTransfer Wire(WireStatus status, Guid? bankId = null) => new()
    {
        BankId = bankId ?? Guid.NewGuid(), SenderBankId = bankId ?? Guid.NewGuid(), ReceiverBankId = Guid.NewGuid(),
        Direction = WireDirection.Outgoing, Amount = 25, Status = status, SenderName = "Sender",
        ReceiverName = "Receiver", BeneficiaryAccountNumber = "123", Scenario = ProcessingScenario.Standard,
        Rail = PaymentRail.Fedwire, TransferType = WireTransferType.CustomerCreditTransfer
    };

    private sealed class EmptyQueueMonitor : IQueueOperationsMonitor
    {
        public Task<QueueMonitorSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new QueueMonitorSnapshot(false, [], null));
    }
}
