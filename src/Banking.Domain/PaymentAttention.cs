namespace Banking.Domain;

// Observations explain persisted state; they never authorize or transition a payment.
public sealed record PaymentAttention(string Title, string Owner, string Action, string Evidence,
    string Funds, string Category);

public static class PaymentAttentionPolicy
{
    public static PaymentAttention For(WireTransfer wire) => wire.Status switch
    {
        WireStatus.PendingApproval => new("Independent approval required", "Approver", "Review the instruction using a different identity from its maker.", "Maker, beneficiary details, amount, and approval record", "No value posting at creation.", "action"),
        WireStatus.PendingComplianceReview => new("Screening review required", "Compliance", "Review the screening reason and record an authorized decision.", "Screening reason and review decision", "No settlement before acceptance.", "action"),
        WireStatus.Created => new("Waiting for validation", "Payment service", "Check the durable outbox and wire consumer if processing stalls.", "Outbox publication and validation events", "No final value posting.", "waiting"),
        WireStatus.Validated or WireStatus.ReadyForFed => new("Waiting for network submission", "Operations", "Check delivery evidence and liquidity; preserve the original instruction.", "Delivery events and liquidity evidence", "Validated customer transfers reserve funds until a final outcome.", "waiting"),
        WireStatus.SentToFed or WireStatus.PendingAtFed => new("Final outcome not yet recorded", "Operations", "Inspect status and investigation evidence before considering another payment.", "Correlated status response, settlement record, and journal", "A timeout alone does not prove rejection or settlement.", "waiting"),
        WireStatus.Received => new("Receiving-side processing", "Receiving bank", "Inspect beneficiary posting and its balanced journal.", "Incoming payment events and customer journal", "Receipt alone does not prove customer posting.", "waiting"),
        WireStatus.Settled or WireStatus.Completed => new("Settlement recorded", "Operations", "Reconcile the settlement and customer journals; use the return case workflow for recovery.", "Settlement identifiers and balanced journals", "A cancellation request does not undo final settlement.", "complete"),
        WireStatus.Returned => new("Return recorded", "Operations", "Reconcile the original and return journals together.", "Return case and reversing journal", "Keep the original posting and separate return evidence.", "review"),
        WireStatus.Rejected => new("Payment rejected", "Maker / Operations", "Inspect the rejection reason and confirm applicable holds were released before a new instruction.", "Rejection response and hold release", "No accepted value posting should remain for a rejected instruction.", "review"),
        _ => new("Inspect payment evidence", "Operations", "Review the lifecycle.", "Payment events", "Do not infer settlement from transport.", "review")
    };

    public static PaymentAttention For(AchEntry entry, DateOnly? businessDate = null) => entry.Status switch
    {
        AchEntryStatus.PendingApproval => new("Independent approval required", "Approver", "Review the company, receiver, SEC code, and authorization before release.", "Maker and separate approval record", "No value posting at capture.", "action"),
        AchEntryStatus.Entered => new("Waiting for ODFI validation", "ODFI", "Inspect validation and outbox evidence if processing stalls.", "Validation outcome", "Credit holds follow validation; no settlement yet.", "waiting"),
        AchEntryStatus.Validated => new("Waiting for batching", "ODFI", "Check the ACH worker and batch assignment.", "Batch identifier and effective date", "No final posting before acceptance.", "waiting"),
        AchEntryStatus.Batched when businessDate is { } date && entry.EffectiveEntryDate > date => new("Scheduled for a future banking date", "Operations", $"Advance the shared lab clock to {entry.EffectiveEntryDate:yyyy-MM-dd} when the lab is ready.", "Effective date, shared clock, and saved file", "Credit funds remain reserved; a draft file is not submission.", "waiting"),
        AchEntryStatus.Batched => new("Waiting for eligible file cutoff", "ODFI", "The ACH worker builds eligible files at its configured lab cutoff. Check the shared date and worker.", "Saved NACHA file and operator response", "Draft inspection moves no money.", "waiting"),
        AchEntryStatus.SentToOperator => new("Waiting for operator outcome", "Operations", "Inspect the saved file and operator result before resubmitting.", "File controls, response, and settlement journal", "Delivery alone does not establish acceptance.", "waiting"),
        AchEntryStatus.NocReceived => new("Account correction needs review", "Originator / Operations", "Inspect the NOC and use corrected instructions for future payments; preserve the original entry.", "NOC code, corrected data, and original trace", "A correction is distinct from a return; inspect settlement separately.", "review"),
        AchEntryStatus.Returned => new("RDFI return recorded", "Operations", "Inspect the return reason and reconcile original and reversing journals before deciding next steps.", "Original trace, return record, and reversing journal", "The return is a separate movement after the original settlement.", "review"),
        AchEntryStatus.Rejected => new("Instruction or file rejected", "Maker / Operations", "Inspect rejection evidence. Eligible rejected files can use the repair workflow with independent approval.", "ODFI/file rejection and replacement linkage", "Confirm applicable holds are released; do not treat rejection as an RDFI return.", "review"),
        AchEntryStatus.Settled or AchEntryStatus.Posted => new("Settlement recorded", "Operations", "Reconcile operator acceptance and balanced journals; continue monitoring for returns and corrections.", "Saved file, trace, and posting journal", "Settlement does not remove the possibility of a later ACH return.", "complete"),
        _ => new("Inspect entry evidence", "Operations", "Review the lifecycle.", "Entry and file records", "Do not infer settlement from file delivery.", "review")
    };

    public static decimal AchSettlementMovement(Guid bankId, AchEntry entry)
    {
        if (entry.OriginatingBankId == entry.ReceivingBankId) return 0;
        var origin = AchRules.IsCredit(entry.TransactionCode) ? -entry.Amount : entry.Amount;
        return entry.OriginatingBankId == bankId ? origin
            : entry.ReceivingBankId == bankId ? -origin : 0;
    }
}

public sealed record LabClockMilestone(DateTimeOffset At, string Label);
public static class LabClockPolicy
{
    // The existing ACH worker uses banking dates and a short wall-clock file cutoff.
    // These are eligible-date milestones, not claimed production settlement times.
    public static LabClockMilestone Next(DateTimeOffset now, IEnumerable<DateOnly> effectiveDates)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var next = effectiveDates.Where(x => x > today && AchBusinessCalendar.IsBankingDay(x))
            .OrderBy(x => x).FirstOrDefault();
        if (next == default) next = AchBusinessCalendar.NextBankingDay(today);
        return new(new DateTimeOffset(next.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            "Next eligible ACH banking date");
    }
}
