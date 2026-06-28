using Banking.Domain;
using Banking.Infrastructure;
using Banking.Infrastructure.Checks;
using Microsoft.EntityFrameworkCore;

namespace Banking.CheckService;

public sealed class Worker(IMessageBus bus, IDbContextFactory<BankingDbContext> dbFactory,
    X937CashLetterWriter writer, ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        bus.ConsumeAsync<CheckDepositCreated>(Queues.CheckDepositCreated, ProcessAsync, stoppingToken),
        bus.ConsumeAsync<CheckOperatorResult>(Queues.CheckInbound, LogOperatorResultAsync, stoppingToken),
        bus.ConsumeAsync<CheckReturnFile>(Queues.CheckReturnInbound, LogReturnsAsync, stoppingToken),
        bus.ConsumeAsync<CheckSettlementNotice>(Queues.CheckSettled, LogSettlementAsync, stoppingToken));

    private async Task ProcessAsync(CheckDepositCreated command, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var deposit = await db.CheckDeposits.Include(x => x.DepositoryBank).Include(x => x.Images)
            .Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == command.CheckDepositId, token);
        if (deposit is null || deposit.Status != CheckDepositStatus.Captured) return;
        try
        {
            if (deposit.Scenario == CheckProcessingScenario.InvalidMicr)
                throw new ArgumentException("Simulated MICR recognition failure.");
            _ = MicrParser.Parse(deposit.RawMicrLine);
            if (deposit.Images.Count(x => x.Side == CheckImageSide.Front) != 1
                || deposit.Images.Count(x => x.Side == CheckImageSide.Back) != 1)
                throw new ArgumentException("Check must have exactly one front and one back image.");
            foreach (var image in deposit.Images)
                TiffImageValidator.Validate(image.FileName, image.ContentType, image.Content);
            if (deposit.Scenario == CheckProcessingScenario.PoorImageQuality)
                throw new ArgumentException("Simulated image quality analysis failed.");

            deposit.Status = CheckDepositStatus.Validated;
            deposit.Events.Add(Event("Validated", "MICR and TIFF image checks passed."));
            var cashLetter = new CheckCashLetter
            {
                DepositoryBankId = deposit.DepositoryBankId,
                DepositoryBank = deposit.DepositoryBank,
                DestinationRoutingNumber = "000000000",
                OriginRoutingNumber = deposit.DepositoryBank.RoutingNumber,
                FileIdModifier = "A",
                Status = "Ready"
            };
            cashLetter.Deposits.Add(deposit);
            cashLetter.RawX937Payload = writer.Write(cashLetter, [deposit]);
            deposit.ImageCashLetterPayload = cashLetter.RawX937Payload;
            deposit.Status = CheckDepositStatus.ImageCashLetterCreated;
            deposit.Events.Add(Event("ImageCashLetterCreated",
                "Simplified X9.37/X9.100-187-style image cash letter created."));
            db.CheckCashLetters.Add(cashLetter);
            cashLetter.Status = "SentToExchange";
            deposit.Status = CheckDepositStatus.SentToExchange;
            deposit.Events.Add(Event("SentToExchange",
                "Image cash letter sent to the simulated check image exchange."));
            await db.SaveChangesAsync(token);
            await bus.PublishAsync(Queues.CheckOutbound,
                new CheckCashLetterEnvelope(cashLetter.Id, deposit.DepositoryBankId,
                    cashLetter.RawX937Payload), token);
            logger.LogInformation("Created cash letter {CashLetterId} for check {CheckDepositId}",
                cashLetter.Id, deposit.Id);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            deposit.Status = CheckDepositStatus.Rejected;
            deposit.ReturnReason = ex.Message;
            deposit.Events.Add(Event("Rejected", ex.Message));
            await db.SaveChangesAsync(token);
            logger.LogWarning(ex, "Check deposit {CheckDepositId} failed validation", deposit.Id);
        }
    }

    private static CheckEvent Event(string type, string description) =>
        new() { EventType = type, Description = description };

    private Task LogOperatorResultAsync(CheckOperatorResult result, CancellationToken token)
    {
        logger.LogInformation("Check exchange result for {CashLetterId}: {Message}",
            result.CashLetterId, result.Message);
        return Task.CompletedTask;
    }

    private Task LogReturnsAsync(CheckReturnFile file, CancellationToken token)
    {
        logger.LogInformation("Received {Count} check return notifications for {CashLetterId}",
            file.Returns.Count, file.CashLetterId);
        return Task.CompletedTask;
    }

    private Task LogSettlementAsync(CheckSettlementNotice notice, CancellationToken token)
    {
        logger.LogInformation("Received settlement notice for {Count} checks in {CashLetterId}",
            notice.SettledCheckDepositIds.Count, notice.CashLetterId);
        return Task.CompletedTask;
    }
}
