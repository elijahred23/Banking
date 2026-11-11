namespace Banking.Domain;

// Presentation forecasts only: never authorize, reserve, settle, or post a payment.
public sealed record WireVisualBalance(string Name, decimal Before, decimal After);
public sealed record WireVisualStep(string Title, string Detail, string Boundary,
    IReadOnlyList<WireVisualBalance> Balances, bool Posted);
public sealed record WireVisualBranch(string Id, string Name, IReadOnlyList<WireVisualStep> Steps);
public sealed record WireVisualization(decimal Amount, string Sender, string Receiver, string Rail,
    bool Institution, bool Correspondent, string SelectedBranch, IReadOnlyList<WireVisualBranch> Branches);

public static class WireVisualizationPolicy
{
    public static WireVisualization Preview(WireTransfer wire, Bank sender, Bank receiver, Account? account)
    {
        var institution = wire.TransferType == WireTransferType.FinancialInstitutionCreditTransfer;
        var correspondent = wire.Rail == PaymentRail.SwiftCbprPlus;
        WireVisualStep Step(string title, string detail, string boundary, bool held = false, bool posted = false)
        {
            var balances = new List<WireVisualBalance>();
            if (!institution && account is not null)
            {
                balances.Add(new("Customer ledger", account.Balance, account.Balance - (posted ? wire.Amount : 0)));
                balances.Add(new("Customer available", account.AvailableBalance,
                    account.AvailableBalance - (held || posted ? wire.Amount : 0)));
                balances.Add(new("Customer holds", account.HeldBalance, account.HeldBalance + (held ? wire.Amount : 0)));
            }
            if (!correspondent)
                balances.Add(new("Bank settlement cash", sender.MasterAccountBalance,
                    sender.MasterAccountBalance - (posted ? wire.Amount : 0)));
            return new(title, detail, boundary, balances, posted);
        }
        var captured = Step("Instruction", "Maker submits for a different approver. No funds move.", "Internal instruction");
        var reserved = Step("Validated", institution
            ? "Master-account liquidity is checked; this workflow does not reserve a customer hold."
            : "After approval and screening, validation reserves customer funds; ledger balance is unchanged.",
            "Internal validation", held: !institution);
        var delivered = Step("Delivered", "Transport delivery is distinct from business acceptance and settlement.",
            wire.TransferType == WireTransferType.FinancialInstitutionCreditTransfer ? "pacs.009 delivery" : "pacs.008 delivery", held: !institution);
        var accepted = Step("Accepted", "Conceptual pause after final acceptance and before settlement/posting. Actual events may be recorded close together.",
            "Positive business outcome", held: !institution);
        var settled = Step("Settled", correspondent
            ? "Customer posting follows final acceptance. Correspondent positions are simulated; master-account effects are not forecast here."
            : "Settlement cash moves, the balanced journal posts, and this payment's customer hold releases.",
            "Settlement and accounting", posted: true);
        var branches = new List<WireVisualBranch>
        {
            new("accepted", "Accepted and settled", [captured, reserved, delivered, accepted, settled]),
            new("rejected", "Business rejection", [captured, reserved, delivered,
                Step("Rejected", "Rejection releases this payment's applicable hold. Existing holds remain; no journal posts.", "Negative business outcome")]),
            new("malformed", "Malformed message", [captured, reserved,
                Step("Validation failed", "Structural validation prevents transmission and releases this payment's hold. No journal posts.", "Local ISO validation")]),
            new("delayed", "Delayed acceptance", [captured, reserved, delivered,
                Step("Pending", "No final outcome yet. Investigate the same payment; delivery alone cannot justify posting.", "Pending business outcome", held: !institution)])
        };
        if ((institution && sender.MasterAccountBalance < wire.Amount)
            || (!institution && (account is null || !AccountAvailabilityPolicy.CanDebit(account, wire.Amount))))
            branches.Add(new("funds", "Insufficient or ineligible funds", [captured,
                Step("Stopped before transmission", "Current source funds or account eligibility prevent validation. No new hold or journal is created.", "Local funds validation")]));
        var selected = branches.Any(x => x.Id == "funds") ? "funds" : wire.Scenario switch
        {
            ProcessingScenario.FedRejects => "rejected",
            ProcessingScenario.MalformedIso => "malformed",
            ProcessingScenario.PendingThenAccepted => "delayed",
            _ => "accepted"
        };
        return new(wire.Amount, sender.Name, receiver.Name, wire.Rail.ToString(), institution,
            correspondent, selected, branches);
    }
}
