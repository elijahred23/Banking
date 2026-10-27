namespace Banking.Domain;

/// <summary>Builds an inspectable copy of a waiting batch. Never assigns durable trace numbers or changes a batch.</summary>
public sealed class NachaDraftPreview(NachaFileWriter writer)
{
    public string Create(Bank bank, AchBatch source, DateTimeOffset capturedAt)
    {
        if (source.OriginatingBankId != bank.Id || source.Entries.Count == 0
            || source.Entries.Any(x => x.OriginatingBankId != bank.Id))
            throw new InvalidOperationException("A preview requires a nonempty batch owned by the originating bank.");
        var batch = new AchBatch
        {
            Id = source.Id, OriginatingBankId = bank.Id, CompanyName = source.CompanyName,
            CompanyId = source.CompanyId, SecCode = source.SecCode, EffectiveEntryDate = source.EffectiveEntryDate,
            BatchNumber = 1, ServiceClassCode = AchRules.ServiceClassCode(source.Entries)
        };
        var sequence = 0;
        foreach (var entry in source.Entries.OrderBy(x => x.CreatedDate).ThenBy(x => x.Id))
            batch.Entries.Add(new AchEntry
            {
                Id = entry.Id, OriginatingBankId = bank.Id, CompanyName = entry.CompanyName,
                CompanyId = entry.CompanyId, SecCode = entry.SecCode, ReceiverName = entry.ReceiverName,
                ReceivingRoutingNumber = entry.ReceivingRoutingNumber, ReceivingAccountNumber = entry.ReceivingAccountNumber,
                TransactionCode = entry.TransactionCode, Amount = entry.Amount, EntryDescription = entry.EntryDescription,
                EffectiveEntryDate = entry.EffectiveEntryDate, Addenda05 = entry.Addenda05, CreatedDate = entry.CreatedDate,
                TraceNumber = bank.RoutingNumber[..8] + (++sequence).ToString("0000000")
            });
        return writer.Write(new AchFile
        {
            // Stable draft identifier helps compare downloads; the actual file gets its own ID at cutoff.
            Id = source.Id, OriginatingBankId = bank.Id, OriginatingBank = bank, CreatedDate = capturedAt,
            ImmediateDestinationRoutingNumber = "091000080", ImmediateOriginRoutingNumber = bank.RoutingNumber,
            Batches = [batch], Status = "Draft"
        });
    }
}
