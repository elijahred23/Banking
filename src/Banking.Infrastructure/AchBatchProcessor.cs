using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

// Shared by queue consumers and operational jobs. Caller owns the SQL transaction.
public sealed class AchBatchProcessor(NachaFileWriter writer)
{
    public async Task<BankReportRow?> PrepareAsync(BankingDbContext db, Guid bankId, Guid entryId,
        DateOnly processingDate, CancellationToken token)
    {
        var entry = await db.AchEntries.Include(x => x.OriginatingAccount).ThenInclude(x => x.Customer).Include(x => x.OriginatingBank)
            .SingleOrDefaultAsync(x => x.Id == entryId && x.OriginatingBankId == bankId, token);
        if (entry is null || entry.Status != AchEntryStatus.Entered || entry.ApprovedBy is null || entry.ApprovedDate is null) return null;

        var errors = AchRules.Validate(entry, processingDate).ToList();
        if (entry.OriginatingAccount.Customer.BankId != bankId)
            errors.Add(new(nameof(entry.OriginatingAccountId), "The originating account does not belong to this bank."));
        if (AchRules.IsCredit(entry.TransactionCode) && !AccountAvailabilityPolicy.CanDebit(entry.OriginatingAccount, entry.Amount))
            errors.Add(new(nameof(entry.Amount), "The originating account has insufficient available funds."));
        var pendingExposure = await db.AchEntries.Where(x => x.OriginatingBankId == entry.OriginatingBankId
                && x.Id != entry.Id && (x.Status == AchEntryStatus.Validated || x.Status == AchEntryStatus.Batched
                    || x.Status == AchEntryStatus.SentToOperator) && (x.TransactionCode == AchTransactionCode.CheckingCredit
                    || x.TransactionCode == AchTransactionCode.SavingsCredit)).SumAsync(x => x.Amount, token);
        if (AchRules.IsCredit(entry.TransactionCode)
            && !PaymentRiskPolicy.WithinAchExposure(entry.OriginatingBank, pendingExposure + entry.Amount))
            errors.Add(new(nameof(entry.Amount), "The entry would exceed the originating bank's teaching ACH exposure limit."));
        if (errors.Count != 0)
        {
            entry.Status = AchEntryStatus.Rejected;
            return new(entry.Id.ToString("N")[..12], "Rejected by ODFI", string.Join(" ", errors.Select(x => x.Message)), entry.Amount, entry.Id, "Ach");
        }

        if (AchRules.IsCredit(entry.TransactionCode)) entry.OriginatingAccount.HeldBalance += entry.Amount;
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
        return new(entry.Id.ToString("N")[..12], "Batched", $"Validated and grouped into batch {batch.Id}. Customer credit funds reserved; no settlement posting.", entry.Amount, entry.Id, "Ach");
    }

    public async Task<IReadOnlyList<BankReportRow>> CloseAsync(BankingDbContext db, Guid bankId,
        DateOnly businessDate, CancellationToken token)
    {
        var results = new List<BankReportRow>();
        if (!AchBusinessCalendar.IsBankingDay(businessDate)) return results;
        var batches = await db.AchBatches.Include(x => x.OriginatingBank).Include(x => x.Entries)
            .Where(x => x.OriginatingBankId == bankId && x.Status == "Open" && x.Entries.Count != 0 && x.EffectiveEntryDate <= businessDate)
            .OrderBy(x => x.EffectiveEntryDate).ToListAsync(token);
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
                batch.ServiceClassCode = AchRules.ServiceClassCode(batch.Entries);
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
            db.OutboxMessages.Add(new OutboxMessage { BankId = bankId, Queue = Queues.AchBatchReady,
                Payload = System.Text.Json.JsonSerializer.Serialize(new AchFileReady(file.Id)) });
            results.Add(new(file.Id.ToString("N")[..12], "File queued", $"Closed {file.Batches.Count} batch(es); saved NACHA file and original outbox event atomically. Acceptance and settlement remain downstream."));
        }
        return results;
    }
}
