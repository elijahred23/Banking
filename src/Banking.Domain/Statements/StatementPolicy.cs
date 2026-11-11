namespace Banking.Domain;

public static class StatementPolicy
{
    public static string MessageType(StatementKind kind) => kind switch {
        StatementKind.Intraday => "camt.052", StatementKind.Daily => "camt.053", StatementKind.Notification => "camt.054",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    public static decimal Closing(decimal opening, IEnumerable<AccountStatementEntry> entries) =>
        opening + entries.Sum(x => x.IsCredit ? x.Amount : -x.Amount);
    public static DateTimeOffset NextRun(DateTimeOffset after, string zoneId, TimeOnly localTime,
        IReadOnlyCollection<DateOnly> holidays)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var local = TimeZoneInfo.ConvertTime(after, zone);
        var date = DateOnly.FromDateTime(local.DateTime);
        for (var i = 0; i < 3700; i++, date = date.AddDays(1))
        {
            if (!FedFundsPolicy.IsBusinessDay(date, holidays)) continue;
            var wall = date.ToDateTime(localTime, DateTimeKind.Unspecified);
            // Shift a skipped wall time forward; choose the later occurrence of an ambiguous time.
            while (zone.IsInvalidTime(wall)) wall = wall.AddMinutes(1);
            var offset = zone.IsAmbiguousTime(wall) ? zone.GetAmbiguousTimeOffsets(wall).Min() : zone.GetUtcOffset(wall);
            var candidate = new DateTimeOffset(wall, offset).ToUniversalTime();
            if (candidate > after) return candidate;
        }
        throw new InvalidOperationException("No eligible reporting day in the configured calendar.");
    }
    public static IReadOnlyList<StatementFinding> Match(IReadOnlyList<AccountStatementEntry> statement,
        IReadOnlyList<CorrespondentLedgerEntry> dueFrom, DateTimeOffset start, DateTimeOffset end, bool includeEnd = false)
    {
        var findings = new List<StatementFinding>();
        var matched = new HashSet<Guid>();
        foreach (var group in statement.GroupBy(x => x.PostingId))
        {
            if (group.Count() > 1) findings.Add(new("Duplicate entry", group.Key.ToString(), 0, "Statement repeats a posting identifier."));
            var entry = group.First();
            var exact = dueFrom.Where(x => x.PostingId == entry.PostingId).ToList();
            var candidates = exact.Count > 0 ? exact : dueFrom.Where(x => x.CorrelationId == entry.CorrelationId).ToList();
            if (candidates.Count == 0)
            { findings.Add(new(entry.IsCredit ? "Missing credit" : "Missing debit", entry.Reference, entry.IsCredit ? entry.Amount : -entry.Amount, "Correspondent entry has no respondent due-from posting.")); continue; }
            if (candidates.Count > 1) findings.Add(new("Duplicate entry", entry.Reference, 0, "Multiple due-from postings share a strong identifier."));
            foreach (var item in candidates) matched.Add(item.Id);
            var candidate = candidates[0];
            // Respondent asset debits increase due-from, whereas issuer liability credits increase the statement account.
            var delta = candidate.Debit - candidate.Credit;
            var expected = entry.IsCredit ? entry.Amount : -entry.Amount;
            if (delta != expected) findings.Add(new("Amount mismatch", entry.Reference, delta - expected, "Matched posting has a different signed amount."));
            if (candidate.ValueDate != entry.ValueDate || candidate.BookingAt < start || candidate.BookingAt > end || !includeEnd && candidate.BookingAt == end)
                findings.Add(new("Timing difference", entry.Reference, 0, "Matched posting has a different value date or was booked outside this statement period."));
        }
        foreach (var item in dueFrom.Where(x => x.BookingAt >= start && (x.BookingAt < end || includeEnd && x.BookingAt == end) && !matched.Contains(x.Id)))
            findings.Add(new(item.Debit > 0 ? "Missing credit" : "Missing debit", item.Reference, item.Debit - item.Credit, "Respondent due-from entry is absent from the statement."));
        return findings;
    }
}
