namespace Banking.Domain;

public sealed record CorrespondentPosition(decimal LedgerBalance, decimal AvailableBalance,
    decimal PendingOutgoing, decimal RespondentLedgerBalance, decimal MirrorDifference,
    IReadOnlyList<string> Findings);

/// <summary>Reconstructs positions from immutable postings; opening balances are the lab's opening equity.</summary>
public static class CorrespondentPositionPolicy
{
    public static void RequireJournal(IReadOnlyList<CorrespondentLedgerEntry> lines)
    {
        JournalControl.RequireBalanced(lines.Select(x => new JournalLine(x.Debit, x.Credit)));
        var first = lines[0];
        if (first.JournalId == Guid.Empty || first.PostingId == Guid.Empty || first.CorrelationId == Guid.Empty
            || lines.Any(x => x.JournalId != first.JournalId || x.PostingId != first.PostingId
                || x.AccountId != first.AccountId || x.BankId != first.BankId || x.CorrelationId != first.CorrelationId
                || x.WireTransferId != first.WireTransferId || x.BookingAt != first.BookingAt || x.ValueDate != first.ValueDate
                || decimal.Round(x.Debit, 4) != x.Debit || decimal.Round(x.Credit, 4) != x.Credit)
            || lines.Count(x => x.IsAccountLine) != 1)
            throw new InvalidOperationException("A correspondent journal requires one account line, consistent ownership and payment references, and four-decimal precision.");
    }

    public static CorrespondentPosition Evaluate(CorrespondentAccount account,
        IEnumerable<CorrespondentLedgerEntry> entries, decimal expectedHolds)
    {
        var lines = entries.Where(x => x.AccountId == account.Id).ToList();
        var findings = new List<string>();
        foreach (var journal in lines.GroupBy(x => x.JournalId))
        {
            try { RequireJournal(journal.ToList()); }
            catch (InvalidOperationException) { findings.Add($"Unbalanced or inconsistent journal {journal.Key}."); }
        }
        if (lines.Any(x => x.BankId != account.IssuerBankId && x.BankId != account.RespondentBankId))
            findings.Add("Journal ownership does not match the account relationship.");
        if (lines.Where(x => x.IsAccountLine).GroupBy(x => new { x.BankId, x.PostingId }).Any(x => x.Count() > 1))
            findings.Add("Duplicate account postings detected.");
        var issuer = account.OpeningBalance + lines.Where(x => x.BankId == account.IssuerBankId && x.IsAccountLine).Sum(x => x.Credit - x.Debit);
        var respondent = account.OpeningBalance + lines.Where(x => x.BankId == account.RespondentBankId && x.IsAccountLine).Sum(x => x.Debit - x.Credit);
        if (issuer != account.IssuerBalance) findings.Add($"Issuer balance differs from ledger by {account.IssuerBalance - issuer:0.0000} USD.");
        if (respondent != account.RespondentBalance) findings.Add($"Respondent balance differs from ledger by {account.RespondentBalance - respondent:0.0000} USD.");
        if (account.HeldBalance != expectedHolds) findings.Add($"Held balance differs from pending instructions by {account.HeldBalance - expectedHolds:0.0000} USD.");
        if (expectedHolds < 0 || account.HeldBalance < 0 || issuer < expectedHolds)
            findings.Add("Insufficient ledger liquidity or invalid reservation balance.");
        // Outstanding mirror delivery is observable separately; it is not automatically an accounting break.
        return new(issuer, issuer - expectedHolds, expectedHolds, respondent, issuer - respondent, findings);
    }
}

public sealed record CorrespondentAccountPosition(Guid AccountId, CorrespondentPosition Position,
    decimal TodaysWireVolume, IReadOnlyList<CorrespondentWireActivity> RecentWires);
public sealed record CorrespondentWireActivity(Guid WireId, Guid CorrelationId, decimal Amount,
    bool IsCredit, WireStatus WireStatus, CorrespondentPaymentStatus AccountingStatus);
