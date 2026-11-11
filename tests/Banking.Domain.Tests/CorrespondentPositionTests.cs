using Banking.Domain;
using Banking.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Banking.Domain.Tests;

public sealed class CorrespondentPositionTests
{
    [Fact]
    public void Settlement_and_fee_reconstruct_the_illustrative_balance()
    {
        var account = Account();
        account.IssuerBalance = 124985;
        account.RespondentBalance = 124985;
        var entries = Journal(account, account.IssuerBankId, -75000)
            .Concat(Journal(account, account.IssuerBankId, -15))
            .Concat(Journal(account, account.RespondentBankId, -75000))
            .Concat(Journal(account, account.RespondentBankId, -15));
        var position = CorrespondentPositionPolicy.Evaluate(account, entries, 0);
        Assert.Equal(124985, position.LedgerBalance);
        Assert.Equal(124985, position.AvailableBalance);
        Assert.Equal(0, position.MirrorDifference);
        Assert.Empty(position.Findings);
    }

    [Fact]
    public void Reservation_reduces_availability_without_posting_and_delayed_mirror_is_visible()
    {
        var account = Account();
        account.IssuerBalance = 199985;
        account.HeldBalance = 75000;
        var position = CorrespondentPositionPolicy.Evaluate(account, Journal(account, account.IssuerBankId, -15), 75000);
        Assert.Equal(199985, position.LedgerBalance);
        Assert.Equal(124985, position.AvailableBalance);
        Assert.Equal(-15, position.MirrorDifference);
        Assert.Empty(position.Findings);
    }

    [Fact]
    public void Reconciliation_reports_corrupt_balances_holds_and_journals_without_repairing_them()
    {
        var account = Account();
        account.IssuerBalance = 190000;
        account.RespondentBalance = 180000;
        account.HeldBalance = 1000;
        var journal = Journal(account, account.IssuerBankId, -75000);
        journal[1].Credit -= 1;
        var position = CorrespondentPositionPolicy.Evaluate(account, journal, 150000);
        Assert.Contains(position.Findings, x => x.StartsWith("Unbalanced"));
        Assert.Contains(position.Findings, x => x.StartsWith("Issuer balance"));
        Assert.Contains(position.Findings, x => x.StartsWith("Respondent balance"));
        Assert.Contains(position.Findings, x => x.StartsWith("Held balance"));
        Assert.Contains(position.Findings, x => x.StartsWith("Insufficient"));
        Assert.Equal(190000, account.IssuerBalance);
        Assert.Equal(1000, account.HeldBalance);
    }

    [Fact]
    public void Other_accounts_do_not_affect_a_position()
    {
        var account = Account();
        var position = CorrespondentPositionPolicy.Evaluate(account, Journal(Account(), account.IssuerBankId, -75000), 0);
        Assert.Equal(200000, position.LedgerBalance);
        Assert.Empty(position.Findings);
    }

    [Theory]
    [InlineData("unbalanced")]
    [InlineData("ownership")]
    [InlineData("correlation")]
    [InlineData("account line")]
    [InlineData("precision")]
    public async Task Persistence_rejects_invalid_journals(string failure)
    {
        var account = Account();
        var journal = Journal(account, account.IssuerBankId, -75000);
        switch (failure)
        {
            case "unbalanced": journal[1].Credit = 1; break;
            case "ownership": journal[1].BankId = Guid.NewGuid(); break;
            case "correlation": journal[1].CorrelationId = Guid.NewGuid(); break;
            case "account line": journal[1].IsAccountLine = true; break;
            case "precision": journal[0].Debit = journal[1].Credit = 0.00001m; break;
        }
        await using var db = new BankingDbContext(new DbContextOptionsBuilder<BankingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.CorrespondentLedgerEntries.AddRange(journal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Persistence_accepts_balanced_journals_and_preserves_immutability()
    {
        var account = Account();
        var journal = Journal(account, account.IssuerBankId, -75000);
        await using var db = new BankingDbContext(new DbContextOptionsBuilder<BankingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        db.CorrespondentLedgerEntries.AddRange(journal);
        await db.SaveChangesAsync();
        journal[0].Reference = "rewrite";
        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }

    private static CorrespondentAccount Account() => new() { IssuerBankId = Guid.NewGuid(), RespondentBankId = Guid.NewGuid(),
        OpeningBalance = 200000, IssuerBalance = 200000, RespondentBalance = 200000 };

    private static List<CorrespondentLedgerEntry> Journal(CorrespondentAccount account, Guid bank, decimal delta)
    {
        var journal = Guid.NewGuid(); var posting = Guid.NewGuid(); var correlation = Guid.NewGuid();
        var debit = bank == account.IssuerBankId ? delta < 0 : delta > 0;
        return [
            new() { AccountId = account.Id, BankId = bank, JournalId = journal, PostingId = posting, CorrelationId = correlation,
                IsAccountLine = true, Debit = debit ? Math.Abs(delta) : 0, Credit = debit ? 0 : Math.Abs(delta) },
            new() { AccountId = account.Id, BankId = bank, JournalId = journal, PostingId = posting, CorrelationId = correlation,
                Debit = debit ? 0 : Math.Abs(delta), Credit = debit ? Math.Abs(delta) : 0 }
        ];
    }
}
