using System.ComponentModel.DataAnnotations;

namespace Banking.Domain;

public sealed class StudioDay
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BankId { get; set; }
    public Guid? CampaignId { get; set; }
    [MaxLength(8)] public string Kind { get; set; } = "Wire";
    public int Phase { get; set; }
    public DateOnly BusinessDate { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndedAt { get; set; }
    public decimal OpeningCash { get; set; }
    public decimal OpeningCustomerBalance { get; set; }
    public decimal OpeningHolds { get; set; }
    [MaxLength(120)] public string StartedBy { get; set; } = "";
    [MaxLength(32)] public string PendingIncident { get; set; } = "None";
    public Guid? PendingIncidentCaseId { get; set; }
}

public sealed record StudioDayPhase(string Title, string Objective, int WireCount, int AchRuns,
    int WireRate, int AchRate, AchTrafficMode Activity);
public sealed record StudioIncidentDefinition(string Id, string Title, string Evidence,
    StudioWireScenario? WireScenario = null, AchProcessingScenario? AchScenario = null);

public static class StudioDayPolicy
{
    public static bool ValidKind(string kind) => kind is "Wire" or "Ach";
    public static IReadOnlyList<StudioDayPhase> Phases { get; } =
    [
        new("Morning opening", "Release customer wires or payroll; monitor reservations and available funds.", 6, 2, 6, 2, AchTrafficMode.Payroll),
        new("Afternoon business", "Process supplier activity and investigate any outstanding outcomes.", 4, 2, 3, 2, AchTrafficMode.Suppliers),
        new("Deadline window", "Handle the final activity burst and identify work that must carry forward.", 3, 2, 6, 2, AchTrafficMode.Utilities),
        new("Close and reconcile", "Stop new generation, reconcile journals, and explain outstanding items. Released payments keep processing.", 0, 0, 1, 1, AchTrafficMode.Utilities)
    ];
    public static IReadOnlyList<StudioIncidentDefinition> Incidents(string kind) => kind == "Wire"
        ? [new("Delay", "Delayed final acceptance", "Inspect intermediate and final outcomes; do not post on transport delivery.", StudioWireScenario.Delayed),
           new("Reject", "Rail rejection", "Inspect the negative outcome, released hold, and absence of settlement posting.", StudioWireScenario.Rejected),
           new("Duplicate", "Duplicate delivery", "Compare repeated delivery with exactly one settlement and balanced posting.", StudioWireScenario.DuplicateDelivery),
           new("OutOfOrder", "Out-of-order status", "Trace correlation rather than assuming delivery order proves business state.", StudioWireScenario.OutOfOrder),
           new("Compliance", "Screening review", "An authorized compliance reviewer must resolve the alert; operations cannot bypass it.", StudioWireScenario.ComplianceReview)]
        : [new("Reject", "File control mismatch", "Inspect file rejection, applicable hold releases, and absent settlement.", AchScenario: AchProcessingScenario.ControlTotalMismatch),
           new("Closed", "Receiving account closed", "Inspect the entry return and its correlated reversing journal.", AchScenario: AchProcessingScenario.AccountClosed),
           new("Missing", "Receiving account not found", "Compare entry return evidence with the original file outcome.", AchScenario: AchProcessingScenario.AccountNotFound),
           new("Correction", "Receiving account correction", "A notification of change updates future instructions; it does not itself reverse value.", AchScenario: AchProcessingScenario.NocCorrectedAccount)];

    public static StudioIncidentDefinition? Incident(string kind, string id) => Incidents(kind).SingleOrDefault(x => x.Id == id);
    public static bool Settled(WireStatus status) => status is WireStatus.Settled or WireStatus.Completed;
    public static bool Settled(AchEntryStatus status) => status is AchEntryStatus.Settled or AchEntryStatus.Posted or AchEntryStatus.NocReceived;
    public static string CustomerOutcome(WireStatus status) => status switch
    {
        WireStatus.Returned => "Returned", WireStatus.Rejected => "Rejected",
        WireStatus.Settled or WireStatus.Completed => "Settled", _ => "Processing"
    };
    public static string CustomerOutcome(AchEntryStatus status) => status switch
    {
        AchEntryStatus.Returned => "Returned", AchEntryStatus.Rejected => "Rejected",
        AchEntryStatus.NocReceived => "Correction", AchEntryStatus.Settled or AchEntryStatus.Posted => "Settled", _ => "Processing"
    };

    public static void ConsumeIncident(StudioDay day, OperationsCase item, Guid paymentId, DateTimeOffset now)
    {
        if (day.EndedAt is not null || day.PendingIncident == "None" || day.PendingIncidentCaseId != item.Id || item.BankId != day.BankId)
            throw new InvalidOperationException("Only the active bank's queued incident can be consumed.");
        item.Reference = paymentId.ToString();
        item.Notes = $"Injected by the simulated incident director at {now:u}. Inspect payment {paymentId}. {Incident(day.Kind, day.PendingIncident)?.Evidence}";
        item.UpdatedDate = now;
        day.PendingIncident = "None";
        day.PendingIncidentCaseId = null;
    }
}
