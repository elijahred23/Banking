using System.Data;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed class StudioDayService(IDbContextFactory<BankingDbContext> factory)
{
    public async Task StartAsync(Guid bankId, string kind, string actor, bool useRoster, CancellationToken token, Guid? campaignId = null)
    {
        if (!StudioDayPolicy.ValidKind(kind)) throw new InvalidOperationException("Choose a wire or ACH studio.");
        await Mutate(async db =>
        {
            if (await db.StudioDays.AnyAsync(x => x.BankId == bankId && x.Kind == kind && x.EndedAt == null, token))
                throw new InvalidOperationException("A banking day is already active. Continue or close it first.");
            if (useRoster && (kind != "Ach" || !await db.PayrollMembers.AnyAsync(x => x.BankId == bankId && x.Enabled, token)))
                throw new InvalidOperationException("No enabled payroll roster exists at this bank. Use generated payroll instead.");
            StudioCampaign? campaign = null;
            var businessDate = StudioPolicy.BankingDate(DateOnly.FromDateTime((await db.SimulationStates.SingleAsync(x => x.Name == "default", token)).CurrentUtc.UtcDateTime));
            if (campaignId is Guid selectedCampaign)
            {
                campaign = await db.StudioCampaigns.SingleOrDefaultAsync(x => x.Id == selectedCampaign && x.BankId == bankId && x.Kind == kind, token)
                    ?? throw new InvalidOperationException("Select a campaign owned by this bank and studio.");
                var previous = await db.StudioDays.Where(x => x.BankId == bankId && x.CampaignId == selectedCampaign).OrderBy(x => x.StartedAt).ToListAsync(token);
                if (previous.Count >= campaign.PlannedDays) throw new InvalidOperationException("This campaign has generated all its planned days.");
                businessDate = previous.Count == 0 ? campaign.FirstDate : AchBusinessCalendar.NextBankingDay(previous[^1].BusinessDate);
            }
            var bank = await db.Banks.SingleAsync(x => x.Id == bankId, token);
            var accounts = await db.Accounts.Where(x => x.Customer.BankId == bankId).ToListAsync(token);
            var day = new StudioDay { BankId = bankId, Kind = kind, BusinessDate = businessDate, CampaignId = campaign?.Id,
                StartedBy = actor, OpeningCash = bank.MasterAccountBalance,
                OpeningCustomerBalance = accounts.Sum(x => x.Balance), OpeningHolds = accounts.Sum(x => x.HeldBalance) };
            db.StudioDays.Add(day);
            await Configure(db, day, useRoster, token);
        }, token);
    }

    public Task AdvanceAsync(Guid bankId, Guid id, int expectedPhase, bool close, CancellationToken token) => Mutate(async db =>
    {
        var day = await db.StudioDays.SingleOrDefaultAsync(x => x.Id == id && x.BankId == bankId && x.EndedAt == null, token)
            ?? throw new InvalidOperationException("This banking day is no longer active.");
        if (day.Phase != expectedPhase) throw new InvalidOperationException("The phase has changed. Refresh before advancing again.");
        var settings = await db.StudioSettings.SingleAsync(x => x.BankId == bankId && x.Kind == day.Kind, token);
        if (!close && settings.RemainingRuns > 0)
            throw new InvalidOperationException("This phase still has scheduled activity. Wait for generation, or close the day to carry work forward.");
        if (!close && day.PendingIncident != "None")
            throw new InvalidOperationException("An incident is still queued. Let a payment consume it or close the day to cancel it.");
        if (close || day.Phase == 3)
        {
            day.EndedAt = DateTimeOffset.UtcNow;
            if (day.PendingIncidentCaseId is Guid caseId)
            {
                var item = await db.OperationsCases.SingleAsync(x => x.Id == caseId && x.BankId == bankId, token);
                item.Status = OperationsCaseStatus.Resolved; item.ResolvedDate = day.EndedAt; item.UpdatedDate = day.EndedAt.Value;
                item.Resolution = "Queued incident canceled when generation closed; no payment was injected.";
                day.PendingIncident = "None"; day.PendingIncidentCaseId = null;
            }
            await Pause(db, bankId, day.Kind, token);
            settings.RemainingRuns = null; settings.Preset = "Custom"; settings.UpdatedDate = DateTimeOffset.UtcNow;
        }
        else
        {
            day.Phase++;
            await Configure(db, day, settings.UseRoster, token);
        }
    }, token);

    public Task QueueIncidentAsync(Guid bankId, Guid id, string incidentId, CancellationToken token) => Mutate(async db =>
    {
        var day = await db.StudioDays.SingleOrDefaultAsync(x => x.Id == id && x.BankId == bankId && x.EndedAt == null, token)
            ?? throw new InvalidOperationException("Start a banking day before queuing an incident.");
        var definition = StudioDayPolicy.Incident(day.Kind, incidentId) ?? throw new InvalidOperationException("Choose a supported incident for this rail.");
        var settings = await db.StudioSettings.SingleAsync(x => x.BankId == bankId && x.Kind == day.Kind, token);
        if (day.Phase == 3 || settings.RemainingRuns == 0) throw new InvalidOperationException("Advance to a phase with remaining generation before queuing an incident.");
        if (day.PendingIncident != "None") throw new InvalidOperationException("An incident is already waiting for the next generated payment/run.");
        var item = new OperationsCase { BankId = bankId, Rail = day.Kind, CaseType = "StudioIncident", Reference = day.Id.ToString(),
            Summary = $"SIMULATED: {definition.Title}", Notes = $"Queued for the next generated {(day.Kind == "Wire" ? "wire" : "ACH run")}. {definition.Evidence}",
            DueDate = DateTimeOffset.UtcNow.AddHours(1), Severity = OperationsCaseSeverity.High };
        db.OperationsCases.Add(item);
        day.PendingIncident = definition.Id; day.PendingIncidentCaseId = item.Id;
    }, token);

    public Task ReviewCaseAsync(Guid bankId, Guid id, string actor, string notes, bool resolve, CancellationToken token) => Mutate(async db =>
    {
        var item = await db.OperationsCases.SingleOrDefaultAsync(x => x.BankId == bankId && x.Id == id && (x.Rail == "Wire" || x.Rail == "Ach"), token)
            ?? throw new InvalidOperationException("The case is no longer available at this bank.");
        if (string.IsNullOrWhiteSpace(notes) || notes.Trim().Length is < 12 or > 500)
            throw new InvalidOperationException("Record 12–500 characters of investigation evidence before updating the case.");
        if (await db.StudioDays.AnyAsync(x => x.BankId == bankId && x.PendingIncidentCaseId == item.Id, token))
            throw new InvalidOperationException("This incident is still queued. Wait for injection or close its banking day to cancel it.");
        var entry = $"[{DateTimeOffset.UtcNow:u} · {actor}] {notes.Trim()}";
        var combined = string.IsNullOrEmpty(item.Notes) ? entry : item.Notes + "\n" + entry;
        if (combined.Length > 2000) throw new InvalidOperationException("This case's note history is full. Retain its history and open a follow-up case in Operations.");
        item.Notes = combined; item.AssignedTo = actor; item.UpdatedDate = DateTimeOffset.UtcNow;
        item.Status = resolve ? OperationsCaseStatus.Resolved : OperationsCaseStatus.Investigating;
        item.ResolvedDate = resolve ? item.UpdatedDate : null; item.Resolution = resolve ? notes.Trim() : null;
    }, token);

    private static async Task Configure(BankingDbContext db, StudioDay day, bool useRoster, CancellationToken token)
    {
        var plan = StudioDayPolicy.Phases[day.Phase];
        var settings = await db.StudioSettings.SingleOrDefaultAsync(x => x.BankId == day.BankId && x.Kind == day.Kind, token);
        if (settings is null) { settings = new StudioSettings { BankId = day.BankId, Kind = day.Kind }; db.StudioSettings.Add(settings); }
        settings.Preset = "BankingDay"; settings.Rail = PaymentRail.Fedwire;
        settings.WireScenario = StudioWireScenario.Normal; settings.AchScenario = AchProcessingScenario.Standard;
        StudioCampaignPlan? authored = null;
        if (day.CampaignId is Guid campaignId)
        {
            var campaign = await db.StudioCampaigns.SingleAsync(x => x.Id == campaignId && x.BankId == day.BankId, token);
            authored = StudioCampaignPlan.Parse(campaign.PlanJson, day.Kind);
        }
        settings.UseRoster = useRoster; settings.RemainingRuns = day.Kind == "Wire" ? plan.WireCount : plan.AchRuns;
        if (authored is not null && day.Phase < 3) settings.RemainingRuns = authored.Counts[day.Phase];
        settings.UpdatedDate = DateTimeOffset.UtcNow;
        if (authored is not null && authored.IncidentPhase == day.Phase)
        {
            var definition = StudioDayPolicy.Incident(day.Kind, authored.Incident)!;
            var item = new OperationsCase { BankId = day.BankId, Rail = day.Kind, CaseType = "StudioIncident", Reference = day.Id.ToString(), Summary = $"SIMULATED authored incident: {definition.Title}", Notes = definition.Evidence, DueDate = DateTimeOffset.UtcNow.AddHours(1) };
            db.OperationsCases.Add(item); day.PendingIncident = definition.Id; day.PendingIncidentCaseId = item.Id;
        }
        await Pause(db, day.BankId, day.Kind, token);
        var name = day.Kind == "Wire" ? WireTraffic.StateName(day.BankId) : AchTraffic.StateName(day.BankId, plan.Activity);
        var state = db.SimulationStates.Local.FirstOrDefault(x => x.Name == name)
            ?? await db.SimulationStates.SingleOrDefaultAsync(x => x.Name == name, token);
        if (state is null) { state = new SimulationState { Name = name }; db.SimulationStates.Add(state); }
        state.IsPaused = day.Phase == 3;
        state.SpeedMultiplier = authored?.Rate ?? (day.Kind == "Wire" ? plan.WireRate : plan.AchRate);
        state.CurrentUtc = DateTimeOffset.UtcNow; state.UpdatedDate = DateTimeOffset.UtcNow;
    }

    private static async Task Pause(BankingDbContext db, Guid bankId, string kind, CancellationToken token)
    {
        var prefix = kind == "Wire" ? WireTraffic.StateName(bankId) : AchTraffic.BankPrefix(bankId);
        var states = await db.SimulationStates.Where(x => x.Name.StartsWith(prefix)).ToListAsync(token);
        foreach (var state in states) { state.IsPaused = true; state.UpdatedDate = DateTimeOffset.UtcNow; }
    }

    private async Task Mutate(Func<BankingDbContext, Task> action, CancellationToken token)
    {
        await using var probe = await factory.CreateDbContextAsync(token);
        await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            await action(db); await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        });
    }
}
