using Banking.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Banking.Infrastructure;

/// <summary>Coordinates schema initialization and optional teaching data seeding.</summary>
internal static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services,
        bool seed = false, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BankingDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        var upgradeRunner = scope.ServiceProvider.GetRequiredService<LabSchemaUpgradeRunner>();
        await upgradeRunner.UpgradeAsync(db, cancellationToken);
        if (!await db.SimulationStates.AnyAsync(cancellationToken))
        {
            db.SimulationStates.Add(new SimulationState { Name = "default", CurrentUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(cancellationToken);
        }
        if (seed && !await db.Banks.AnyAsync(cancellationToken))
        {
            var bankers = Bank("Bankers Bank", "101000019", "BANKERS", 50_000_000m,
                ("John Smith", "123456", 125_000m));
            var first = Bank("First Oklahoma Bank", "103000648", "FIRSTOK", 40_000_000m,
                ("Mary Jones", "654321", 25_000m));
            var community = Bank("Community National Bank", "111901234", "COMMUNITY", 25_000_000m,
                ("Alice Carter", "445566", 80_000m));
            var redRiver = Bank("Red River Bank", "111000753", "REDRIVER", 30_000_000m,
                ("Robert Lee", "778899", 55_000m));
            db.Banks.AddRange(bankers, first, community, redRiver);
            await db.SaveChangesAsync(cancellationToken);
        }
        if (seed)
        {
            await EnsureInternationalBanksAsync(db, cancellationToken);
            await EnsureCorrespondentRoutesAsync(db, cancellationToken);
            await TrafficPopulation.SeedAsync(factory, cancellationToken);
            await PayrollRosterSeeder.SeedAsync(factory, cancellationToken);
        }
    }

    private static async Task EnsureInternationalBanksAsync(BankingDbContext db,
        CancellationToken cancellationToken)
    {
        var existingBics = await db.Banks.Select(x => x.Bic).ToListAsync(cancellationToken);
        var banks = new List<Bank>();
        if (!existingBics.Contains("EUDMDEFFXXX"))
            banks.Add(InternationalBank("Euro Demo Bank", "000000001", "EURODEMO",
                "EUDMDEFFXXX", "Frankfurt", "DE", "Anna Müller",
                "DE89370400440532013000", 75_000m));
        if (!existingBics.Contains("BRDMGB2LXXX"))
            banks.Add(InternationalBank("Britannia Demo Bank", "000000002", "BRITDEMO",
                "BRDMGB2LXXX", "London", "GB", "James Wilson",
                "GB82WEST12345698765432", 60_000m));
        if (!existingBics.Contains("BNYCUS33XXX"))
            banks.Add(new Bank
            {
                Name = "Big New York Correspondent Bank", RoutingNumber = "000000003",
                FedParticipantId = "BIGNEWYORK", Bic = "BNYCUS33XXX", TownName = "New York",
                CountryCode = "US", MasterAccountBalance = 100_000_000m, SwiftEnabled = true
            });
        if (banks.Count == 0) return;
        db.Banks.AddRange(banks);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task EnsureCorrespondentRoutesAsync(BankingDbContext db,
        CancellationToken cancellationToken)
    {
        var banks = await db.Banks.Where(x => x.Bic == "BAKRUS44XXX" || x.Bic == "FIOKUS44XXX"
                || x.Bic == "BNYCUS33XXX" || x.Bic == "EUDMDEFFXXX" || x.Bic == "BRDMGB2LXXX")
            .ToDictionaryAsync(x => x.Bic, x => x.Id, cancellationToken);
        if (!banks.TryGetValue("BAKRUS44XXX", out var bankers)
            || !banks.TryGetValue("BNYCUS33XXX", out var newYork)) return;

        var desired = new List<(Guid From, Guid To, string Type, int Priority)>();
        if (banks.TryGetValue("FIOKUS44XXX", out var first))
            desired.Add((bankers, first, "Direct", 1));
        if (banks.TryGetValue("EUDMDEFFXXX", out var euro))
            desired.Add((newYork, euro, "Direct", 1));
        if (banks.TryGetValue("BRDMGB2LXXX", out var britannia))
            desired.Add((newYork, britannia, "Direct", 2));
        desired.Add((bankers, newYork, "Intermediary", 1));

        var existing = await db.CorrespondentRelationships
            .Where(x => x.CurrencyCode == "USD" && x.Rail == CorrespondentRails.Swift)
            .Select(x => new { x.FromBankId, x.ToBankId }).ToListAsync(cancellationToken);
        foreach (var edge in desired.Where(edge => !existing.Any(x =>
                     x.FromBankId == edge.From && x.ToBankId == edge.To)))
            db.CorrespondentRelationships.Add(new CorrespondentRelationship
            {
                FromBankId = edge.From, ToBankId = edge.To, CurrencyCode = "USD",
                Rail = CorrespondentRails.Swift, RelationshipType = edge.Type,
                Priority = edge.Priority
            });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Bank InternationalBank(string name, string routing, string participant,
        string bic, string town, string country, string customerName, string iban, decimal balance) => new()
    {
        Name = name, RoutingNumber = routing, FedParticipantId = participant, Bic = bic,
        TownName = town, CountryCode = country, MasterAccountBalance = 0,
        FedNowEnabled = false, FedNowSendEnabled = false, FedNowReceiveEnabled = false,
        FedNowRequestForPaymentEnabled = false, FedNowOnline = false, SwiftEnabled = true,
        Customers = [new Customer { Name = customerName,
            Accounts = [new Account { AccountNumber = iban, Balance = balance }] }]
    };

    private static Bank Bank(string name, string routing, string participant, decimal balance,
        (string Name, string Account, decimal Balance) customer) => new()
    {
        Name = name, RoutingNumber = routing, FedParticipantId = participant,
        Bic = participant switch
        {
            "BANKERS" => "BAKRUS44XXX",
            "FIRSTOK" => "FIOKUS44XXX",
            "COMMUNITY" => "CNATUS44XXX",
            _ => "RRBAUS44XXX"
        },
        TownName = participant == "FIRSTOK" ? "Oklahoma City" : "Tulsa",
        CountryCode = "US",
        MasterAccountBalance = balance,
        Customers = [new Customer { Name = customer.Name,
            Accounts = [new Account { AccountNumber = customer.Account, Balance = customer.Balance }] }]
    };
}
