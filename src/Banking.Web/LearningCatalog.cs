using Banking.Domain;
using Banking.Web.Models;

namespace Banking.Web;

public static class LearningCatalog
{
    public static IReadOnlyList<LearningPathViewModel> Paths { get; } =
    [
        new("foundations", "Payment foundations", "New learners", "Learn the payment lifecycle, settlement finality, and the major rails.", ["first-fedwire", "fednow-finality", "compare-rails"]),
        new("operations", "Payment operations", "Operations teams", "Practice approval, exception handling, observability, and reconciliation.", ["first-fedwire", "rejected-wire", "trace-payment"]),
        new("iso", "ISO 20022", "Analysts and engineers", "Connect business actions to pacs messages, identifiers, and status codes.", ["first-fedwire", "iso-message-chain", "swift-route"]),
        new("accounting", "Settlement accounting", "Finance and operations", "Follow holds, master-account settlement, and balanced journals.", ["fednow-finality", "rejected-wire", "trace-payment"]),
        new("systems", "Distributed payment systems", "Software engineers", "Trace messages across services, queues, retries, and idempotent settlement.", ["trace-payment", "rejected-wire", "swift-route"])
    ];

    public static IReadOnlyList<LearningMissionViewModel> Missions { get; } =
    [
        new("first-fedwire", "foundations", "Send a first Fedwire payment", "Create, approve, and observe a domestic customer credit transfer.", "Beginner", 12,
            ["Distinguish payment creation from release", "Identify pacs.008 and pacs.002", "Explain when the customer hold becomes a posted debit"],
            ["Sign in as Maker and create a standard Fedwire payment", "Switch to Approver and release it", "Open the payment and inspect its lifecycle, messages, and journal"],
            "When does the customer balance become a posted debit?", ["When the maker submits", "When the approver releases", "After final network acceptance"],
            "Approval authorizes processing, but settlement is the event that converts the hold into posted accounting entries.", "Wires", "Create", "Start Fedwire mission", PaymentRail.Fedwire, ProcessingScenario.Standard),
        new("fednow-finality", "foundations", "Observe FedNow finality", "Compare instant receiver acceptance with traditional wire processing.", "Beginner", 10,
            ["Recognize immediate final settlement", "Explain receiver acceptance", "Find the FedNow network reference"],
            ["Create a standard FedNow payment", "Approve the payment", "Observe receiver acceptance and final posting"],
            "Which event should immediately precede final settlement?", ["Receiver acceptance", "End-of-day batching", "A manual operator release"],
            "FedNow is designed for immediate processing and final settlement; it is not an end-of-day batch rail.", "Wires", "Create", "Start FedNow mission", PaymentRail.FedNow, ProcessingScenario.Standard),
        new("compare-rails", "foundations", "Choose the appropriate rail", "Use a side-by-side decision table to select a rail for different payment needs.", "Beginner", 8,
            ["Compare speed and finality", "Separate message transport from settlement", "Choose a rail from business requirements"],
            ["Open the rail comparison", "Compare limits, formats, and settlement", "Explain which rail you would choose for three example payments"],
            "Which rail is batch-oriented and uses fixed-width records?", ["ACH", "FedNow", "SWIFT CBPR+"],
            "Rail selection is a business decision involving urgency, destination, amount, availability, finality, and data requirements.", "Learning", "Rails", "Compare payment rails"),
        new("rejected-wire", "operations", "Diagnose a rejected wire", "Run a controlled rejection and prove that no customer funds moved.", "Intermediate", 15,
            ["Find the rejection reason", "Verify release of the customer hold", "Distinguish a status message from settlement"],
            ["Create a FedRejects scenario", "Approve and follow it to rejection", "Inspect delivery errors, pacs.002 status, and the absent settlement journal"],
            "What should happen to held customer funds after rejection?", ["They are released", "They become a debit", "They remain held indefinitely"],
            "A rejected payment must release its hold. A rejection status communicates failure; it does not move value.", "Wires", "Create", "Start rejection mission", PaymentRail.Fedwire, ProcessingScenario.FedRejects),
        new("trace-payment", "systems", "Trace one payment end to end", "Follow a correlation ID through web entry, queues, services, settlement, and accounting.", "Intermediate", 18,
            ["Use correlation identifiers", "Map lifecycle events to services", "Reconcile transport delivery with business status"],
            ["Create and approve a payment", "Record its wire and correlation IDs", "Match timeline stages, delivery attempts, ISO messages, and journal entries"],
            "Can a delivered transport message still produce a rejected payment?", ["Yes", "No"],
            "Transport delivery only proves that a message reached its destination. Business validation and settlement can still reject it.", "Wires", "Create", "Start tracing", PaymentRail.Fedwire, ProcessingScenario.PendingThenAccepted),
        new("iso-message-chain", "iso", "Read an ISO message chain", "Connect a payment instruction to its network status messages.", "Intermediate", 14,
            ["Locate the UETR and end-to-end ID", "Correlate pacs.002 with pacs.008", "Interpret accepted, pending, and rejected states"],
            ["Open the ISO explorer", "Inspect field annotations", "Compare payment and status message purposes"],
            "Which message normally carries the customer credit transfer instruction?", ["pacs.008", "pacs.002", "camt.056"],
            "The pacs.008 carries the customer credit transfer; pacs.002 reports processing status and preserves references to the original instruction.", "Learning", "IsoExplorer", "Open ISO explorer"),
        new("swift-route", "iso", "Follow a SWIFT correspondent route", "Observe one UETR moving through a serial correspondent path.", "Advanced", 18,
            ["Separate SWIFT messaging from settlement", "Identify each correspondent leg", "Explain why the original UETR persists"],
            ["Create an international SWIFT payment", "Approve it", "Inspect the correspondent route and per-leg delivery states"],
            "Does SWIFT itself settle the funds in this lab model?", ["No", "Yes"],
            "SWIFT carries structured messages. The lab models settlement as correspondent-position movements, not settlement performed by SWIFT itself.", "Wires", "Create", "Start SWIFT mission", PaymentRail.SwiftCbprPlus, ProcessingScenario.Standard)
    ];

