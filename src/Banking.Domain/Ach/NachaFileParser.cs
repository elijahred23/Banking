using System.Globalization;

namespace Banking.Domain;

public sealed record NachaValidationResult(bool IsValid, IReadOnlyList<string> Errors,
    int BatchCount, int EntryCount, decimal TotalDebits, decimal TotalCredits);

public sealed class NachaFileParser
{
    public NachaValidationResult Validate(string? payload)
    {
        var errors = new List<string>();
        var lines = (payload ?? "").Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return new(false, ["The NACHA file is empty."], 0, 0, 0, 0);
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Length != 94) errors.Add($"Record {i + 1} is {lines[i].Length} characters; expected 94.");
        if (lines[0].Length == 0 || lines[0][0] != '1') errors.Add("File header record is missing.");
        var controls = lines.Where(x => x.Length == 94 && x[0] == '9' && x.Any(c => c != '9')).ToList();
        if (controls.Count != 1) errors.Add("Exactly one file control record is required.");
        var batchCount = lines.Count(x => x.Length == 94 && x[0] == '5');
        var batchControls = lines.Count(x => x.Length == 94 && x[0] == '8');
        if (batchCount != batchControls) errors.Add("Batch header and control counts do not match.");
        var entryLines = lines.Where(x => x.Length == 94 && x[0] == '6').ToList();
        foreach (var entry in entryLines)
        {
            var routing = entry.Substring(3, 8) + entry[11];
            if (!AbaRoutingNumberValidator.IsValid(routing)) errors.Add($"Entry routing number {routing} is invalid.");
            if (!decimal.TryParse(entry.Substring(29, 10), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                errors.Add("Entry contains a nonnumeric amount.");
            if (entry.Substring(1, 2) is not ("22" or "27" or "32" or "37"))
                errors.Add($"Transaction code {entry.Substring(1, 2)} is outside the supported ACH subset.");
        }
        decimal debit = 0, credit = 0;
        foreach (var entry in entryLines)
        {
            if (!long.TryParse(entry.Substring(29, 10), out var cents)) continue;
            var amount = cents / 100m;
            if (entry.Substring(1, 2) is "27" or "37") debit += amount; else credit += amount;
        }
        if (controls.Count == 1)
        {
            var control = controls[0];
            CheckNumber(control.Substring(1, 6), batchCount, "file batch count", errors);
            CheckNumber(control.Substring(7, 6), lines.Length / 10, "file block count", errors);
            CheckNumber(control.Substring(13, 8), entryLines.Count + lines.Count(x => x.Length == 94 && x[0] == '7'), "file entry/addenda count", errors);
            CheckNumber(control.Substring(21, 10), RoutingHash(entryLines), "file routing hash", errors);
            CheckCents(control.Substring(31, 12), debit, "file debit total", errors);
            CheckCents(control.Substring(43, 12), credit, "file credit total", errors);
        }
        ValidateBatchControls(lines, errors);
        return new(errors.Count == 0, errors, batchCount, entryLines.Count, debit, credit);
    }

    private static void ValidateBatchControls(string[] lines, List<string> errors)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length != 94 || lines[i][0] != '5') continue;
            var details = new List<string>();
            var addendaCount = 0;
            string? control = null;
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].Length != 94) continue;
                if (lines[j][0] == '6') details.Add(lines[j]);
                else if (lines[j][0] == '7') addendaCount++;
                else if (lines[j][0] == '8') { control = lines[j]; break; }
                else if (lines[j][0] is '5' or '9') break;
            }
            if (control is null) { errors.Add("Batch control record is missing."); continue; }
            decimal debits = 0, credits = 0;
            foreach (var detail in details)
            {
                if (!long.TryParse(detail.Substring(29, 10), out var cents)) continue;
                if (detail.Substring(1, 2) is "27" or "37") debits += cents / 100m; else credits += cents / 100m;
            }
            CheckNumber(control.Substring(4, 6), details.Count + addendaCount, "batch entry/addenda count", errors);
            CheckNumber(control.Substring(10, 10), RoutingHash(details), "batch routing hash", errors);
            CheckCents(control.Substring(20, 12), debits, "batch debit total", errors);
            CheckCents(control.Substring(32, 12), credits, "batch credit total", errors);
        }
    }

    private static long RoutingHash(IEnumerable<string> entries) => entries.Sum(x =>
        long.TryParse(x.Substring(3, 8), out var routing) ? routing : 0) % 10_000_000_000L;

    private static void CheckNumber(string raw, long expected, string label, List<string> errors)
    {
        if (!long.TryParse(raw, out var actual) || actual != expected) errors.Add($"The {label} does not match detail records.");
    }
    private static void CheckCents(string raw, decimal expected, string label, List<string> errors) =>
        CheckNumber(raw, decimal.ToInt64(decimal.Round(expected * 100, 0, MidpointRounding.AwayFromZero)), label, errors);
}
