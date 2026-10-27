using System.Data;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed record TrafficCustomerProfile(string Routing, string Name, int Number, decimal OpeningBalance);

/// <summary>Fictional, repeatable lab opening balances; existing accounts are never replenished.</summary>
public static class TrafficPopulation
{
    public static string AccountNumber(string routing, int number) => $"90{routing}{number:00}";
    public static string AccountLabel(Account account)
    {
        var profile = Profiles.FirstOrDefault(x => AccountNumber(x.Routing, x.Number) == account.AccountNumber);
        return profile?.Number switch { 1 => "Payroll operating", 2 => "Business operating",
            3 => "Utility collections", >= 12 => "Savings", _ => "Checking" };
    }
    public static bool IsPersonChecking(Account account) => Profiles.Any(x => x.Number is >= 4 and <= 11
        && AccountNumber(x.Routing, x.Number) == account.AccountNumber);
    public static bool IsSupplier(Account account) => Profiles.Any(x => x.Number == 2
        && AccountNumber(x.Routing, x.Number) == account.AccountNumber);
    public static IReadOnlyList<TrafficCustomerProfile> Profiles { get; } =
    [
        new("101000019", "Greenway Labs", 1, 450000m),
        new("101000019", "Maple Supply Co", 2, 180000m),
        new("101000019", "Cedar Utilities", 3, 125000m),
        new("101000019", "Avery Brooks", 4, 3500m),
        new("101000019", "Avery Brooks", 12, 1800m),
        new("101000019", "Jordan Ellis", 5, 4875m),
        new("101000019", "Jordan Ellis", 13, 2750m),
        new("101000019", "Morgan Reed", 6, 6250m),
        new("101000019", "Morgan Reed", 14, 3700m),
        new("101000019", "Taylor Hayes", 7, 7625m),
        new("101000019", "Taylor Hayes", 15, 4650m),
        new("101000019", "Casey Bennett", 8, 9000m),
        new("101000019", "Casey Bennett", 16, 5600m),
        new("101000019", "Riley Carter", 9, 10375m),
        new("101000019", "Riley Carter", 17, 6550m),
        new("101000019", "Cameron Price", 10, 11750m),
        new("101000019", "Cameron Price", 18, 7500m),
        new("101000019", "Quinn Parker", 11, 13125m),
        new("101000019", "Quinn Parker", 19, 8450m),
        new("103000648", "Prairie Software", 1, 450000m),
        new("103000648", "Summit Supply Co", 2, 180000m),
        new("103000648", "Prairie Energy", 3, 125000m),
        new("103000648", "Sofia Martinez", 4, 3500m),
        new("103000648", "Sofia Martinez", 12, 1800m),
        new("103000648", "Liam Walker", 5, 4875m),
        new("103000648", "Liam Walker", 13, 2750m),
        new("103000648", "Emma Davis", 6, 6250m),
        new("103000648", "Emma Davis", 14, 3700m),
        new("103000648", "Noah Thompson", 7, 7625m),
        new("103000648", "Noah Thompson", 15, 4650m),
        new("103000648", "Olivia Patel", 8, 9000m),
        new("103000648", "Olivia Patel", 16, 5600m),
        new("103000648", "Ethan Nguyen", 9, 10375m),
        new("103000648", "Ethan Nguyen", 17, 6550m),
        new("103000648", "Mia Robinson", 10, 11750m),
        new("103000648", "Mia Robinson", 18, 7500m),
        new("103000648", "Lucas Chen", 11, 13125m),
        new("103000648", "Lucas Chen", 19, 8450m),
        new("111901234", "Oakridge Design", 1, 450000m),
        new("111901234", "Harbor Supply Co", 2, 180000m),
        new("111901234", "Oakridge Water", 3, 125000m),
        new("111901234", "Isabella Lewis", 4, 3500m),
        new("111901234", "Isabella Lewis", 12, 1800m),
        new("111901234", "Mason Scott", 5, 4875m),
        new("111901234", "Mason Scott", 13, 2750m),
        new("111901234", "Amelia Young", 6, 6250m),
        new("111901234", "Amelia Young", 14, 3700m),
        new("111901234", "Logan Allen", 7, 7625m),
        new("111901234", "Logan Allen", 15, 4650m),
        new("111901234", "Harper King", 8, 9000m),
        new("111901234", "Harper King", 16, 5600m),
        new("111901234", "James Wright", 9, 10375m),
        new("111901234", "James Wright", 17, 6550m),
        new("111901234", "Evelyn Hill", 10, 11750m),
        new("111901234", "Evelyn Hill", 18, 7500m),
        new("111901234", "Benjamin Green", 11, 13125m),
        new("111901234", "Benjamin Green", 19, 8450m),
        new("111000753", "Riverbend Health", 1, 450000m),
        new("111000753", "River Supply Co", 2, 180000m),
        new("111000753", "Riverbend Power", 3, 125000m),
        new("111000753", "Charlotte Adams", 4, 3500m),
        new("111000753", "Charlotte Adams", 12, 1800m),
        new("111000753", "Elijah Baker", 5, 4875m),
        new("111000753", "Elijah Baker", 13, 2750m),
        new("111000753", "Abigail Nelson", 6, 6250m),
        new("111000753", "Abigail Nelson", 14, 3700m),
        new("111000753", "Henry Mitchell", 7, 7625m),
        new("111000753", "Henry Mitchell", 15, 4650m),
        new("111000753", "Emily Roberts", 8, 9000m),
        new("111000753", "Emily Roberts", 16, 5600m),
        new("111000753", "Daniel Turner", 9, 10375m),
        new("111000753", "Daniel Turner", 17, 6550m),
        new("111000753", "Elizabeth Phillips", 10, 11750m),
        new("111000753", "Elizabeth Phillips", 18, 7500m),
        new("111000753", "Samuel Campbell", 11, 13125m),
        new("111000753", "Samuel Campbell", 19, 8450m),
    ];

    public static async Task SeedAsync(IDbContextFactory<BankingDbContext> factory, CancellationToken token = default)
    {
        await using var probe = await factory.CreateDbContextAsync(token);
        await probe.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var routings = Profiles.Select(x => x.Routing).Distinct().ToArray();
            var banks = await db.Banks.Where(x => routings.Contains(x.RoutingNumber))
                .Include(x => x.Customers).ThenInclude(x => x.Accounts).ToListAsync(token);
            var accountNumbers = (await db.Accounts.Select(x => x.AccountNumber).ToListAsync(token)).ToHashSet();
            foreach (var bank in banks)
            foreach (var profile in Profiles.Where(x => x.Routing == bank.RoutingNumber))
            {
                var number = AccountNumber(profile.Routing, profile.Number);
                if (!accountNumbers.Add(number)) continue;
                var customer = bank.Customers.FirstOrDefault(x => x.Name == profile.Name);
                if (customer is null)
                {
                    customer = new Customer { BankId = bank.Id, Name = profile.Name };
                    bank.Customers.Add(customer);
                    db.Customers.Add(customer);
                }
                db.Accounts.Add(new Account { Customer = customer, AccountNumber = number,
                    Balance = profile.OpeningBalance, Status = AccountStatus.Open });
            }
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        });
    }
}