    public static LearningMissionViewModel? Mission(string id) =>
        Missions.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> Hints(string id) =>
    ["Start with the latest lifecycle event; it identifies the current processing boundary.",
     "Compare transport delivery with the business status. They answer different questions.",
     id == "rejected-wire" ? "A rejection should have no settlement journal and should release any hold." : "Use the UETR or correlation ID to connect messages to the original payment."];

    public static IReadOnlyList<string> RoleHandoffs(string id) => id switch
    {
        "first-fedwire" or "fednow-finality" or "rejected-wire" =>
            ["Maker: create and submit the instruction", "Approver: independently review and release", "Operations: verify messages, settlement, and accounting"],
        "swift-route" => ["Maker: capture cross-border party data", "Approver: release the instruction", "Operations: reconcile each correspondent leg"],
        _ => ["Analyst: predict the outcome", "Operator: gather evidence", "Reviewer: challenge the explanation"]
    };

    public static KnowledgeQuestionViewModel Question(string id)
    {
        var mission = Mission(id)!;
        var correct = mission.PredictionChoices.Last();
        if (id is "compare-rails") correct = "ACH";
        if (id is "rejected-wire") correct = "They are released";
        if (id is "trace-payment") correct = "Yes";
        if (id is "iso-message-chain") correct = "pacs.008";
        if (id is "swift-route") correct = "No";
        if (id is "fednow-finality") correct = "Receiver acceptance";
        return new($"{id}-check", mission.PredictionPrompt, mission.PredictionChoices, correct, mission.Debrief);
    }

    public static string ProductionNote(string id) => id switch
    {
        "swift-route" => "Production CBPR+ requires private usage guidelines, signing, sanctions controls, and configured correspondent agreements.",
        "iso-message-chain" => "Lab profile validation is educational and is not network certification or authoritative MyStandards validation.",
        _ => "Production processing adds formal operating rules, entitlements, risk controls, reconciliation, and exception procedures beyond this lab."
    };

    public static LearningDebriefViewModel Debrief(WireTransfer wire) => wire.Status switch
    {
        WireStatus.Completed or WireStatus.Settled => new("Payment settled",
            "The network accepted the instruction and the lab posted balanced debit and credit entries.",
            "Production finality, exception rights, and posting policies depend on the rail and governing rules.",
            ["Confirm the final status message", "Reconcile the journal totals", "Verify the hold is no longer outstanding"]),
        WireStatus.Rejected => new("Payment rejected",
            "Processing stopped before final settlement. Any customer hold should be released and no settlement journal should post.",
            "Production teams also investigate repairability, sanctions decisions, network reason codes, and customer notification obligations.",
            ["Find the first rejection event", "Read the status reason", "Verify that funds did not move"]),
        WireStatus.PendingApproval => new("Awaiting dual control",
            "The maker recorded an instruction, but an independent approver has not released it. No network message or funds hold should exist yet.",
            "Production entitlements commonly enforce amount limits, separation of duties, and auditable approval policies.",
            ["Identify the maker", "Switch to an approver", "Predict what release will create"]),
        WireStatus.PendingAtFed or WireStatus.SentToFed or WireStatus.ReadyForFed => new("Processing is not final",
            "The payment is in flight. Transport success and an intermediate status do not prove settlement.",
            "Operators should use correlated status messages and settlement confirmation—not elapsed time—to determine finality.",
            ["Inspect the latest lifecycle event", "Check delivery attempts", "Look for a final status code"]),
        _ => new("Lifecycle in progress",
            "Use the timeline to determine which service currently owns the payment and what event is expected next.",
            "Production systems require timeouts, reconciliation, and escalation when expected responses do not arrive.",
            ["Find the current owner", "Inspect the latest message", "Predict the next state"])
    };
}
