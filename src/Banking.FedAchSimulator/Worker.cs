using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.FedAchSimulator;

public sealed class Worker(IMessageBus bus, IDbContextFactory<BankingDbContext> dbFactory,
    NachaFileParser parser, ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        bus.ConsumeAsync<AchFileEnvelope>(Queues.AchOutbound, ProcessAsync, stoppingToken);

    private async Task ProcessAsync(AchFileEnvelope envelope, CancellationToken token)
    {
        var validation = parser.Validate(envelope.NachaPayload);
        if (!validation.IsValid)
        {
            await bus.PublishAsync(Queues.AchInbound,
                new AchOperatorResult(envelope.FileId, false, string.Join(" ", validation.Errors), []), token);
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var file = await db.AchFiles.Include(x => x.Batches).ThenInclude(x => x.Entries)
            .AsNoTracking().SingleOrDefaultAsync(x => x.Id == envelope.FileId, token);
        if (file is null) return;
        var settled = new List<Guid>();
        var returns = new List<AchReturnItem>();
        var nocs = new List<AchNocItem>();
        foreach (var entry in file.Batches.SelectMany(x => x.Entries))
        {
            var account = await db.Accounts.Include(x => x.Customer).FirstOrDefaultAsync(x =>
                x.Customer.Bank.RoutingNumber == entry.ReceivingRoutingNumber
                && x.AccountNumber == entry.ReceivingAccountNumber, token);
            var item = ReturnFor(entry, account);
            if (item is not null) returns.Add(item);
            else
            {
                settled.Add(entry.Id);
                if (entry.Scenario == AchProcessingScenario.NocCorrectedAccount)
                    nocs.Add(new(entry.Id, "C01", entry.ReceivingAccountNumber.TrimStart('0'), "Corrected account number."));
                if (entry.Scenario == AchProcessingScenario.NocCorrectedRouting)
                    nocs.Add(new(entry.Id, "C02", entry.ReceivingRoutingNumber, "Corrected routing number."));
                if (entry.Scenario == AchProcessingScenario.NocCorrectedAccountAndRouting)
                    nocs.Add(new(entry.Id, "C03", entry.ReceivingRoutingNumber + entry.ReceivingAccountNumber,
                        "Corrected routing and account numbers."));
            }
        }
        await bus.PublishAsync(Queues.AchInbound,
            new AchOperatorResult(file.Id, true, "FedACH simulator accepted and settled the file.", settled), token);
        if (returns.Count != 0 || nocs.Count != 0) await Task.Delay(TimeSpan.FromSeconds(1), token);
        if (returns.Count != 0) await bus.PublishAsync(Queues.AchReturnInbound, new AchReturnFile(file.Id, returns), token);
        if (nocs.Count != 0) await bus.PublishAsync(Queues.AchNocInbound, new AchNocFile(file.Id, nocs), token);
        logger.LogInformation("FedACH processed {FileId}: {Settled} settled, {Returns} returned", file.Id, settled.Count, returns.Count);
    }

    private static AchReturnItem? ReturnFor(AchEntry entry, Account? account)
    {
        if (entry.Scenario == AchProcessingScenario.InvalidRoutingNumber) return new(entry.Id, "R13", "Invalid ACH routing number.");
        if (entry.Scenario == AchProcessingScenario.AccountClosed) return new(entry.Id, "R02", "Account closed.");
        if (entry.Scenario == AchProcessingScenario.InvalidAccountNumber) return new(entry.Id, "R04", "Invalid account number structure.");
        if (entry.Scenario == AchProcessingScenario.AccountNotFound) return new(entry.Id, "R03", "No account/unable to locate account.");
        if (entry.Scenario == AchProcessingScenario.InsufficientFunds) return new(entry.Id, "R01", "Insufficient funds.");
        if (entry.Purpose == AchPaymentPurpose.TaxPaymentEftps) return null;
        if (account is null) return new(entry.Id, "R03", "No account/unable to locate account.");
        if (entry.TransactionCode is AchTransactionCode.CheckingDebit or AchTransactionCode.SavingsDebit
            && account.AvailableBalance < entry.Amount) return new(entry.Id, "R01", "Insufficient funds.");
        return null;
    }
}
