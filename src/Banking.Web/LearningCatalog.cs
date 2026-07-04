using Banking.Domain;
using Banking.Web.Models;

namespace Banking.Web;

public static class LearningCatalog
{
    public static IReadOnlyList<LearningPathViewModel> Paths { get; } =
    [
        new("foundations", "Payment foundations", "New learners", "Learn the payment lifecycle, settlement finality, and the major rails.", ["first-fedwire", "fednow-finality", "compare-rails", "ach-file", "check-clearing"]),
        new("operations", "Payment operations", "Operations teams", "Practice approval, exception handling, observability, and reconciliation.", ["first-fedwire", "rejected-wire", "trace-payment", "ach-return", "check-duplicate"]),
        new("iso", "ISO 20022", "Analysts and engineers", "Connect business actions to pacs messages, identifiers, and status codes.", ["first-fedwire", "iso-message-chain", "swift-route", "liquidity-transfer", "nonvalue-request"]),
        new("accounting", "Settlement accounting", "Finance and operations", "Follow holds, master-account settlement, and balanced journals.", ["fednow-finality", "rejected-wire", "trace-payment", "accounting-journal", "ach-file", "check-clearing"]),
        new("systems", "Distributed payment systems", "Software engineers", "Trace messages across services, queues, retries, and idempotent settlement.", ["trace-payment", "rejected-wire", "swift-route", "idempotent-replay", "nonvalue-request"])
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
            "SWIFT carries structured messages. The lab models settlement as correspondent-position movements, not settlement performed by SWIFT itself.", "Wires", "Create", "Start SWIFT mission", PaymentRail.SwiftCbprPlus, ProcessingScenario.Standard),
        new("ach-file", "foundations", "Build and settle an ACH file", "Trace an entry through its batch, fixed-width file, settlement, and journal.", "Intermediate", 18,
            ["Distinguish entry, batch, and file", "Find the trace number and effective date", "Reconcile ACH settlement accounting"],
            ["Create a standard ACH credit", "Observe batch and file assignment", "Inspect control records and the balanced journal"],
            "What is the ACH transport unit sent to the operator?", ["An individual JSON event", "A pacs.008 message", "A NACHA file containing batches"],
            "ACH transport is file-oriented: entries are grouped into batches and serialized into fixed-width NACHA records.", "Ach", "Create", "Start ACH file mission"),
        new("ach-return", "operations", "Investigate an ACH exception", "Distinguish RDFI returns from notifications of change and original file rejection.", "Intermediate", 18,
            ["Interpret return and NOC codes", "Separate original settlement from later return", "Find any reversing journal"],
            ["Create an ACH return or NOC scenario", "Follow the original entry and exception record", "Explain whether value reversed"],
            "Does a notification of change itself reverse the payment?", ["Always", "Only after operator approval", "No; it communicates corrected information"],
            "A notification of change communicates corrected receiving information; a return sends value back through a distinct workflow.", "Ach", "Create", "Start ACH exception mission"),
        new("check-clearing", "foundations", "Clear an image check", "Follow capture, image validation, cash-letter transport, presentment, and settlement.", "Intermediate", 20,
            ["Separate capture from paying-bank decision", "Identify the image cash letter", "Reconcile check settlement"],
            ["Generate test TIFFs and capture a check", "Inspect the event replay and cash letter", "Switch bank perspective and verify the journal"],
            "What crosses the check exchange in this lab?", ["An ACH entry", "A pacs.008 message", "An image cash letter with check images"],
            "Checks remain an image-cash-letter rail in this lab and are not represented as ACH or ISO credit transfers.", "Check", "Instructions", "Start check clearing mission"),
        new("check-duplicate", "operations", "Detect duplicate presentment", "Run a duplicate-presentment scenario and prove that value is not posted twice.", "Advanced", 18,
            ["Identify duplicate keys", "Find the paying-bank return", "Verify idempotent accounting"],
            ["Capture a check using DuplicatePresentment", "Inspect the return evidence", "Verify no second value journal posted"],
            "What is the essential accounting invariant for a duplicate?", ["Create a compensating third payment", "Leave both journals posted", "Do not post the duplicate value"],
            "Duplicate detection must prevent a second settlement rather than relying on an operator to notice duplicate accounting later.", "Check", "Instructions", "Start duplicate check mission"),
        new("liquidity-transfer", "iso", "Move bank liquidity with pacs.009", "Contrast a financial-institution transfer with a customer pacs.008 payment.", "Advanced", 18,
            ["Identify pacs.009", "Explain why no customer account is debited", "Reconcile master-account movement"],
            ["Create a financial institution credit transfer", "Release it with dual control", "Inspect the message and settlement evidence"],
            "Which balance funds this lab transfer?", ["A beneficiary customer hold", "An ACH suspense account", "The sending bank master account"],
            "A pacs.009 institution transfer moves bank liquidity; it does not originate from a customer account.", "Wires", "Create", "Start liquidity mission", PaymentRail.Fedwire, ProcessingScenario.Standard),
        new("nonvalue-request", "iso", "Trace a non-value conversation", "Follow a request and response without mistaking messages for settlement.", "Intermediate", 14,
            ["Correlate request and response", "Identify the absence of settlement", "Explain the business purpose"],
            ["Create a request-for-payment or report request", "Inspect both ISO messages", "Verify that no value journal exists"],
            "Does a request-for-payment itself settle funds?", ["Yes, on delivery", "Yes, on acknowledgement", "No; a separate payment is required"],
            "Non-value messages can request, report, or acknowledge business activity without moving funds.", "Wires", "MessageWorkflows", "Start non-value mission"),
        new("accounting-journal", "accounting", "Reconstruct a payment journal", "Use a completed payment to connect settlement with balanced debit and credit lines.", "Advanced", 16,
            ["Explain every journal line", "Prove debits equal credits", "Connect the journal to one payment"],
            ["Create and settle a payment", "Open the learning workbench", "Reconstruct its T-account view"],
            "What makes a posted journal acceptable in this lab?", ["At least one debit", "A network reference", "Equal debits and credits with payment correlation"],
            "Balance is necessary, and correlation makes the posting auditable back to the originating payment.", "Wires", "Create", "Start accounting mission", PaymentRail.Fedwire, ProcessingScenario.Standard),
        new("idempotent-replay", "systems", "Reason about duplicate delivery", "Use controlled scenarios and durable inbox evidence to prove at-most-once business effects.", "Advanced", 20,
            ["Distinguish delivery attempts from business effects", "Inspect inbox and dead-letter evidence", "Verify settlement idempotency"],
            ["Run a standard payment", "Inspect the operations recovery controls", "Explain why replay must not create another journal"],
            "What may increase during a safe duplicate delivery?", ["Settlement amount", "Number of customer debits", "Delivery attempts, but not business postings"],
            "A consumer may observe repeated transport attempts while its durable inbox and settlement keys prevent duplicate business effects.", "Wires", "Create", "Start idempotency mission", PaymentRail.Fedwire, ProcessingScenario.Standard)
    ];

    public static LearningMissionViewModel? Mission(string id) =>
        Missions.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<string> Hints(string id) => id switch
    {
        "ach-file" or "ach-return" => ["Start with the entry status and trace number.", "Then follow its batch and file; those are distinct durable layers.", "A return, NOC, and file rejection have different value effects."],
        "check-clearing" or "check-duplicate" => ["Use the MICR identity and image hashes to identify the item.", "Separate capture, cash-letter delivery, and paying-bank decision.", "A duplicate must not produce a second value journal."],
        "nonvalue-request" => ["Identify the request message and expected response type.", "Correlate the persisted conversation before interpreting status.", "Verify the absence of a settlement journal."],
        _ => ["Start with the latest lifecycle event; it identifies the current processing boundary.",
            "Compare transport delivery with the business status. They answer different questions.",
            id == "rejected-wire" ? "A rejection should have no settlement journal and should release any hold." : "Use the UETR or correlation ID to connect messages to the original payment."]
    };

    public static IReadOnlyList<string> RoleHandoffs(string id) => id switch
    {
        "first-fedwire" or "fednow-finality" or "rejected-wire" =>
            ["Maker: create and submit the instruction", "Approver: independently review and release", "Operations: verify messages, settlement, and accounting"],
        "swift-route" => ["Maker: capture cross-border party data", "Approver: release the instruction", "Operations: reconcile each correspondent leg"],
        "ach-file" or "ach-return" => ["Originator: capture the entry", "ACH service: group and serialize the file", "Operations: reconcile settlement and exceptions"],
        "check-clearing" or "check-duplicate" => ["Depository bank: capture images and MICR", "Exchange: route the cash letter", "Paying bank: settle or return the item"],
        _ => ["Analyst: predict the outcome", "Operator: gather evidence", "Reviewer: challenge the explanation"]
    };

    public static KnowledgeQuestionViewModel Question(string id, int variant = 0)
    {
        var mission = Mission(id)!;
        if (variant % 2 == 1)
        {
            const string correctEvidence = "A correlated, balanced value journal";
            var choices = Rotate(new[] { "A delivered transport message", "Elapsed processing time", correctEvidence }, variant);
            return new($"{id}-evidence", "Which evidence proves that value was posted in this lab?", choices,
                correctEvidence, "Transport and status evidence establish processing state; a correlated balanced journal establishes accounting movement.",
                new Dictionary<string, string>
                {
                    [choices[0]] = "Delivery proves transport, not settlement or posting.",
                    [choices[1]] = "Elapsed time is never evidence of finality.",
                    [choices[2]] = "Correct: the journal proves the accounting effect and its correlation."
                });
        }
        var correct = mission.PredictionChoices.Last();
        if (id is "compare-rails") correct = "ACH";
        if (id is "rejected-wire") correct = "They are released";
        if (id is "trace-payment") correct = "Yes";
        if (id is "iso-message-chain") correct = "pacs.008";
        if (id is "swift-route") correct = "No";
        if (id is "fednow-finality") correct = "Receiver acceptance";
        var orderedChoices = Rotate(mission.PredictionChoices, variant);
        return new($"{id}-check", mission.PredictionPrompt, orderedChoices, correct, mission.Debrief,
            mission.PredictionChoices.ToDictionary(x => x, x => x == correct
                ? $"Correct. {mission.Debrief}"
                : $"That choice conflicts with the expected rail behavior. {mission.Debrief}"));
    }

    private static IReadOnlyList<string> Rotate(IReadOnlyList<string> choices, int variant)
    {
        if (choices.Count < 2) return choices;
        var offset = Math.Abs(variant) % choices.Count;
        return choices.Skip(offset).Concat(choices.Take(offset)).ToList();
    }

    public static string ProductionNote(string id) => id switch
    {
        "swift-route" => "Production CBPR+ requires private usage guidelines, signing, sanctions controls, and configured correspondent agreements.",
        "iso-message-chain" => "Lab profile validation is educational and is not network certification or authoritative MyStandards validation.",
        "ach-file" or "ach-return" => "This narrow NACHA/FedACH-style model is not Nacha rules compliance, operator certification, or a complete risk and return-rights implementation.",
        "check-clearing" or "check-duplicate" => "The readable image-cash-letter format and image checks are teaching subsets, not licensed X9 conformance or Check 21 certification.",
        "nonvalue-request" => "Supported message conversations are learning profiles and do not represent network connectivity, signing, or authoritative usage-guideline validation.",
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
