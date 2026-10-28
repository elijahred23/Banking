using System.Data;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed class StudioCampaignService(IDbContextFactory<BankingDbContext> factory)
{
    public Task AuthorAsync(Guid bankId, string kind, string title, string json, string actor, CancellationToken token) => Mutate(async db =>
    {
        Text(title, 3, 100); var plan = StudioCampaignPlan.Parse(json, kind);
        db.StudioScenarios.Add(new StudioScenario { BankId = bankId, Kind = kind, Title = title.Trim(), PlanJson = plan.Serialize(), CreatedBy = actor });
        await Task.CompletedTask;
    }, token);
    public Task CreateCampaignAsync(Guid bankId, Guid scenarioId, int days, DateOnly firstDate, CancellationToken token) => Mutate(async db =>
    {
        if (days is < 2 or > 10 || !AchBusinessCalendar.IsBankingDay(firstDate)) throw new InvalidOperationException("Choose 2–10 days and a banking-day start date.");
        var scenario = await db.StudioScenarios.SingleOrDefaultAsync(x => x.BankId == bankId && x.Id == scenarioId, token)
            ?? throw new InvalidOperationException("Select a scenario owned by this bank.");
        StudioCampaignPlan.Parse(scenario.PlanJson, scenario.Kind);
        db.StudioCampaigns.Add(new StudioCampaign { BankId = bankId, Kind = scenario.Kind, Title = scenario.Title, PlanJson = scenario.PlanJson, PlannedDays = days, FirstDate = firstDate });
    }, token);
    public Task AuthorizeAsync(Guid bankId, Guid accountId, string routing, string account, decimal limit, DateOnly validThrough, string actor, CancellationToken token) => Mutate(async db =>
    {
        Text(account, 1, 17);
        if (!AbaRoutingNumberValidator.IsValid(routing) || account.Any(char.IsControl) || limit <= 0 || limit > 1000000m || decimal.Round(limit, 2) != limit)
            throw new InvalidOperationException("Use a valid routing number and a positive, two-decimal per-entry limit up to $1,000,000.");
        if (!await db.Accounts.AnyAsync(x => x.Id == accountId && x.Customer.BankId == bankId && x.Status == AccountStatus.Open, token))
            throw new InvalidOperationException("Select an open collection account owned by this bank.");
        var now = (await db.SimulationStates.SingleAsync(x => x.Name == "default", token)).CurrentUtc;
        if (validThrough < DateOnly.FromDateTime(now.UtcDateTime)) throw new InvalidOperationException("Authorization must not expire before the shared lab date.");
        db.StudioDebitAuthorizations.Add(new StudioDebitAuthorization { BankId = bankId, OriginatingAccountId = accountId,
            Routing = routing, Account = account, Limit = limit, ValidThrough = validThrough, GrantedBy = actor });
    }, token);
    public Task RevokeAsync(Guid bankId, Guid id, CancellationToken token) => Mutate(async db =>
    {
        var item = await db.StudioDebitAuthorizations.SingleOrDefaultAsync(x => x.BankId == bankId && x.Id == id, token)
            ?? throw new InvalidOperationException("Authorization not found at this bank.");
        item.RevokedAt ??= DateTimeOffset.UtcNow;
    }, token);

    public async Task<Guid> RepairAsync(Guid bankId, Guid originalId, string routing, string account, DateOnly date, string reason, string actor, CancellationToken token)
    {
        Guid replacementId = default;
        await Mutate(async db =>
        {
            Text(reason, 12, 500);
            var original = await db.AchEntries.Include(x => x.Batch).ThenInclude(x => x!.File)
                .SingleOrDefaultAsync(x => x.Id == originalId && x.OriginatingBankId == bankId, token)
                ?? throw new InvalidOperationException("Original entry not found at this bank.");
            var prior = await db.StudioAchRepairs.SingleOrDefaultAsync(x => x.BankId == bankId && x.OriginalEntryId == originalId, token);
            if (prior is not null) { replacementId = prior.ReplacementEntryId; return; }
            if (await db.AchLedgerEntries.AnyAsync(x => x.Entry.OriginatingBankId == bankId && x.AchEntryId == originalId, token)) throw new InvalidOperationException("An entry with posted journal evidence cannot be resubmitted through file repair.");
            var replacement = StudioRepairPolicy.Replacement(original, routing, account, date, actor);
            var clock = await db.SimulationStates.SingleAsync(x => x.Name == "default", token);
            var errors = AchRules.Validate(replacement, DateOnly.FromDateTime(clock.CurrentUtc.UtcDateTime));
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors.Select(x => x.Message)));
            var source = await db.Accounts.Include(x => x.Customer).SingleAsync(x => x.Id == original.OriginatingAccountId && x.Customer.BankId == bankId, token);
            if (source.Status != AccountStatus.Open || AchRules.IsCredit(replacement.TransactionCode) && !AccountAvailabilityPolicy.CanDebit(source, replacement.Amount))
                throw new InvalidOperationException("The source account must be open and have available funds for a credit.");
            replacement.ReceivingBankId = await db.Banks.Where(x => x.RoutingNumber == routing).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(token);
            db.AchEntries.Add(replacement);
            db.StudioAchRepairs.Add(new StudioAchRepair { BankId = bankId, OriginalEntryId = originalId, ReplacementEntryId = replacement.Id, CreatedBy = actor, Reason = reason.Trim() });
            replacementId = replacement.Id;
        }, token);
        return replacementId;
    }

    public Task ConversationAsync(Guid bankId, Guid wireId, string text, string actor, CancellationToken token) => Mutate(async db =>
    {
        Text(text, 12, 500);
        var wire = await db.WireTransfers.SingleOrDefaultAsync(x => x.Id == wireId && x.BankId == bankId && x.Direction == WireDirection.Outgoing, token)
            ?? throw new InvalidOperationException("Select an outgoing wire owned by this bank.");
        if (!WireCasePolicy.CanInvestigate(wire)) throw new InvalidOperationException("Wait until this wire reaches a stage that can be investigated.");
        var pending = await db.StudioNotes.AnyAsync(x => x.BankId == bankId && x.WireId == wireId && x.Kind == "ReceiverResponse" && x.AvailableAt > DateTimeOffset.UtcNow, token);
        if (pending) throw new InvalidOperationException("A simulated receiver response is still pending; wait before sending another request.");
        var day = await db.StudioDays.Where(x => x.BankId == bankId && x.Kind == "Wire" && x.StartedAt <= wire.CreatedDate && (x.EndedAt == null || x.EndedAt >= wire.CreatedDate)).OrderByDescending(x => x.StartedAt).FirstOrDefaultAsync(token);
        var campaign = day?.CampaignId is Guid campaignId ? await db.StudioCampaigns.SingleAsync(x => x.Id == campaignId && x.BankId == bankId, token) : null;
        var plan = campaign is null ? StudioCampaignPlan.Default("Wire") : StudioCampaignPlan.Parse(campaign.PlanJson, "Wire");
        db.StudioNotes.Add(new StudioNote { BankId = bankId, CampaignId = campaign?.Id, WireId = wireId, Kind = "WireQuestion", Actor = actor, Text = text.Trim() });
        var context = text.Contains(wire.CorrelationId.ToString(), StringComparison.OrdinalIgnoreCase)
            ? $"Correlated trace received. The originating lab record was {wire.Status} at {DateTimeOffset.UtcNow:u}. This snapshot is not current beneficiary-posting confirmation."
            : $"Please supply correlation {wire.CorrelationId} and the specific missing evidence in your follow-up.";
        db.StudioNotes.Add(new StudioNote { BankId = bankId, CampaignId = campaign?.Id, WireId = wireId, Kind = "ReceiverResponse", Actor = "Simulated receiving-bank desk",
            AvailableAt = DateTimeOffset.UtcNow.AddSeconds(plan.ResponseSeconds(wire.ReceiverBankId)), Text = context + " Cancellation does not imply funds returned. Use the authorized wire case workflow for actual ISO requests/returns." });
    }, token);
    public Task HandoffAsync(Guid bankId, Guid campaignId, string recipientRole, string text, string actor, CancellationToken token) => Mutate(async db =>
    {
        Text(text, 12, 1000);
        if (recipientRole is not ("PaymentCreator" or "PaymentApprover" or "ComplianceOfficer" or "Operations")) throw new InvalidOperationException("Choose a supported role.");
        if (!await db.StudioCampaigns.AnyAsync(x => x.Id == campaignId && x.BankId == bankId, token)) throw new InvalidOperationException("Select a campaign owned by this bank.");
        db.StudioNotes.Add(new StudioNote { BankId = bankId, CampaignId = campaignId, Kind = "Handoff", RecipientRole = recipientRole, Actor = actor, Text = text.Trim() });
    }, token);
    public Task FundingAsync(Guid bankId, decimal amount, string reason, string actor, CancellationToken token) => Mutate(async db =>
    {
        Text(reason, 12, 500);
        if (amount <= 0 || amount > 100000000m || decimal.Round(amount, 2) != amount) throw new InvalidOperationException("Request a positive, two-decimal amount up to $100,000,000.");
        db.StudioFundingRequests.Add(new StudioFundingRequest { BankId = bankId, Amount = amount, Reason = reason.Trim(), RequestedBy = actor });
        await Task.CompletedTask;
    }, token);
    public Task ReconcileFundingAsync(Guid bankId, Guid id, Guid wireId, CancellationToken token) => Mutate(async db =>
    {
        var request = await db.StudioFundingRequests.SingleOrDefaultAsync(x => x.Id == id && x.BankId == bankId, token)
            ?? throw new InvalidOperationException("Funding request not found at this bank.");
        if (request.IncomingWireId is not null) return;
        var wire = await db.WireTransfers.SingleOrDefaultAsync(x => x.Id == wireId && x.BankId == bankId && x.Direction == WireDirection.Incoming, token);
        if (wire is null || wire.TransferType != WireTransferType.FinancialInstitutionCreditTransfer || !StudioDayPolicy.Settled(wire.Status)
            || wire.Amount < request.Amount || wire.CreatedDate < request.CreatedAt)
            throw new InvalidOperationException("Match a settled incoming financial-institution wire with the funding reference, amount, and date.");
        if (!await db.WireTransfers.AnyAsync(x => x.CorrelationId == wire.CorrelationId && x.Direction == WireDirection.Outgoing && x.ReceiverBankId == bankId && x.CustomerReference == request.Reference && x.Amount == wire.Amount && x.TransferType == WireTransferType.FinancialInstitutionCreditTransfer && (x.Status == WireStatus.Settled || x.Status == WireStatus.Completed), token))
            throw new InvalidOperationException("The incoming wire must correlate to the requested funding reference.");
        var lines = await db.LedgerEntries.Where(x => x.WireTransferId == wire.Id && x.WireTransfer.BankId == bankId).ToListAsync(token);
        if (lines.Count < 2 || lines.GroupBy(x => x.JournalId).Any(g => g.Sum(x => x.Debit) != g.Sum(x => x.Credit) || g.Sum(x => x.Debit) <= 0))
            throw new InvalidOperationException("Balanced posting evidence is required before reconciliation.");
        if (await db.StudioFundingRequests.AnyAsync(x => x.BankId == bankId && x.IncomingWireId == wireId, token)) throw new InvalidOperationException("That incoming wire is already matched to a funding request.");
        request.IncomingWireId = wireId; request.ReconciledAt = DateTimeOffset.UtcNow;
    }, token);
    private static void Text(string? text, int min, int max)
    { if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < min || text.Trim().Length > max || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) throw new InvalidOperationException($"Enter {min}–{max} printable characters."); }
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
