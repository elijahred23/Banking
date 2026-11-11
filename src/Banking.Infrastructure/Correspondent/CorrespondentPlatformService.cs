using Banking.Domain;
using Microsoft.EntityFrameworkCore;
namespace Banking.Infrastructure;

public sealed class CorrespondentPlatformService(IDbContextFactory<BankingDbContext> factory)
{
    public Task<Guid> ProposeAgreementAsync(Guid bank, Guid accountId, decimal limit, string actor, CancellationToken ct) =>
        StatementTransactions.Run(factory, async db => {
            await StatementTransactions.Account(db, bank, accountId, true, ct);
            Money(limit); Actor(actor);
            if (await db.CorrespondentServiceAgreements.AnyAsync(x => x.AccountId == accountId, ct))
                throw new ArgumentException("An agreement already exists. Suspend it to stop new instructions.");
            var agreement = new CorrespondentServiceAgreement { AccountId = accountId, TransferLimit = limit,
                CreatedBy = actor, CreatedAt = await StatementTransactions.Now(db, ct) };
            db.CorrespondentServiceAgreements.Add(agreement);
            await Audit(db, accountId, agreement.Id, bank, actor, "Agreement proposed", ct);
            return agreement.Id;
        }, ct);

    public Task ApproveAgreementAsync(Guid bank, Guid id, string actor, bool suspend, CancellationToken ct) =>
        StatementTransactions.Run(factory, async db => {
            var agreement = await db.CorrespondentServiceAgreements.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Instruction not found.");
            var account = await StatementTransactions.Account(db, bank, agreement.AccountId, false, ct);
            Actor(actor);
            if (suspend) agreement.Suspended = true;
            else {
                if (string.Equals(agreement.CreatedBy, actor, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A different operator must approve the agreement.");
                if (bank == account.IssuerBankId) agreement.IssuerApproved = true;
                else agreement.RespondentApproved = true;
            }
            await Audit(db, account.Id, id, bank, actor, suspend ? "Agreement suspended" : "Agreement approved", ct);
            return true;
        }, ct);

    internal static async Task<CorrespondentServiceAgreement> Eligible(BankingDbContext db, Guid accountId, decimal amount, CancellationToken ct)
    {
        var agreement = await db.CorrespondentServiceAgreements.SingleOrDefaultAsync(x => x.AccountId == accountId, ct);
        if (agreement is null || !agreement.IsActive || amount > agreement.TransferLimit)
            throw new ArgumentException("An active bilaterally approved agreement with sufficient transfer authority is required.");
        return agreement;
    }

    public Task<Guid> RequestBookAsync(Guid bank, Guid sourceId, Guid destinationId, decimal amount, string actor, CancellationToken ct) =>
        StatementTransactions.Run(factory, async db => {
            var source = await StatementTransactions.Account(db, bank, sourceId, false, ct);
            if (bank != source.RespondentBankId) throw new ArgumentException("The source respondent must make the instruction.");
            Money(amount); Actor(actor);
            await Pair(db, source, destinationId, ct);
            await Eligible(db, sourceId, amount, ct);
            await Eligible(db, destinationId, amount, ct);
            if (source.IssuerBalance - source.HeldBalance < amount) throw new ArgumentException("Insufficient available funds.");
            source.HeldBalance += amount;
            var order = new CorrespondentBookOrder { SourceAccountId = sourceId, DestinationAccountId = destinationId,
                Amount = amount, CreatedBy = actor, CreatedAt = await StatementTransactions.Now(db, ct) };
            db.CorrespondentBookOrders.Add(order);
            await Audit(db, source.Id, order.Id, bank, actor, "Book payment requested", ct);
            return order.Id;
        }, ct);

    public Task DecideBookAsync(Guid bank, Guid id, string actor, bool approve, CancellationToken ct) =>
        StatementTransactions.Run(factory, async db => {
            var order = await db.CorrespondentBookOrders.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Instruction not found.");
            var source = await StatementTransactions.Account(db, bank, order.SourceAccountId, true, ct);
            Actor(actor);
            if (string.Equals(order.CreatedBy, actor, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The maker cannot approve its own instruction.");
            if (order.Status != CorrespondentOrderStatus.Pending) return false;
            var destination = await Pair(db, source, order.DestinationAccountId, ct);
            if (approve) {
                await Eligible(db, source.Id, order.Amount, ct); await Eligible(db, destination.Id, order.Amount, ct);
                source.HeldBalance -= order.Amount;
                await Transfer(db, source, destination, order.Amount, order.Id, "Approved internal book transfer", ct);
            } else source.HeldBalance -= order.Amount;
            order.Status = approve ? CorrespondentOrderStatus.Posted : CorrespondentOrderStatus.Rejected;
            order.DecidedBy = actor; order.DecidedAt = await StatementTransactions.Now(db, ct);
            await Audit(db, source.Id, id, bank, actor, approve ? "Book payment approved" : "Book payment rejected", ct);
            return true;
        }, ct);

    public Task<Guid> ProposeFundingAsync(Guid bank, CorrespondentFundingRule input, string actor, CancellationToken ct) =>
        StatementTransactions.Run(factory, async db => {
            var source = await StatementTransactions.Account(db, bank, input.SourceAccountId, false, ct);
            if (bank != source.RespondentBankId) throw new ArgumentException("The funding source respondent must authorize the proposal.");
            await Pair(db, source, input.DestinationAccountId, ct);
            Money(input.Maximum); Actor(actor);
            if (!Enum.IsDefined(input.Strategy) || input.Threshold < 0 || input.Target < 0
                || decimal.Round(input.Target, 4) != input.Target || decimal.Round(input.Threshold, 4) != input.Threshold
                || input.Strategy == CorrespondentFundingStrategy.Threshold && input.Target < input.Threshold)
                throw new ArgumentException("Choose a valid strategy, threshold and target.");
            await Eligible(db, source.Id, input.Maximum, ct); await Eligible(db, input.DestinationAccountId, input.Maximum, ct);
            var now = await StatementTransactions.Now(db, ct);
            if (input.NextRunAt < now) throw new ArgumentException("Choose a future first execution in UTC.");
            var rule = new CorrespondentFundingRule { SourceAccountId = source.Id, DestinationAccountId = input.DestinationAccountId,
                Strategy = input.Strategy, Threshold = input.Threshold, Target = input.Target, Maximum = input.Maximum,
                NextRunAt = input.NextRunAt, CreatedBy = actor };
            db.CorrespondentFundingRules.Add(rule);
            await Audit(db, source.Id, rule.Id, bank, actor, "Funding proposed", ct);
            return rule.Id;
        }, ct);

    public Task ApproveFundingAsync(Guid bank, Guid id, string actor, bool disable, CancellationToken ct) =>
        StatementTransactions.Run(factory, async db => {
            var rule = await db.CorrespondentFundingRules.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Instruction not found.");
            var source = await StatementTransactions.Account(db, bank, rule.SourceAccountId, false, ct);
            Actor(actor);
            if (disable) rule.Enabled = false;
            else {
                if (string.Equals(rule.CreatedBy, actor, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A different operator must approve funding authority.");
                if (bank == source.IssuerBankId) rule.IssuerApproved = true; else rule.SourceApproved = true;
            }
            await Audit(db, source.Id, id, bank, actor, disable ? "Funding disabled" : "Funding approved", ct);
            return true;
        }, ct);

    public async Task ProcessDueAsync(CancellationToken ct)
    {
        await using var probe = await factory.CreateDbContextAsync(ct);
        var now = await StatementTransactions.Now(probe, ct);
        var ids = await probe.CorrespondentFundingRules.Where(x => x.Enabled && x.IssuerApproved && x.SourceApproved && x.NextRunAt <= now)
            .OrderBy(x => x.NextRunAt).Select(x => x.Id).Take(100).ToListAsync(ct);
        foreach (var id in ids)
            await StatementTransactions.Run(factory, async db => {
                var rule = await db.CorrespondentFundingRules.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Instruction not found.");
                var clock = await StatementTransactions.Now(db, ct);
                if (!rule.Enabled || !rule.IssuerApproved || !rule.SourceApproved || rule.NextRunAt > clock) return false;
                var run = new CorrespondentFundingRun { RuleId = id, ScheduledFor = rule.NextRunAt, CreatedAt = clock };
                var source = await db.CorrespondentAccounts.SingleAsync(x => x.Id == rule.SourceAccountId, ct);
                var destination = await Pair(db, source, rule.DestinationAccountId, ct);
                try {
                    await Eligible(db, source.Id, rule.Maximum, ct); await Eligible(db, destination.Id, rule.Maximum, ct);
                    if (clock.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) run.Outcome = "Skipped: weekend";
                    else {
                        run.Amount = CorrespondentFundingPolicy.Calculate(rule.Strategy, source.IssuerBalance - source.HeldBalance,
                            destination.IssuerBalance - destination.HeldBalance, rule.Threshold, rule.Target, rule.Maximum);
                        if (run.Amount > 0) await Transfer(db, source, destination, run.Amount, run.Id, "Authorized automated funding", ct);
                        run.Outcome = run.Amount > 0 ? "Posted" : "Skipped: no eligible liquidity or funding need";
                    }
                } catch (ArgumentException ex) {
                    run.Outcome = "Skipped: " + ex.Message;
                    db.OperationsCases.Add(new OperationsCase { BankId = source.IssuerBankId, Rail = "Correspondent",
                        CaseType = "Funding authorization", Reference = "FUND:" + run.Id.ToString("N"),
                        Summary = "Scheduled funding requires review.", Notes = run.Outcome,
                        DueDate = clock.AddDays(1) });
                }
                db.CorrespondentFundingRuns.Add(run);
                rule.NextRunAt = CorrespondentFundingPolicy.NextWeekday(clock);
                return true;
            }, ct);
    }

    private static async Task<CorrespondentAccount> Pair(BankingDbContext db, CorrespondentAccount source, Guid destinationId, CancellationToken ct) =>
        await db.CorrespondentAccounts.SingleOrDefaultAsync(x => x.Id == destinationId && x.Id != source.Id && x.IssuerBankId == source.IssuerBankId, ct)
        ?? throw new ArgumentException("Select a different respondent account at the same correspondent.");

    private static async Task Transfer(BankingDbContext db, CorrespondentAccount source, CorrespondentAccount destination,
        decimal amount, Guid correlation, string reference, CancellationToken ct)
    {
        if (source.IssuerBalance - source.HeldBalance < amount) throw new ArgumentException("Insufficient available source liquidity.");
        var now = await StatementTransactions.Now(db, ct);
        var valueDate = DateOnly.FromDateTime(now.UtcDateTime);
        // Each side has its own posting identity; the common correlation joins both balanced journals.
        foreach (var (account, delta, offset) in new[] { (source, -amount, destination), (destination, amount, source) }) {
            var posting = Guid.NewGuid();
            CorrespondentPostingService.Post(db, account, account.IssuerBankId, posting, correlation, delta, now, valueDate,
                null, reference, "CORRESPONDENT_BOOK_CLEARING");
            account.IssuerBalance += delta;
            db.OutboxMessages.Add(Outbox.Message(StatementQueues.Posting, new CorrespondentPostingNotice(account.Id, posting), account.RespondentBankId));
        }
    }
    private static async Task Audit(BankingDbContext db, Guid account, Guid subject, Guid bank, string actor, string action, CancellationToken ct) =>
        db.CorrespondentAuthorizationEvents.Add(new CorrespondentAuthorizationEvent { AccountId = account, SubjectId = subject,
            BankId = bank, Actor = actor, Action = action, CreatedAt = await StatementTransactions.Now(db, ct) });
    private static void Money(decimal amount) { if (amount <= 0 || decimal.Round(amount, 4) != amount) throw new ArgumentException("Use a positive amount with at most four decimal places."); }
    private static void Actor(string actor) { if (string.IsNullOrWhiteSpace(actor) || actor.Length > 120) throw new ArgumentException("A named operator is required."); }
}
