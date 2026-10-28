namespace Banking.Domain;

// Fictional, deterministic teaching cases. These never drive the payment workflow.
public sealed record StudioPracticeStep(string Title, string Evidence, decimal Held, decimal Posted,
    string Message, string Question, string[] Choices, int Answer, string Explanation);
public sealed record StudioPracticeCase(string Id, string Title, string Objective, StudioPracticeStep[] Steps);

public static class StudioPractice
{
    public const decimal OpeningBalance = 1000m;
    public const decimal Amount = 125m;

    public static IReadOnlyList<StudioPracticeCase> Cases(bool ach)
    {
        var steps = new[]
        {
            Step("Captured", "A maker records a fictional $125 credit payment. No journal exists.", 0, 0,
                "Internal instruction", "Who should authorize this payment?", ["The same maker", "A separate approver", "The transport queue"], 1,
                "A separate authorized approver preserves maker/approver separation."),
            Step("Approved and reserved", "Approval and validation reserve $125; the posted customer balance remains $1,000.", Amount, 0,
                ach ? "Approved entry → SEC/effective-date batch" : "Approved instruction → supported ISO profile",
                "What is the sender's available balance?", ["$1,000", "$875", "$750"], 1,
                "Available balance is posted balance less holds: $1,000 − $125 = $875."),
            Step("Transport delivered", "The transport has delivered the message. Business acceptance has not yet arrived.", Amount, 0,
                ach ? "NACHA file delivered to simulated operator" : "Payment message delivered to simulated rail",
                "Does transport delivery justify customer posting?", ["Yes", "No"], 1,
                "Delivery proves transport progress. Acceptance and settlement require their own evidence."),
            Step("Business accepted", "Final business acceptance is recorded; this teaching replay pauses before settlement/posting.", Amount, 0,
                "Positive business outcome", "Which evidence is still needed?", ["A new maker", "Settlement and a balanced journal", "Another copy of the delivery"], 1,
                "Acceptance and settlement are distinct concepts. The live lab may record these events close together."),
            Step("Settled and posted", "Settlement cash decreases $125. The sender's posted balance decreases $125 and the hold releases.", 0, Amount,
                ach ? "Accepted entry settlement / posting" : "Settlement / beneficiary delivery evidence",
                "Which originating-bank journal balances?", ["Debit deposits $125; credit settlement cash $125", "Debit deposits $125 only", "Credit both accounts $125"], 0,
                "For this outgoing credit example, debit the customer deposit liability and credit settlement cash. The receiving bank has its own balanced posting.")
        };
        StudioPracticeCase Branch(string id, string title, string objective, int keep, StudioPracticeStep ending)
            => new(id, title, objective, [.. steps.Take(keep), ending]);
        var cases = new List<StudioPracticeCase>
        {
            new("normal", "Normal credit payment", "Trace a $125 credit from instruction through settlement and explain each balance change.", steps),
            Branch("delayed", "Delayed acceptance", "Separate a delivered message from an accepted payment.", 3,
                Step("Awaiting outcome", "Delivery exists, but no final acceptance or settlement evidence has arrived.", Amount, 0, "Pending status / investigation",
                    "What should operations do next?", ["Post now", "Trace the existing payment and retain the hold", "Create a second payment"], 1,
                    "Investigate the correlated payment and its status. A retry must preserve idempotency; a new payment could duplicate value.")),
            Branch("rejected", ach ? "File control failure" : "Business rejection", "Explain rejection before settlement and release the reservation.", 3,
                Step("Rejected; hold released", ach ? "File control validation failed. No entry in this teaching file settles." : "The rail rejects the payment before settlement.", 0, 0,
                    ach ? "File rejection / control totals" : "Negative business outcome",
                    "What should the sender's posted balance be?", ["$875", "$1,000"], 1,
                    "A pre-settlement rejection releases the applicable hold without posting customer or settlement accounting.")),
            Branch("duplicate", "Duplicate delivery", "Recognize why duplicate transport must not duplicate settlement.", 5,
                Step("Duplicate ignored", "The same correlated delivery arrives again; the existing posting is retained.", 0, Amount, "Duplicate correlation detected",
                    "How many settlement postings should exist?", ["One", "Two"], 0,
                    "Idempotency prevents another journal or balance movement for the same processed instruction."))
        };
        if (ach)
        {
            cases.Add(Branch("return", "Account closed return", "Distinguish a post-settlement return from a file rejection.", 5,
                Step("Return reconciled", "A fictional account-closed return reverses the original $125 movement using a separate correlated journal.", 0, 0, "R02 teaching case",
                    "How should the original journal be handled?", ["Delete it", "Keep it and add a balanced reversing journal"], 1,
                    "Returns retain the original audit trail. This simplified credit example restores sender funds; actual return timing and eligibility depend on the entry and rules.")));
            cases.Add(Branch("noc", "Corrected account notification", "Learn why corrected future instructions do not reverse settled value.", 5,
                Step("Correction received", "The receiving bank supplies corrected account information for future entries. The original payment remains posted.", 0, Amount,
                    "Notification of change", "What should happen to the settled payment?", ["Reverse it", "Keep it; review corrected details for future entries"], 1,
                    "A notification of change communicates corrections. It is distinct from a return and does not itself move money.")));
        }
        else
        {
            cases.Add(Branch("compliance", "Compliance review", "Identify who can clear a compliance hold before transmission.", 2,
                Step("Screening hold", "A screening alert prevents transmission pending an authorized compliance decision.", Amount, 0, "Internal compliance alert",
                    "Who may resolve the alert?", ["Any maker", "An authorized compliance reviewer"], 1,
                    "A learning persona does not grant permissions. Only an authorized reviewer may clear or reject an actual alert.")));
            cases.Add(Branch("recall", "Cancellation after settlement", "Understand why a cancellation request is not an automatic refund.", 5,
                Step("Cancellation requested", "Operations opens a correlated case after settlement. No return of funds has been confirmed.", 0, Amount, "Cancellation / investigation request",
                    "Can the sender be credited immediately?", ["Yes, the request is enough", "No, wait for an authorized confirmed return workflow"], 1,
                    "A request does not undo settlement. Follow the rail-specific response and return workflow, keeping the case and original journal auditable.")));
        }
        return cases;
    }

    private static StudioPracticeStep Step(string title, string evidence, decimal held, decimal posted,
        string message, string question, string[] choices, int answer, string explanation)
        => new(title, evidence, held, posted, message, question, choices, answer, explanation);
}
