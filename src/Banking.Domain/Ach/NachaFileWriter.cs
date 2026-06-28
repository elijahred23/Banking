using System.Globalization;

namespace Banking.Domain;

public sealed class NachaFileWriter
{
    public string Write(AchFile file)
    {
        if (file.Batches.Count == 0) throw new ArgumentException("An ACH file needs at least one batch.");
        var lines = new List<string> { WriteFileHeader(file) };
        foreach (var batch in file.Batches.OrderBy(x => x.BatchNumber))
        {
            lines.Add(WriteBatchHeader(batch, file));
            var entrySequence = 0;
            foreach (var entry in batch.Entries.OrderBy(x => x.CreatedDate))
            {
                entrySequence++;
                lines.Add(WriteEntryDetail(entry));
                if (!string.IsNullOrWhiteSpace(entry.Addenda05))
                    lines.Add(WriteAddenda05(entry.Addenda05, entrySequence, entry.TraceNumber));
            }
            lines.Add(WriteBatchControl(batch, file));
        }
        lines.Add(WriteFileControl(file, lines.Count + 1));
        while (lines.Count % 10 != 0) lines.Add(new string('9', 94));
        if (lines.Any(x => x.Length != 94)) throw new InvalidOperationException("Every NACHA record must be 94 characters.");
        return string.Join(Environment.NewLine, lines);
    }

    private static string WriteFileHeader(AchFile file) => Record(
        "1", "01", $" {Digits(file.ImmediateDestinationRoutingNumber, 9)}",
        $" {Digits(file.ImmediateOriginRoutingNumber, 9)}", file.CreatedDate.ToString("yyMMdd"),
        file.CreatedDate.ToString("HHmm"), Fit(file.FileIdModifier, 1), "094", "10", "1",
        Fit("FEDACH SIMULATOR", 23), Fit(file.OriginatingBank?.Name ?? "ORIGINATING BANK", 23), Fit(file.Id.ToString("N"), 8));

    private static string WriteBatchHeader(AchBatch batch, AchFile file) => Record(
        "5", Digits(batch.ServiceClassCode, 3), Fit(batch.CompanyName, 16), new string(' ', 20),
        Fit(batch.CompanyId, 10), Fit(batch.SecCode.ToString().ToUpperInvariant(), 3),
        Fit(batch.Entries.FirstOrDefault()?.EntryDescription ?? "PAYMENT", 10), new string(' ', 6),
        batch.EffectiveEntryDate.ToString("yyMMdd", CultureInfo.InvariantCulture), new string(' ', 3), "1",
        Digits(file.ImmediateOriginRoutingNumber, 9)[..8], Number(batch.BatchNumber, 7));

    private static string WriteEntryDetail(AchEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.TraceNumber) || entry.TraceNumber.Length != 15)
            throw new ArgumentException($"ACH entry {entry.Id} needs a 15-digit trace number.");
        return Record("6", ((int)entry.TransactionCode).ToString("00"),
            Digits(entry.ReceivingRoutingNumber, 9)[..8], Digits(entry.ReceivingRoutingNumber, 9)[8..],
            Fit(entry.ReceivingAccountNumber, 17), Amount(entry.Amount, 10),
            Fit(entry.Id.ToString("N"), 15), Fit(entry.ReceiverName, 22), "  ",
            string.IsNullOrWhiteSpace(entry.Addenda05) ? "0" : "1", Digits(entry.TraceNumber, 15));
    }

    private static string WriteAddenda05(string addenda, int sequence, string? trace) => Record(
        "7", "05", Fit(addenda, 80), Number(sequence, 4), Digits(trace ?? "", 15)[^7..]);

    private static string WriteBatchControl(AchBatch batch, AchFile file)
    {
        var debit = batch.Entries.Where(IsDebit).Sum(x => x.Amount);
        var credit = batch.Entries.Where(x => !IsDebit(x)).Sum(x => x.Amount);
        var count = batch.Entries.Count + batch.Entries.Count(x => !string.IsNullOrWhiteSpace(x.Addenda05));
        return Record("8", Digits(batch.ServiceClassCode, 3), Number(count, 6),
            RoutingHash(batch.Entries), Amount(debit, 12), Amount(credit, 12), Fit(batch.CompanyId, 10),
            new string(' ', 19), new string(' ', 6), Digits(file.ImmediateOriginRoutingNumber, 9)[..8],
            Number(batch.BatchNumber, 7));
    }

    private static string WriteFileControl(AchFile file, int recordsBeforePadding)
    {
        var entries = file.Batches.SelectMany(x => x.Entries).ToList();
        var entryAddendaCount = entries.Count + entries.Count(x => !string.IsNullOrWhiteSpace(x.Addenda05));
        var blockCount = (int)Math.Ceiling(recordsBeforePadding / 10m);
        return Record("9", Number(file.Batches.Count, 6), Number(blockCount, 6), Number(entryAddendaCount, 8),
            RoutingHash(entries), Amount(entries.Where(IsDebit).Sum(x => x.Amount), 12),
            Amount(entries.Where(x => !IsDebit(x)).Sum(x => x.Amount), 12), new string(' ', 39));
    }

    private static bool IsDebit(AchEntry entry) => entry.TransactionCode is AchTransactionCode.CheckingDebit or AchTransactionCode.SavingsDebit;
    private static string RoutingHash(IEnumerable<AchEntry> entries) => Number(entries.Sum(x => long.Parse(Digits(x.ReceivingRoutingNumber, 9)[..8], CultureInfo.InvariantCulture)) % 10_000_000_000L, 10);
    private static string Amount(decimal amount, int length) => Number(decimal.ToInt64(decimal.Round(amount * 100, 0, MidpointRounding.AwayFromZero)), length);
    private static string Number(long value, int length) => value.ToString(CultureInfo.InvariantCulture).PadLeft(length, '0')[^length..];
    private static string Digits(string value, int length) => new string((value ?? "").Where(char.IsDigit).ToArray()).PadLeft(length, '0')[^length..];
    private static string Fit(string? value, int length) => (value ?? "").ToUpperInvariant().PadRight(length)[..length];
    private static string Record(params string[] fields)
    {
        var value = string.Concat(fields);
        if (value.Length != 94) throw new InvalidOperationException($"NACHA record was {value.Length}, expected 94.");
        return value;
    }
}
