using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public static class JournalPosting
{
    public static void AddBalanced(this DbSet<LedgerEntry> entries, params LedgerEntry[] lines)
    {
        RequireBalanced(lines.Select(x => new JournalLine(x.Debit, x.Credit)));
        entries.AddRange(lines);
    }

    public static void AddBalanced(this DbSet<AchLedgerEntry> entries, params AchLedgerEntry[] lines)
    {
        RequireBalanced(lines.Select(x => new JournalLine(x.Debit, x.Credit)));
        entries.AddRange(lines);
    }

    public static void AddBalanced(this DbSet<CheckLedgerEntry> entries, params CheckLedgerEntry[] lines)
    {
        RequireBalanced(lines.Select(x => new JournalLine(x.Debit, x.Credit)));
        entries.AddRange(lines);
    }

    private static void RequireBalanced(IEnumerable<JournalLine> lines) =>
        JournalControl.RequireBalanced(lines);
}
