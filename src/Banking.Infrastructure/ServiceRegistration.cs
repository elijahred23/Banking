using Banking.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Banking.Infrastructure;

public static class ServiceRegistration
{
    public static IServiceCollection AddBankingInfrastructure(this IServiceCollection services,
        IConfiguration configuration)
    {
        var sql = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");
        services.AddDbContextFactory<BankingDbContext>(options =>
            options.UseSqlServer(sql, sqlOptions => sqlOptions.EnableRetryOnFailure()));
        services.AddSingleton<IMessageBus, RabbitMqMessageBus>();
        services.AddSingleton<IIsoMessageService, IsoMessageService>();
        services.AddSingleton<IFedNowMessageService, FedNowMessageService>();
        services.AddSingleton<ICbprPlusMessageService, CbprPlusMessageService>();
        return services;
    }

    public static async Task InitializeDatabaseAsync(this IServiceProvider services,
        bool seed = false, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<BankingDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await UpgradeLabSchemaAsync(db, cancellationToken);
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
        if (seed) await EnsureInternationalBanksAsync(db, cancellationToken);
    }

    private static Task<int> UpgradeLabSchemaAsync(BankingDbContext db, CancellationToken token) =>
        db.Database.ExecuteSqlRawAsync("""
            IF COL_LENGTH(N'dbo.Accounts', N'HeldBalance') IS NULL
                ALTER TABLE dbo.Accounts ADD HeldBalance decimal(19,4) NOT NULL
                    CONSTRAINT DF_Accounts_HeldBalance DEFAULT (0);
            IF COL_LENGTH(N'dbo.WireTransfers', N'BeneficiaryAccountNumber') IS NULL
                ALTER TABLE dbo.WireTransfers ADD BeneficiaryAccountNumber nvarchar(24) NOT NULL
                    CONSTRAINT DF_WireTransfers_BeneficiaryAccountNumber DEFAULT (N'UNKNOWN');
            IF COL_LENGTH(N'dbo.WireTransfers', N'Scenario') IS NULL
                ALTER TABLE dbo.WireTransfers ADD Scenario nvarchar(max) NOT NULL
                    CONSTRAINT DF_WireTransfers_Scenario DEFAULT (N'Standard');
            IF COL_LENGTH(N'dbo.WireTransfers', N'Rail') IS NULL
                ALTER TABLE dbo.WireTransfers ADD Rail nvarchar(max) NOT NULL
                    CONSTRAINT DF_WireTransfers_Rail DEFAULT (N'Fedwire');
            IF EXISTS (SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID(N'dbo.Accounts') AND name = N'AccountNumber'
                    AND max_length < 68)
            BEGIN
                IF EXISTS (SELECT 1 FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'dbo.Accounts') AND name = N'IX_Accounts_AccountNumber')
                    DROP INDEX IX_Accounts_AccountNumber ON dbo.Accounts;
                ALTER TABLE dbo.Accounts ALTER COLUMN AccountNumber nvarchar(34) NOT NULL;
                CREATE UNIQUE INDEX IX_Accounts_AccountNumber ON dbo.Accounts (AccountNumber);
            END;
            IF EXISTS (SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID(N'dbo.WireTransfers') AND name = N'BeneficiaryAccountNumber'
                    AND max_length < 68)
                ALTER TABLE dbo.WireTransfers ALTER COLUMN BeneficiaryAccountNumber nvarchar(34) NOT NULL;
            IF COL_LENGTH(N'dbo.Banks', N'FedNowEnabled') IS NULL
            BEGIN
                ALTER TABLE dbo.Banks ADD
                    FedNowEnabled bit NOT NULL CONSTRAINT DF_Banks_FedNowEnabled DEFAULT (1),
                    FedNowSendEnabled bit NOT NULL CONSTRAINT DF_Banks_FedNowSendEnabled DEFAULT (1),
                    FedNowReceiveEnabled bit NOT NULL CONSTRAINT DF_Banks_FedNowReceiveEnabled DEFAULT (1),
                    FedNowRequestForPaymentEnabled bit NOT NULL CONSTRAINT DF_Banks_FedNowRfpEnabled DEFAULT (1),
                    FedNowOnline bit NOT NULL CONSTRAINT DF_Banks_FedNowOnline DEFAULT (1);
            END;
            IF COL_LENGTH(N'dbo.Banks', N'Bic') IS NULL
            BEGIN
                ALTER TABLE dbo.Banks ADD
                    Bic nvarchar(11) NOT NULL CONSTRAINT DF_Banks_Bic DEFAULT (N'UNKNOWNXX'),
                    TownName nvarchar(35) NOT NULL CONSTRAINT DF_Banks_TownName DEFAULT (N'Unknown'),
                    CountryCode nvarchar(2) NOT NULL CONSTRAINT DF_Banks_CountryCode DEFAULT (N'US'),
                    SwiftEnabled bit NOT NULL CONSTRAINT DF_Banks_SwiftEnabled DEFAULT (1);
            END;
            EXEC(N'UPDATE dbo.Banks SET
                Bic = CASE RoutingNumber
                    WHEN N''101000019'' THEN N''BAKRUS44XXX''
                    WHEN N''103000648'' THEN N''FIOKUS44XXX''
                    WHEN N''111901234'' THEN N''CNATUS44XXX''
                    WHEN N''111000753'' THEN N''RRBAUS44XXX''
                    ELSE N''ZZZZUS'' + RIGHT(RoutingNumber, 5)
                END,
                TownName = CASE WHEN RoutingNumber = N''103000648'' THEN N''Oklahoma City''
                    WHEN TownName = N''Unknown'' THEN N''Tulsa'' ELSE TownName END
            WHERE Bic = N''UNKNOWNXX'';');
            IF NOT EXISTS (SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'dbo.Banks') AND name = N'IX_Banks_Bic')
                EXEC(N'CREATE UNIQUE INDEX IX_Banks_Bic ON dbo.Banks (Bic);');
            IF OBJECT_ID(N'dbo.LedgerEntries', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.LedgerEntries (
                    Id uniqueidentifier NOT NULL CONSTRAINT PK_LedgerEntries PRIMARY KEY,
                    JournalId uniqueidentifier NOT NULL,
                    WireTransferId uniqueidentifier NOT NULL,
                    AccountCode nvarchar(80) NOT NULL,
                    AccountName nvarchar(120) NOT NULL,
                    Debit decimal(19,4) NOT NULL,
                    Credit decimal(19,4) NOT NULL,
                    Description nvarchar(240) NOT NULL,
                    CreatedDate datetimeoffset NOT NULL,
                    CONSTRAINT FK_LedgerEntries_WireTransfers_WireTransferId FOREIGN KEY (WireTransferId)
                        REFERENCES dbo.WireTransfers (Id) ON DELETE CASCADE,
                    CONSTRAINT CK_LedgerEntries_OneSide CHECK
                        ((Debit > 0 AND Credit = 0) OR (Credit > 0 AND Debit = 0))
                );
                CREATE INDEX IX_LedgerEntries_WireTransferId ON dbo.LedgerEntries (WireTransferId);
                CREATE INDEX IX_LedgerEntries_JournalId ON dbo.LedgerEntries (JournalId);
            END;
            """, token);

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
        if (banks.Count == 0) return;
        db.Banks.AddRange(banks);
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
