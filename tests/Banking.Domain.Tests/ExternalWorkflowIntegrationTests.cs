using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class ExternalWorkflowIntegrationTests
{
    [Fact, Trait("Category", "ExternalIntegration")]
    public async Task Sql_and_rabbit_delivery_persist_inbox_and_balanced_settlement_state()
    {
        if (Environment.GetEnvironmentVariable("BANKING_RUN_EXTERNAL_INTEGRATION") != "1") return;

        var database = $"BankingIntegration_{Guid.NewGuid():N}";
        var connection = $"Server=localhost,11433;Database={database};User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connection,
            ["RabbitMq:HostName"] = "localhost",
            ["RabbitMq:UserName"] = "taskwrapper",
            ["RabbitMq:Password"] = "taskwrapper123"
        }).Build();
        var serviceCollection = new ServiceCollection().AddLogging();
        serviceCollection.AddSingleton<IConfiguration>(configuration);
        var services = serviceCollection.AddBankingInfrastructure(configuration).BuildServiceProvider();
        try
        {
            await services.InitializeDatabaseAsync();
            var factory = services.GetRequiredService<IDbContextFactory<BankingDbContext>>();
            var bank = new Bank
            {
                Name = "Integration Bank", RoutingNumber = "101000019", FedParticipantId = "INTEGRATION",
                Bic = "INTGUS44XXX", TownName = "Tulsa", CountryCode = "US"
            };
            var account = new Account { AccountNumber = "integration", Balance = 100, HeldBalance = 25 };
            bank.Customers.Add(new Customer { Name = "Test Customer", Accounts = [account] });
            var wire = new WireTransfer
            {
                BankId = bank.Id, SenderBankId = bank.Id, ReceiverBankId = Guid.NewGuid(),
                FromAccountId = account.Id, Direction = WireDirection.Outgoing, Amount = 25,
                Status = WireStatus.PendingAtFed, SenderName = "Test Customer", ReceiverName = "Receiver",
                BeneficiaryAccountNumber = "987", Scenario = ProcessingScenario.Standard,
                Rail = PaymentRail.Fedwire, TransferType = WireTransferType.CustomerCreditTransfer
            };
            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Banks.Add(bank);
                db.WireTransfers.Add(wire);
                await db.SaveChangesAsync();
            }

            var bus = services.GetRequiredService<IMessageBus>();
            var queue = $"integration.{Guid.NewGuid():N}";
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var consumer = bus.ConsumeAsync<WireCreated>(queue, (message, _) =>
            {
                Assert.Equal(wire.Id, message.WireId);
                received.TrySetResult();
                return Task.CompletedTask;
            }, cancellation.Token);
            await Task.Delay(300, cancellation.Token);
            await bus.PublishAsync(queue, new WireCreated(wire.Id), cancellation.Token, "integration-message");
            await received.Task.WaitAsync(cancellation.Token);

            await using (var db = await factory.CreateDbContextAsync(cancellation.Token))
            {
                var persistedWire = await db.WireTransfers.SingleAsync(x => x.Id == wire.Id, cancellation.Token);
                var persistedAccount = await db.Accounts.SingleAsync(x => x.Id == account.Id, cancellation.Token);
                WirePostingPolicy.SettleCustomerPayment(persistedWire, persistedAccount);
                var journal = Guid.NewGuid();
                db.LedgerEntries.AddBalanced(
                    new LedgerEntry { JournalId = journal, WireTransferId = wire.Id, AccountCode = "CUSTOMER",
                        AccountName = "Customer", Debit = 25, Description = "Customer debit" },
                    new LedgerEntry { JournalId = journal, WireTransferId = wire.Id, AccountCode = "FEDMASTER",
                        AccountName = "Settlement", Credit = 25, Description = "Settlement credit" });
                await db.SaveChangesAsync(cancellation.Token);
            }

            await Task.Delay(200, cancellation.Token);
            await using (var verify = await factory.CreateDbContextAsync(cancellation.Token))
            {
                Assert.Equal(75, (await verify.Accounts.SingleAsync(x => x.Id == account.Id, cancellation.Token)).Balance);
                Assert.NotNull((await verify.ConsumerInboxMessages.SingleAsync(x => x.MessageId == "integration-message", cancellation.Token)).CompletedDate);
                var lines = await verify.LedgerEntries.Where(x => x.WireTransferId == wire.Id).ToListAsync(cancellation.Token);
                Assert.True(JournalControl.IsBalanced(lines.Select(x => new JournalLine(x.Debit, x.Credit))));
            }
            cancellation.Cancel();
            try { await consumer; } catch (OperationCanceledException) { }
        }
        finally
        {
            var factory = services.GetRequiredService<IDbContextFactory<BankingDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.EnsureDeletedAsync();
            await services.DisposeAsync();
        }
    }
}
