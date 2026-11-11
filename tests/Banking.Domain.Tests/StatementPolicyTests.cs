using Banking.Domain;
using Banking.Infrastructure;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class StatementPolicyTests
{
    [Fact]
    public void Statement_arithmetic_uses_only_booked_entry_directions()
    {
        var statement = Sample();
        Assert.Equal(650_000m, StatementPolicy.Closing(500_000, statement.Entries));
        statement.Entries.Add(new AccountStatementEntry { Amount = 25 });
        Assert.Equal(649_975m, StatementPolicy.Closing(500_000, statement.Entries));
    }
    [Theory]
    [InlineData(StatementKind.Intraday, "camt.052.001.08")]
    [InlineData(StatementKind.Daily, "camt.053.001.08")]
    [InlineData(StatementKind.Notification, "camt.054.001.08")]
    public void Camt_messages_preserve_versions_signed_balances_dates_and_escaped_text(StatementKind kind, string version)
    {
        var statement = Sample(); statement.Kind = kind; statement.RecipientName = "Fictional <Bank> & Trust";
        var xml = CamtStatementBuilder.Build(statement);
        Assert.Contains(version, xml); Assert.Contains("Fictional &lt;Bank&gt; &amp; Trust", xml);
        Assert.Contains("100000.0000", xml); Assert.Contains("BOOK", xml);
        CamtStatementBuilder.Validate(xml, kind);
        var other = kind == StatementKind.Daily ? StatementKind.Intraday : StatementKind.Daily;
        Assert.Throws<InvalidOperationException>(() => CamtStatementBuilder.Validate(xml, other));
    }
    [Fact]
    public void Missing_amount_duplicate_and_timing_exceptions_are_distinct()
    {
        var statement = Sample();
        var entries = statement.Entries;
        var own = entries.Select(x => new CorrespondentLedgerEntry { Id = Guid.NewGuid(), PostingId = x.PostingId,
            CorrelationId = x.CorrelationId, Debit = x.IsCredit ? x.Amount : 0, Credit = x.IsCredit ? 0 : x.Amount,
            BookingAt = x.BookingAt, ValueDate = x.ValueDate, Reference = x.Reference }).ToList();
        Assert.Empty(StatementPolicy.Match(entries, own, statement.PeriodStart, statement.PeriodEnd));
        own[0].Credit += 1;
        Assert.Contains(StatementPolicy.Match(entries, own, statement.PeriodStart, statement.PeriodEnd), x => x.Type == "Amount mismatch");
        own[0].Credit -= 1; own[0].BookingAt = statement.PeriodEnd.AddHours(1);
        Assert.Contains(StatementPolicy.Match(entries, own, statement.PeriodStart, statement.PeriodEnd), x => x.Type == "Timing difference");
        own.Add(new CorrespondentLedgerEntry { PostingId = own[0].PostingId });
        Assert.Contains(StatementPolicy.Match(entries, own, statement.PeriodStart, statement.PeriodEnd), x => x.Type == "Duplicate entry");
        own.Clear();
        var missing = StatementPolicy.Match(entries, own, statement.PeriodStart, statement.PeriodEnd);
        Assert.Contains(missing, x => x.Type == "Missing debit"); Assert.Contains(missing, x => x.Type == "Missing credit");
    }
    [Fact]
    public void Business_day_schedule_tracks_dst_and_holidays_without_duplicate_local_runs()
    {
        var friday = new DateTimeOffset(2026, 3, 7, 0, 0, 0, TimeSpan.Zero); // Friday 18:00 CST
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 23, 0, 0, TimeSpan.Zero), StatementPolicy.NextRun(friday, "America/Chicago", new(18, 0), []));
        Assert.Equal(new DateTimeOffset(2026, 3, 10, 23, 0, 0, TimeSpan.Zero), StatementPolicy.NextRun(friday, "America/Chicago", new(18, 0), [new(2026, 3, 9)]));
    }
    [Fact]
    public void Csv_escapes_formula_text_and_preserves_precision()
    {
        var statement = Sample(); statement.Entries[0].Reference = "=HYPERLINK(\"malicious\")";
        var csv = System.Text.Encoding.UTF8.GetString(new StatementExportService().Csv(statement));
        Assert.Contains("\"'=HYPERLINK", csv); Assert.Contains("650000.0000", csv);
    }
    [Fact]
    public async Task Pdf_handles_empty_and_multiple_pages_with_balances_and_references()
    {
        var statement = Sample();
        var exports = new StatementExportService();
        var bytes = exports.Pdf(statement);
        Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(bytes));
        Assert.Contains("650,000.0000", System.Text.Encoding.ASCII.GetString(bytes));
        var qa = Environment.GetEnvironmentVariable("BANKING_STATEMENT_QA_DIR");
        if (qa is not null) { Directory.CreateDirectory(qa); await File.WriteAllBytesAsync(Path.Combine(qa, "statement-example.pdf"), bytes); }
        for (var i = 0; i < 80; i++) statement.Entries.Add(new AccountStatementEntry { Amount = i + 1, IsCredit = true,
            BookingAt = statement.PeriodStart.AddMinutes(i), ValueDate = new(2026, 11, 10), Reference = new string('W', 180) });
        statement.TotalCredits = statement.Entries.Where(x => x.IsCredit).Sum(x => x.Amount);
        statement.ClosingBalance = StatementPolicy.Closing(statement.OpeningBalance, statement.Entries);
        bytes = exports.Pdf(statement);
        Assert.Contains("Page 2 of", System.Text.Encoding.ASCII.GetString(bytes));
        if (qa is not null) await File.WriteAllBytesAsync(Path.Combine(qa, "statement-multipage.pdf"), bytes);
        statement.Entries.Clear();
        Assert.Contains("No booked transactions", System.Text.Encoding.ASCII.GetString(exports.Pdf(statement)));
    }
    private static AccountStatement Sample()
    {
        var start = new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        return new AccountStatement { StatementNumber = "STMT-20261110-001", AccountNumber = "LAB0042", IssuerName = "Bankers Bank",
            RecipientName = "Community Bank A", Kind = StatementKind.Daily, PeriodStart = start, PeriodEnd = start.AddDays(1), CreatedAt = start.AddDays(1),
            SequenceNumber = 1, OpeningBalance = 500_000, ClosingBalance = 650_000, TotalDebits = 100_000, TotalCredits = 250_000,
            Entries = [new() { PostingId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), Amount = 100_000, BookingAt = start.AddHours(9), ValueDate = new(2026, 11, 10), Reference = "Outgoing Fedwire" },
                new() { PostingId = Guid.NewGuid(), CorrelationId = Guid.NewGuid(), Amount = 250_000, IsCredit = true, BookingAt = start.AddHours(14), ValueDate = new(2026, 11, 10), Reference = "Incoming Fedwire" }] };
    }
}
