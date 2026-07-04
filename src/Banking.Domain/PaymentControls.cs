namespace Banking.Domain;

public readonly record struct JournalLine(decimal Debit, decimal Credit);

public static class JournalControl
{
    public static bool IsBalanced(IEnumerable<JournalLine> lines)
    {
        var materialized = lines.ToList();
        return materialized.Count >= 2
            && materialized.All(x => x.Debit >= 0 && x.Credit >= 0
                && (x.Debit == 0) != (x.Credit == 0))
            && materialized.Sum(x => x.Debit) == materialized.Sum(x => x.Credit);
    }

    public static void RequireBalanced(IEnumerable<JournalLine> lines)
    {
        if (!IsBalanced(lines))
            throw new InvalidOperationException("A posted journal must contain balanced, one-sided debit and credit lines.");
    }
}

public static class WireWorkflow
{
    private static readonly IReadOnlyDictionary<WireStatus, WireStatus[]> Allowed =
        new Dictionary<WireStatus, WireStatus[]>
        {
            [WireStatus.PendingApproval] = [WireStatus.Created, WireStatus.Rejected],
            [WireStatus.PendingComplianceReview] = [WireStatus.Created, WireStatus.Rejected],
            [WireStatus.Created] = [WireStatus.Validated, WireStatus.PendingComplianceReview, WireStatus.Rejected],
            [WireStatus.Validated] = [WireStatus.ReadyForFed, WireStatus.Rejected],
            [WireStatus.ReadyForFed] = [WireStatus.SentToFed, WireStatus.Rejected],
            [WireStatus.SentToFed] = [WireStatus.ReadyForFed, WireStatus.PendingAtFed, WireStatus.Settled, WireStatus.Rejected],
            [WireStatus.PendingAtFed] = [WireStatus.SentToFed, WireStatus.Settled, WireStatus.Rejected],
            [WireStatus.Settled] = [WireStatus.Returned],
            [WireStatus.Received] = [WireStatus.Completed, WireStatus.Returned],
            [WireStatus.Completed] = [WireStatus.Returned]
        };

    public static IReadOnlyList<WireStatus> AllowedNext(WireStatus current) =>
        Allowed.TryGetValue(current, out var next) ? next : [];

    public static bool CanTransition(WireStatus current, WireStatus next) =>
        AllowedNext(current).Contains(next);

    public static void Transition(WireTransfer wire, WireStatus next)
    {
        if (wire.Status == next) return;
        if (!CanTransition(wire.Status, next))
            throw new InvalidOperationException($"Wire cannot transition from {wire.Status} to {next}.");
        wire.Status = next;
    }
}

public static class WirePostingPolicy
{
    public static void SettleCustomerPayment(WireTransfer wire, Account account)
    {
        if (wire.TransferType != WireTransferType.CustomerCreditTransfer)
            throw new InvalidOperationException("Customer posting cannot be used for an institution transfer.");
        if (wire.FromAccountId != account.Id)
            throw new InvalidOperationException("The posting account does not own this payment hold.");
        if (account.HeldBalance < wire.Amount)
            throw new InvalidOperationException("The customer payment hold is missing or insufficient.");
        WireWorkflow.Transition(wire, WireStatus.Settled);
        account.HeldBalance -= wire.Amount;
        account.Balance -= wire.Amount;
    }

    public static void RejectCustomerPayment(WireTransfer wire, Account account)
    {
        if (wire.FromAccountId != account.Id)
            throw new InvalidOperationException("The posting account does not own this payment hold.");
        WireWorkflow.Transition(wire, WireStatus.Rejected);
        account.HeldBalance = Math.Max(0, account.HeldBalance - wire.Amount);
    }
}
