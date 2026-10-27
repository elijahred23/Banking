using System.Data;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed class AchTrafficService(IDbContextFactory<BankingDbContext> factory)
{
    public async Task TickAsync(Guid bankId, CancellationToken token)
    {
        await using var probe = await factory.CreateDbContextAsync(token);
        await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var prefix = AchTraffic.BankPrefix(bankId);
            var state = await db.SimulationStates.SingleOrDefaultAsync(x => x.Name.StartsWith(prefix) && !x.IsPaused, token);
            if (state is null) return;
            var now = DateTimeOffset.UtcNow;
            var settings = await db.StudioSettings.SingleOrDefaultAsync(x => x.BankId == bankId && x.Kind == "Ach", token);
            var pending = await db.AchEntries.Where(x => x.OriginatingBankId == bankId
                && x.CreatedBy == AchTraffic.Maker && x.Status == AchEntryStatus.PendingApproval).ToListAsync(token);
            foreach (var entry in pending)
            {
                AchTraffic.Approve(entry, now);
                db.OutboxMessages.Add(Outbox.Message(Queues.AchEntryCreated, new AchEntryCreated(entry.Id), bankId));
            }
            var outstanding = await db.AchEntries.CountAsync(x => x.OriginatingBankId == bankId
                && x.CreatedBy == AchTraffic.Maker && x.Status != AchEntryStatus.Posted
                && x.Status != AchEntryStatus.Returned && x.Status != AchEntryStatus.Rejected
                && x.Status != AchEntryStatus.NocReceived, token);
            if (settings?.RemainingRuns == 0) state.IsPaused = true;
            if (!state.IsPaused && state.CurrentUtc <= now && outstanding <= (settings?.UseRoster == true ? 16 : 20))
            {
                var mode = AchTraffic.Mode(state.Name);
                if (mode == AchTrafficMode.Mixed)
                {
                    var modes = Enum.GetValues<AchTrafficMode>().Where(x => x != AchTrafficMode.Mixed).ToArray();
                    mode = modes[Random.Shared.Next(modes.Length)];
                }
                var companyNumber = mode is AchTrafficMode.Payroll or AchTrafficMode.Reimbursements ? 1 : mode == AchTrafficMode.Suppliers ? 2 : 3;
                var routing = await db.Banks.Where(x => x.Id == bankId).Select(x => x.RoutingNumber).SingleAsync(token);
                var number = TrafficPopulation.AccountNumber(routing, companyNumber);
                var company = await db.Accounts.Include(x => x.Customer).ThenInclude(x => x.Bank)
                    .SingleOrDefaultAsync(x => x.Customer.BankId == bankId && x.AccountNumber == number
                        && x.Status == AccountStatus.Open, token);
                var accounts = await db.Accounts.Include(x => x.Customer).ThenInclude(x => x.Bank)
                    .Where(x => x.Customer.BankId != bankId && x.Status == AccountStatus.Open
                        && x.Customer.Bank.CountryCode == "US").ToListAsync(token);
                var candidates = accounts.Where(x => mode == AchTrafficMode.Suppliers
                    ? TrafficPopulation.IsSupplier(x) : TrafficPopulation.IsPersonChecking(x))
                    .OrderBy(_ => Random.Shared.Next()).Take(4).ToList();
                var clock = await db.SimulationStates.SingleAsync(x => x.Name == "default", token);
                var processingDate = DateOnly.FromDateTime(clock.CurrentUtc.UtcDateTime);
                var effectiveDate = settings?.Preset == "FridayPayday" ? StudioPolicy.Friday(processingDate)
                    : settings?.Preset == "MonthEnd" ? StudioPolicy.MonthEnd(processingDate) : StudioPolicy.BankingDate(processingDate);
                var entries = new List<AchEntry>();
                if (company is not null && mode == AchTrafficMode.Payroll && settings?.UseRoster == true)
                {
                    var roster = await db.PayrollMembers.Include(x => x.EmployerAccount).ThenInclude(x => x.Customer).ThenInclude(x => x.Bank)
                        .Include(x => x.CheckingAccount).ThenInclude(x => x.Customer).ThenInclude(x => x.Bank)
                        .Include(x => x.SavingsAccount).ThenInclude(x => x!.Customer).ThenInclude(x => x.Bank)
                        .Where(x => x.BankId == bankId && x.EmployerAccountId == company.Id && x.Enabled && x.NextPayDate <= effectiveDate)
                        .OrderBy(x => x.NextPayDate).ThenBy(x => x.Id).Take(4).ToListAsync(token);
                    decimal committed = 0;
                    foreach (var member in roster)
                    {
                        if (company.AvailableBalance - committed < member.NetPay) continue;
                        try
                        {
                            var pay = StudioPolicy.Payroll(member, processingDate, now);
                            if (pay.Count == 0) continue;
                            entries.AddRange(pay); committed += member.NetPay;
                            member.NextPayDate = StudioPolicy.BankingDate(effectiveDate.AddDays(14));
                        }
                        catch (InvalidOperationException) { /* Invalid/closed roster accounts remain unpaid and visible. */ }
                    }
                }
                else if (company is not null && candidates.Count > 0)
                    entries.AddRange(AchTraffic.CreateRun(company, candidates, mode, processingDate, now));
                foreach (var entry in entries)
                {
                    entry.EffectiveEntryDate = effectiveDate;
                    entry.Scenario = settings?.AchScenario ?? AchProcessingScenario.Standard;
                    // R01 is a debit-return teaching scenario; credit activities use R02/R03 instead.
                    if (entry.Scenario == AchProcessingScenario.InsufficientFunds && AchRules.IsCredit(entry.TransactionCode))
                        entry.Scenario = AchProcessingScenario.AccountClosed;
                }
                db.AchEntries.AddRange(entries);
                if (entries.Count > 0 && settings?.RemainingRuns is > 0) settings.RemainingRuns--;
                state.CurrentUtc = now.AddSeconds(60d / (double)Math.Clamp(state.SpeedMultiplier, 1m, 4m));
            }
            state.UpdatedDate = now;
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });
    }
}
