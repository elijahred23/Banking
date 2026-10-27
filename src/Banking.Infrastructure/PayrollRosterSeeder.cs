using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public static class PayrollRosterSeeder
{
    public static async Task SeedAsync(IDbContextFactory<BankingDbContext> factory, CancellationToken token = default)
    {
        await using var probe = factory.CreateDbContext();
        await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, token);
            var accounts = await db.Accounts.Include(x => x.Customer).ThenInclude(x => x.Bank).ToListAsync(token);
            var clock = await db.SimulationStates.SingleAsync(x => x.Name == "default", token);
            var existing = await db.PayrollMembers.ToListAsync(token);
            foreach (var employer in accounts.Where(x => x.AccountNumber == TrafficPopulation.AccountNumber(x.Customer.Bank.RoutingNumber, 1)))
            {
                var employees = accounts.Where(x => x.Customer.BankId != employer.Customer.BankId && TrafficPopulation.IsPersonChecking(x))
                    .OrderBy(x => x.Customer.Name).Take(8).ToList();
                for (var i = 0; i < employees.Count; i++)
                {
                    var employee = employees[i];
                    if (existing.Any(x => x.BankId == employer.Customer.BankId && x.EmployerAccountId == employer.Id && x.CheckingAccountId == employee.Id)) continue;
                    var savings = accounts.FirstOrDefault(x => x.CustomerId == employee.CustomerId && TrafficPopulation.AccountLabel(x) == "Savings");
                    db.PayrollMembers.Add(new PayrollMember { BankId = employer.Customer.BankId, EmployerAccountId = employer.Id,
                        CheckingAccountId = employee.Id, SavingsAccountId = savings?.Id, NetPay = 1250m + i * 173.27m,
                        SavingsPercent = savings is null ? 0m : 10m,
                        NextPayDate = StudioPolicy.BankingDate(DateOnly.FromDateTime(clock.CurrentUtc.UtcDateTime)) });
                }
            }
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });
    }
}
