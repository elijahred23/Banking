using System.Data;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

/// <summary>Durable bank-scoped schedules use SimulationStates under wire-traffic:{bankId}.
/// CurrentUtc is the next generation time; SpeedMultiplier is wires/minute (1–12).
/// The default learning clock is never changed.</summary>
public sealed class WireTrafficService(IDbContextFactory<BankingDbContext> factory)
{
    public async Task TickAsync(Guid bankId, CancellationToken token)
    {
        await using var probe = await factory.CreateDbContextAsync(token);
        await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var name = WireTraffic.StateName(bankId);
            var state = await db.SimulationStates.SingleOrDefaultAsync(x => x.Name == name, token);
            if (state is null || state.IsPaused) return;
            var now = DateTimeOffset.UtcNow;
            var settings = await db.StudioSettings.SingleOrDefaultAsync(x => x.BankId == bankId && x.Kind == "Wire", token);
            var day = await db.StudioDays.SingleOrDefaultAsync(x => x.BankId == bankId && x.Kind == "Wire" && x.EndedAt == null, token);
            var campaign = day?.CampaignId is Guid campaignId ? await db.StudioCampaigns.SingleAsync(x => x.Id == campaignId && x.BankId == bankId, token) : null;
            var campaignPlan = campaign is null ? null : StudioCampaignPlan.Parse(campaign.PlanJson, "Wire");
            var incident = day is not null ? StudioDayPolicy.Incident("Wire", day.PendingIncident) : null;
            // Release only this bank's simulator-made wires, on a later tick than creation.
            var pending = await db.WireTransfers.Include(x => x.Events).Where(x =>
                x.BankId == bankId && x.CreatedBy == WireTraffic.Maker
                && x.Status == WireStatus.PendingApproval).ToListAsync(token);
            foreach (var wire in pending)
            {
                WireTraffic.Approve(wire, now);
                db.WireEvents.Add(wire.Events[^1]);
                db.OutboxMessages.Add(Outbox.Message(Queues.WireCreated, new WireCreated(wire.Id), bankId));
            }
            var outstanding = await db.WireTransfers.CountAsync(x => x.BankId == bankId
                && x.CreatedBy == WireTraffic.Maker && x.Status != WireStatus.Settled
                && x.Status != WireStatus.Rejected && x.Status != WireStatus.Returned, token);
            if (settings?.RemainingRuns == 0) state.IsPaused = true;
            if (!state.IsPaused && state.CurrentUtc <= now && outstanding < 5)
            {
                var rail = settings?.Rail ?? PaymentRail.Fedwire;
                if (settings?.Preset == "RailComparison")
                {
                    rail = new[] { PaymentRail.Fedwire, PaymentRail.FedNow, PaymentRail.SwiftCbprPlus }[(3 - (settings.RemainingRuns ?? 3)) % 3];
                }
                var sources = await db.Accounts.Include(x => x.Customer).ThenInclude(x => x.Bank)
                    .Where(x => x.Customer.BankId == bankId && (rail == PaymentRail.SwiftCbprPlus || x.Customer.Bank.CountryCode == "US")
                        && x.Status == AccountStatus.Open).ToListAsync(token);
                var destinations = await db.Accounts.Include(x => x.Customer).ThenInclude(x => x.Bank)
                    .Where(x => x.Customer.BankId != bankId && (rail == PaymentRail.SwiftCbprPlus || x.Customer.Bank.CountryCode == "US")
                        && x.Status == AccountStatus.Open).ToListAsync(token);
                if (rail == PaymentRail.FedNow)
                {
                    sources = sources.Where(x => x.Customer.Bank.FedNowEnabled && x.Customer.Bank.FedNowSendEnabled).ToList();
                    destinations = destinations.Where(x => x.Customer.Bank.FedNowEnabled && x.Customer.Bank.FedNowReceiveEnabled && x.Customer.Bank.FedNowOnline).ToList();
                }
                if (rail == PaymentRail.SwiftCbprPlus)
                {
                    sources = sources.Where(x => x.Customer.Bank.SwiftEnabled).ToList();
                    destinations = destinations.Where(x => x.Customer.Bank.SwiftEnabled).ToList();
                }
                var eligible = sources.Where(x => WireTraffic.Amount(x) >= 1m
                    && (rail == PaymentRail.SwiftCbprPlus || PaymentRiskPolicy.WithinFedwireLiquidity(x.Customer.Bank, WireTraffic.Amount(x)))).ToList();
                if (eligible.Count > 0 && destinations.Count > 0)
                {
                    var source = eligible[Random.Shared.Next(eligible.Count)];
                    var destination = destinations[Random.Shared.Next(destinations.Count)];
                    if (rail == PaymentRail.SwiftCbprPlus)
                    {
                        var resolver = new PaymentRouteResolver(factory);
                        foreach (var candidate in destinations.OrderBy(_ => Random.Shared.Next()))
                        {
                            try { await resolver.ResolveRouteAsync(bankId, candidate.Customer.BankId, "USD", CorrespondentRails.Swift, token); destination = candidate; break; }
                            catch (InvalidOperationException) { /* The normal wire service records unavailable-route rejections. */ }
                        }
                    }
                    var wire = WireTraffic.Create(source, destination, now, rail: rail,
                        scenario: incident?.WireScenario is StudioWireScenario fault ? StudioPolicy.Scenario(fault)
                            : campaignPlan is not null ? campaignPlan.WireScenario(destination.Customer.BankId)
                            : settings is null ? ProcessingScenario.PendingThenAccepted : StudioPolicy.Scenario(settings.WireScenario),
                        complianceReview: (incident?.WireScenario ?? settings?.WireScenario) == StudioWireScenario.ComplianceReview);
                    db.WireTransfers.Add(wire);
                    if (incident is not null && day?.PendingIncidentCaseId is Guid incidentCaseId)
                    {
                        var item = await db.OperationsCases.SingleAsync(x => x.Id == incidentCaseId && x.BankId == bankId, token);
                        StudioDayPolicy.ConsumeIncident(day, item, wire.Id, now);
                    }
                    if (settings?.RemainingRuns is > 0) settings.RemainingRuns--;
                }
                state.CurrentUtc = now.AddSeconds(60d / (double)Math.Clamp(state.SpeedMultiplier, 1m, 12m));
            }
            state.UpdatedDate = now;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });
    }
}
