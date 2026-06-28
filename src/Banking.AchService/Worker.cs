using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Banking.AchService;

public sealed class Worker(IMessageBus bus, IDbContextFactory<BankingDbContext> dbFactory,
    NachaFileWriter writer, IConfiguration configuration, ILogger<Worker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        bus.ConsumeAsync<AchEntryCreated>(Queues.AchEntryCreated, ValidateAndBatchAsync, stoppingToken),
        RunCutoffAsync(stoppingToken));

    private async Task ValidateAndBatchAsync(AchEntryCreated command, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var entry = await db.AchEntries.Include(x => x.OriginatingAccount)
            .SingleOrDefaultAsync(x => x.Id == command.EntryId, token);
        if (entry is null || entry.Status != AchEntryStatus.Entered) return;

        var error = entry.Amount <= 0 ? "Amount must be positive."
            : entry.EffectiveEntryDate < DateOnly.FromDateTime(DateTime.UtcNow.Date) ? "Effective date cannot be in the past."
            : !AbaRoutingNumberValidator.IsValid(entry.ReceivingRoutingNumber) ? "Invalid ACH routing number."
            : entry.SecCode == AchStandardEntryClass.Iat ? "IAT is reserved for a later lab slice."
            : entry.Purpose == AchPaymentPurpose.TaxPaymentEftps && (entry.SecCode != AchStandardEntryClass.Ccd || string.IsNullOrWhiteSpace(entry.Addenda05))
                ? "EFTPS-style payments require CCD and an addenda 05 record."
            : IsCredit(entry.TransactionCode) && entry.OriginatingAccount.AvailableBalance < entry.Amount
                ? "Insufficient originating account funds."
            : null;
        if (error is not null)
        {
            entry.Status = AchEntryStatus.Returned;
            entry.ReturnCode = !AbaRoutingNumberValidator.IsValid(entry.ReceivingRoutingNumber) ? "R13" : "R01";
            db.AchReturns.Add(new AchReturn { AchEntryId = entry.Id, ReturnCode = entry.ReturnCode, Reason = error });
            await db.SaveChangesAsync(token);
            return;
        }

        if (IsCredit(entry.TransactionCode)) entry.OriginatingAccount.HeldBalance += entry.Amount;
        entry.Status = AchEntryStatus.Validated;
        var batch = await db.AchBatches.Include(x => x.Entries).FirstOrDefaultAsync(x =>
            x.OriginatingBankId == entry.OriginatingBankId && x.Status == "Open"
            && x.SecCode == entry.SecCode && x.CompanyId == entry.CompanyId
            && x.EffectiveEntryDate == entry.EffectiveEntryDate, token);
        if (batch is null)
        {
            batch = new AchBatch { OriginatingBankId = entry.OriginatingBankId, SecCode = entry.SecCode,
                CompanyName = entry.CompanyName, CompanyId = entry.CompanyId,
                EffectiveEntryDate = entry.EffectiveEntryDate };
            db.AchBatches.Add(batch);
        }
        batch.Entries.Add(entry);
        entry.Status = AchEntryStatus.Batched;
        await db.SaveChangesAsync(token);
        logger.LogInformation("ACH entry {EntryId} added to batch {BatchId}", entry.Id, batch.Id);
    }

    private async Task RunCutoffAsync(CancellationToken token)
    {
        var seconds = Math.Max(1, configuration.GetValue("Ach:BatchCutoffSeconds", 5));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(token)) await CloseOpenBatchesAsync(token);
    }

    private async Task CloseOpenBatchesAsync(CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var batches = await db.AchBatches.Include(x => x.OriginatingBank).Include(x => x.Entries)
            .Where(x => x.Status == "Open" && x.Entries.Count != 0).OrderBy(x => x.EffectiveEntryDate).ToListAsync(token);
        foreach (var bankGroup in batches.GroupBy(x => x.OriginatingBankId))
        {
            var origin = bankGroup.First().OriginatingBank;
            var file = new AchFile { OriginatingBankId = origin.Id, OriginatingBank = origin,
                ImmediateDestinationRoutingNumber = "091000080", ImmediateOriginRoutingNumber = origin.RoutingNumber,
                Status = "Ready" };
            var number = 0;
            var sequence = 0;
            foreach (var batch in bankGroup)
            {
                batch.Status = "Closed";
                batch.BatchNumber = ++number;
                file.Batches.Add(batch);
                foreach (var entry in batch.Entries.OrderBy(x => x.CreatedDate))
                    entry.TraceNumber = origin.RoutingNumber[..8] + (++sequence).ToString("0000000");
            }
            file.RawNachaPayload = writer.Write(file);
            if (file.Batches.SelectMany(x => x.Entries).Any(x => x.Scenario == AchProcessingScenario.MalformedFile))
                file.RawNachaPayload = file.RawNachaPayload[..^1];
            else if (file.Batches.SelectMany(x => x.Entries).Any(x => x.Scenario == AchProcessingScenario.ControlTotalMismatch))
            {
                var lines = file.RawNachaPayload.Split(Environment.NewLine).ToArray();
                var index = Array.FindIndex(lines, x => x[0] == '9' && x.Any(c => c != '9'));
                lines[index] = lines[index].Remove(13, 8).Insert(13, "99999999");
                file.RawNachaPayload = string.Join(Environment.NewLine, lines);
            }
            db.AchFiles.Add(file);
            await db.SaveChangesAsync(token);
            await bus.PublishAsync(Queues.AchBatchReady, new AchFileReady(file.Id), token);
            logger.LogInformation("Closed {BatchCount} ACH batches into file {FileId}", file.Batches.Count, file.Id);
        }
    }

    private static bool IsCredit(AchTransactionCode code) => code is AchTransactionCode.CheckingCredit or AchTransactionCode.SavingsCredit;
}
