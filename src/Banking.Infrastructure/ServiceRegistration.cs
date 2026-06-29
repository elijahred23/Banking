using Banking.Domain;
using Banking.Infrastructure.Checks;
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
        services.AddSingleton<IQueueOperationsMonitor, RabbitMqOperationsMonitor>();
        services.AddSingleton<IIsoMessageService, IsoMessageService>();
        services.AddSingleton<IFedNowMessageService, FedNowMessageService>();
        services.AddSingleton<ICbprPlusMessageService, CbprPlusMessageService>();
        services.AddSingleton<IPaymentRouteResolver, PaymentRouteResolver>();
        services.AddSingleton<NachaFileWriter>();
        services.AddSingleton<NachaFileParser>();
        services.AddSingleton<X937CashLetterWriter>();
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
        if (seed)
        {
            await EnsureInternationalBanksAsync(db, cancellationToken);
            await EnsureCorrespondentRoutesAsync(db, cancellationToken);
        }
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
            IF COL_LENGTH(N'dbo.WireTransfers', N'TransferType') IS NULL
                ALTER TABLE dbo.WireTransfers ADD TransferType nvarchar(max) NOT NULL
                    CONSTRAINT DF_WireTransfers_TransferType DEFAULT (N'CustomerCreditTransfer');
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
            IF OBJECT_ID(N'dbo.AchFiles', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.AchFiles (
                    Id uniqueidentifier NOT NULL CONSTRAINT PK_AchFiles PRIMARY KEY,
                    OriginatingBankId uniqueidentifier NOT NULL,
                    ImmediateDestinationRoutingNumber nvarchar(9) NOT NULL,
                    ImmediateOriginRoutingNumber nvarchar(9) NOT NULL,
                    FileIdModifier nvarchar(1) NOT NULL,
                    RawNachaPayload nvarchar(max) NOT NULL,
                    Status nvarchar(20) NOT NULL,
                    CreatedDate datetimeoffset NOT NULL,
                    CONSTRAINT FK_AchFiles_Banks_OriginatingBankId FOREIGN KEY (OriginatingBankId) REFERENCES dbo.Banks(Id)
                );
                CREATE INDEX IX_AchFiles_CreatedDate ON dbo.AchFiles(CreatedDate);
                CREATE INDEX IX_AchFiles_OriginatingBankId ON dbo.AchFiles(OriginatingBankId);
            END;
            IF OBJECT_ID(N'dbo.AchBatches', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.AchBatches (
                    Id uniqueidentifier NOT NULL CONSTRAINT PK_AchBatches PRIMARY KEY,
                    OriginatingBankId uniqueidentifier NOT NULL,
                    AchFileId uniqueidentifier NULL,
                    SecCode nvarchar(max) NOT NULL,
                    CompanyName nvarchar(16) NOT NULL,
                    CompanyId nvarchar(10) NOT NULL,
                    EffectiveEntryDate date NOT NULL,
                    ServiceClassCode nvarchar(3) NOT NULL,
                    Status nvarchar(20) NOT NULL,
                    BatchNumber int NOT NULL,
                    CONSTRAINT FK_AchBatches_Banks_OriginatingBankId FOREIGN KEY (OriginatingBankId) REFERENCES dbo.Banks(Id),
                    CONSTRAINT FK_AchBatches_AchFiles_AchFileId FOREIGN KEY (AchFileId) REFERENCES dbo.AchFiles(Id)
                );
                CREATE INDEX IX_AchBatches_EffectiveEntryDate ON dbo.AchBatches(EffectiveEntryDate);
                CREATE INDEX IX_AchBatches_OriginatingBankId ON dbo.AchBatches(OriginatingBankId);
                CREATE INDEX IX_AchBatches_AchFileId ON dbo.AchBatches(AchFileId);
            END;
            IF OBJECT_ID(N'dbo.AchEntries', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.AchEntries (
                    Id uniqueidentifier NOT NULL CONSTRAINT PK_AchEntries PRIMARY KEY,
                    OriginatingBankId uniqueidentifier NOT NULL,
                    ReceivingBankId uniqueidentifier NULL,
                    OriginatingAccountId uniqueidentifier NOT NULL,
                    AchBatchId uniqueidentifier NULL,
                    CompanyName nvarchar(16) NOT NULL,
                    CompanyId nvarchar(10) NOT NULL,
                    SecCode nvarchar(max) NOT NULL,
                    ReceiverName nvarchar(22) NOT NULL,
                    ReceivingRoutingNumber nvarchar(9) NOT NULL,
                    ReceivingAccountNumber nvarchar(17) NOT NULL,
                    TransactionCode nvarchar(max) NOT NULL,
                    Amount decimal(19,4) NOT NULL,
                    EntryDescription nvarchar(10) NOT NULL,
                    EffectiveEntryDate date NOT NULL,
                    Addenda05 nvarchar(80) NULL,
                    ReturnCode nvarchar(3) NULL,
                    TraceNumber nvarchar(15) NULL,
                    Status nvarchar(max) NOT NULL,
                    Purpose nvarchar(max) NOT NULL,
                    Scenario nvarchar(max) NOT NULL,
                    CreatedDate datetimeoffset NOT NULL,
                    CONSTRAINT FK_AchEntries_Banks_OriginatingBankId FOREIGN KEY (OriginatingBankId) REFERENCES dbo.Banks(Id),
                    CONSTRAINT FK_AchEntries_Banks_ReceivingBankId FOREIGN KEY (ReceivingBankId) REFERENCES dbo.Banks(Id),
                    CONSTRAINT FK_AchEntries_Accounts_OriginatingAccountId FOREIGN KEY (OriginatingAccountId) REFERENCES dbo.Accounts(Id),
                    CONSTRAINT FK_AchEntries_AchBatches_AchBatchId FOREIGN KEY (AchBatchId) REFERENCES dbo.AchBatches(Id)
                );
                CREATE INDEX IX_AchEntries_TraceNumber ON dbo.AchEntries(TraceNumber);
                CREATE INDEX IX_AchEntries_ReceivingRoutingNumber ON dbo.AchEntries(ReceivingRoutingNumber);
                CREATE INDEX IX_AchEntries_OriginatingBankId ON dbo.AchEntries(OriginatingBankId);
                CREATE INDEX IX_AchEntries_ReceivingBankId ON dbo.AchEntries(ReceivingBankId);
                CREATE INDEX IX_AchEntries_OriginatingAccountId ON dbo.AchEntries(OriginatingAccountId);
                CREATE INDEX IX_AchEntries_AchBatchId ON dbo.AchEntries(AchBatchId);
            END;
            IF OBJECT_ID(N'dbo.AchReturns', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.AchReturns (Id uniqueidentifier NOT NULL CONSTRAINT PK_AchReturns PRIMARY KEY,
                    AchEntryId uniqueidentifier NOT NULL, ReturnCode nvarchar(3) NOT NULL, Reason nvarchar(240) NOT NULL,
                    ReceivedDate datetimeoffset NOT NULL, CONSTRAINT FK_AchReturns_AchEntries_AchEntryId FOREIGN KEY (AchEntryId) REFERENCES dbo.AchEntries(Id) ON DELETE CASCADE);
                CREATE INDEX IX_AchReturns_AchEntryId ON dbo.AchReturns(AchEntryId);
            END;
            IF OBJECT_ID(N'dbo.AchNotificationsOfChange', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.AchNotificationsOfChange (Id uniqueidentifier NOT NULL CONSTRAINT PK_AchNotificationsOfChange PRIMARY KEY,
                    AchEntryId uniqueidentifier NOT NULL, ChangeCode nvarchar(3) NOT NULL, CorrectedData nvarchar(35) NOT NULL,
                    Description nvarchar(240) NOT NULL, ReceivedDate datetimeoffset NOT NULL,
                    CONSTRAINT FK_AchNotificationsOfChange_AchEntries_AchEntryId FOREIGN KEY (AchEntryId) REFERENCES dbo.AchEntries(Id) ON DELETE CASCADE);
                CREATE INDEX IX_AchNotificationsOfChange_AchEntryId ON dbo.AchNotificationsOfChange(AchEntryId);
            END;
            IF EXISTS (SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID(N'dbo.AchNotificationsOfChange') AND name = N'CorrectedData'
                    AND max_length < 70)
                ALTER TABLE dbo.AchNotificationsOfChange ALTER COLUMN CorrectedData nvarchar(35) NOT NULL;
            IF OBJECT_ID(N'dbo.AchLedgerEntries', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.AchLedgerEntries (Id uniqueidentifier NOT NULL CONSTRAINT PK_AchLedgerEntries PRIMARY KEY,
                    JournalId uniqueidentifier NOT NULL, AchEntryId uniqueidentifier NOT NULL, AccountCode nvarchar(80) NOT NULL,
                    AccountName nvarchar(120) NOT NULL, Debit decimal(19,4) NOT NULL, Credit decimal(19,4) NOT NULL,
                    Description nvarchar(240) NOT NULL, CreatedDate datetimeoffset NOT NULL,
                    CONSTRAINT FK_AchLedgerEntries_AchEntries_AchEntryId FOREIGN KEY (AchEntryId) REFERENCES dbo.AchEntries(Id) ON DELETE CASCADE);
                CREATE INDEX IX_AchLedgerEntries_AchEntryId ON dbo.AchLedgerEntries(AchEntryId);
                CREATE INDEX IX_AchLedgerEntries_JournalId ON dbo.AchLedgerEntries(JournalId);
            END;
            IF OBJECT_ID(N'dbo.CheckCashLetters', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.CheckCashLetters (
                    Id uniqueidentifier NOT NULL CONSTRAINT PK_CheckCashLetters PRIMARY KEY,
                    DepositoryBankId uniqueidentifier NOT NULL,
                    DestinationRoutingNumber nvarchar(9) NOT NULL,
                    OriginRoutingNumber nvarchar(9) NOT NULL,
                    FileIdModifier nvarchar(1) NOT NULL,
                    RawX937Payload nvarchar(max) NOT NULL,
                    Status nvarchar(30) NOT NULL,
                    CreatedDate datetimeoffset NOT NULL,
                    CONSTRAINT FK_CheckCashLetters_Banks_DepositoryBankId
                        FOREIGN KEY (DepositoryBankId) REFERENCES dbo.Banks(Id)
                );
                CREATE INDEX IX_CheckCashLetters_CreatedDate ON dbo.CheckCashLetters(CreatedDate);
                CREATE INDEX IX_CheckCashLetters_DepositoryBankId ON dbo.CheckCashLetters(DepositoryBankId);
            END;
            IF OBJECT_ID(N'dbo.CheckDeposits', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.CheckDeposits (
                    Id uniqueidentifier NOT NULL CONSTRAINT PK_CheckDeposits PRIMARY KEY,
                    DepositoryBankId uniqueidentifier NOT NULL,
                    PayingBankId uniqueidentifier NULL,
                    DepositingAccountId uniqueidentifier NOT NULL,
                    CheckCashLetterId uniqueidentifier NULL,
                    DepositorName nvarchar(120) NOT NULL,
                    PayingRoutingNumber nvarchar(9) NOT NULL,
                    PayingAccountNumber nvarchar(34) NOT NULL,
                    CheckNumber nvarchar(15) NOT NULL,
                    RawMicrLine nvarchar(80) NOT NULL,
                    Amount decimal(19,4) NOT NULL,
                    Status nvarchar(max) NOT NULL,
                    Scenario nvarchar(max) NOT NULL,
                    CorrelationId uniqueidentifier NOT NULL,
                    ImageCashLetterPayload nvarchar(max) NULL,
                    ReturnCode nvarchar(10) NULL,
                    ReturnReason nvarchar(240) NULL,
                    CreatedDate datetimeoffset NOT NULL,
                    CONSTRAINT FK_CheckDeposits_Banks_DepositoryBankId FOREIGN KEY (DepositoryBankId) REFERENCES dbo.Banks(Id),
                    CONSTRAINT FK_CheckDeposits_Banks_PayingBankId FOREIGN KEY (PayingBankId) REFERENCES dbo.Banks(Id),
                    CONSTRAINT FK_CheckDeposits_Accounts_DepositingAccountId FOREIGN KEY (DepositingAccountId) REFERENCES dbo.Accounts(Id),
                    CONSTRAINT FK_CheckDeposits_CheckCashLetters_CheckCashLetterId FOREIGN KEY (CheckCashLetterId) REFERENCES dbo.CheckCashLetters(Id)
                );
                CREATE UNIQUE INDEX IX_CheckDeposits_CorrelationId ON dbo.CheckDeposits(CorrelationId);
                CREATE INDEX IX_CheckDeposits_PayingRoutingNumber ON dbo.CheckDeposits(PayingRoutingNumber);
                CREATE INDEX IX_CheckDeposits_DuplicateDetection ON dbo.CheckDeposits(PayingRoutingNumber, PayingAccountNumber, CheckNumber, Amount);
                CREATE INDEX IX_CheckDeposits_DepositoryBankId ON dbo.CheckDeposits(DepositoryBankId);
                CREATE INDEX IX_CheckDeposits_PayingBankId ON dbo.CheckDeposits(PayingBankId);
                CREATE INDEX IX_CheckDeposits_DepositingAccountId ON dbo.CheckDeposits(DepositingAccountId);
                CREATE INDEX IX_CheckDeposits_CheckCashLetterId ON dbo.CheckDeposits(CheckCashLetterId);
            END;
            IF OBJECT_ID(N'dbo.CheckImages', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.CheckImages (
                    Id uniqueidentifier NOT NULL CONSTRAINT PK_CheckImages PRIMARY KEY,
                    CheckDepositId uniqueidentifier NOT NULL,
                    Side nvarchar(10) NOT NULL,
                    Format nvarchar(10) NOT NULL,
                    FileName nvarchar(120) NOT NULL,
                    ContentType nvarchar(80) NOT NULL,
                    Content varbinary(max) NOT NULL,
                    SizeBytes int NOT NULL,
                    Sha256Hash nvarchar(128) NOT NULL,
                    CreatedDate datetimeoffset NOT NULL,
                    CONSTRAINT FK_CheckImages_CheckDeposits_CheckDepositId FOREIGN KEY (CheckDepositId)
                        REFERENCES dbo.CheckDeposits(Id) ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX IX_CheckImages_CheckDepositId_Side ON dbo.CheckImages(CheckDepositId, Side);
            END;
            IF OBJECT_ID(N'dbo.CheckReturns', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.CheckReturns (
                    Id uniqueidentifier NOT NULL CONSTRAINT PK_CheckReturns PRIMARY KEY,
                    CheckDepositId uniqueidentifier NOT NULL,
                    ReturnCode nvarchar(10) NOT NULL,
                    Reason nvarchar(240) NOT NULL,
                    ReceivedDate datetimeoffset NOT NULL,
                    CONSTRAINT FK_CheckReturns_CheckDeposits_CheckDepositId FOREIGN KEY (CheckDepositId)
                        REFERENCES dbo.CheckDeposits(Id) ON DELETE CASCADE
                );
                CREATE INDEX IX_CheckReturns_CheckDepositId ON dbo.CheckReturns(CheckDepositId);
            END;
            IF OBJECT_ID(N'dbo.CheckLedgerEntries', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.CheckLedgerEntries (
                    Id uniqueidentifier NOT NULL CONSTRAINT PK_CheckLedgerEntries PRIMARY KEY,
                    JournalId uniqueidentifier NOT NULL,
                    CheckDepositId uniqueidentifier NOT NULL,
                    AccountCode nvarchar(80) NOT NULL,
                    AccountName nvarchar(120) NOT NULL,
                    Debit decimal(19,4) NOT NULL,
                    Credit decimal(19,4) NOT NULL,
                    Description nvarchar(240) NOT NULL,
                    CreatedDate datetimeoffset NOT NULL,
                    CONSTRAINT FK_CheckLedgerEntries_CheckDeposits_CheckDepositId FOREIGN KEY (CheckDepositId)
                        REFERENCES dbo.CheckDeposits(Id) ON DELETE CASCADE,
                    CONSTRAINT CK_CheckLedgerEntries_OneSide CHECK
                        ((Debit > 0 AND Credit = 0) OR (Credit > 0 AND Debit = 0))
                );
                CREATE INDEX IX_CheckLedgerEntries_CheckDepositId ON dbo.CheckLedgerEntries(CheckDepositId);
                CREATE INDEX IX_CheckLedgerEntries_JournalId ON dbo.CheckLedgerEntries(JournalId);
            END;
            IF OBJECT_ID(N'dbo.CheckEvents', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.CheckEvents (
                    Id uniqueidentifier NOT NULL CONSTRAINT PK_CheckEvents PRIMARY KEY,
                    CheckDepositId uniqueidentifier NOT NULL,
                    EventType nvarchar(60) NOT NULL,
                    Description nvarchar(500) NOT NULL,
                    CreatedDate datetimeoffset NOT NULL,
                    CONSTRAINT FK_CheckEvents_CheckDeposits_CheckDepositId FOREIGN KEY (CheckDepositId)
                        REFERENCES dbo.CheckDeposits(Id) ON DELETE CASCADE
                );
                CREATE INDEX IX_CheckEvents_CheckDepositId ON dbo.CheckEvents(CheckDepositId);
            END;
            IF OBJECT_ID(N'dbo.CorrespondentRelationships', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.CorrespondentRelationships (
                    Id uniqueidentifier NOT NULL CONSTRAINT PK_CorrespondentRelationships PRIMARY KEY,
                    FromBankId uniqueidentifier NOT NULL, ToBankId uniqueidentifier NOT NULL,
                    CurrencyCode nvarchar(3) NOT NULL, Rail nvarchar(30) NOT NULL,
                    RelationshipType nvarchar(40) NOT NULL,
                    IsActive bit NOT NULL CONSTRAINT DF_CorrespondentRelationships_IsActive DEFAULT (1),
                    Priority int NOT NULL CONSTRAINT DF_CorrespondentRelationships_Priority DEFAULT (100),
                    CreatedDate datetimeoffset NOT NULL,
                    CONSTRAINT FK_CorrespondentRelationships_FromBank FOREIGN KEY (FromBankId) REFERENCES dbo.Banks(Id),
                    CONSTRAINT FK_CorrespondentRelationships_ToBank FOREIGN KEY (ToBankId) REFERENCES dbo.Banks(Id));
                CREATE UNIQUE INDEX IX_CorrespondentRelationships_FromBankId_ToBankId_CurrencyCode_Rail
                    ON dbo.CorrespondentRelationships(FromBankId, ToBankId, CurrencyCode, Rail);
            END;
            IF OBJECT_ID(N'dbo.PaymentRoutes', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.PaymentRoutes (
                    Id uniqueidentifier NOT NULL CONSTRAINT PK_PaymentRoutes PRIMARY KEY,
                    PaymentId uniqueidentifier NOT NULL, Rail nvarchar(30) NOT NULL,
                    CurrencyCode nvarchar(3) NOT NULL, OriginBankId uniqueidentifier NOT NULL,
                    DestinationBankId uniqueidentifier NOT NULL, RouteStatus nvarchar(30) NOT NULL,
                    CreatedDate datetimeoffset NOT NULL,
                    CONSTRAINT FK_PaymentRoutes_WireTransfers_PaymentId FOREIGN KEY (PaymentId) REFERENCES dbo.WireTransfers(Id) ON DELETE CASCADE,
                    CONSTRAINT FK_PaymentRoutes_OriginBank FOREIGN KEY (OriginBankId) REFERENCES dbo.Banks(Id),
                    CONSTRAINT FK_PaymentRoutes_DestinationBank FOREIGN KEY (DestinationBankId) REFERENCES dbo.Banks(Id));
                CREATE UNIQUE INDEX IX_PaymentRoutes_PaymentId ON dbo.PaymentRoutes(PaymentId);
            END;
            IF OBJECT_ID(N'dbo.PaymentRouteSteps', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.PaymentRouteSteps (
                    Id uniqueidentifier NOT NULL CONSTRAINT PK_PaymentRouteSteps PRIMARY KEY,
                    PaymentRouteId uniqueidentifier NOT NULL, StepNumber int NOT NULL,
                    FromBankId uniqueidentifier NOT NULL, ToBankId uniqueidentifier NOT NULL,
                    StepType nvarchar(40) NOT NULL, Status nvarchar(30) NOT NULL,
                    MessageId nvarchar(100) NULL, Uetr uniqueidentifier NULL,
                    CreatedDate datetimeoffset NOT NULL, CompletedDate datetimeoffset NULL,
                    CONSTRAINT FK_PaymentRouteSteps_Route FOREIGN KEY (PaymentRouteId) REFERENCES dbo.PaymentRoutes(Id) ON DELETE CASCADE,
                    CONSTRAINT FK_PaymentRouteSteps_FromBank FOREIGN KEY (FromBankId) REFERENCES dbo.Banks(Id),
                    CONSTRAINT FK_PaymentRouteSteps_ToBank FOREIGN KEY (ToBankId) REFERENCES dbo.Banks(Id));
                CREATE UNIQUE INDEX IX_PaymentRouteSteps_PaymentRouteId_StepNumber ON dbo.PaymentRouteSteps(PaymentRouteId, StepNumber);
                CREATE INDEX IX_PaymentRouteSteps_MessageId ON dbo.PaymentRouteSteps(MessageId) WHERE MessageId IS NOT NULL;
            END;
            IF OBJECT_ID(N'dbo.WireCases', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.WireCases (
                    Id uniqueidentifier NOT NULL CONSTRAINT PK_WireCases PRIMARY KEY,
                    WireTransferId uniqueidentifier NOT NULL,
                    RequestedByBankId uniqueidentifier NOT NULL,
                    Type nvarchar(max) NOT NULL, Status nvarchar(max) NOT NULL,
                    Reason nvarchar(500) NOT NULL, RequestMessageType nvarchar(20) NOT NULL,
                    ResponseMessageType nvarchar(20) NULL, Resolution nvarchar(500) NULL,
                    CreatedDate datetimeoffset NOT NULL, UpdatedDate datetimeoffset NOT NULL,
                    CONSTRAINT FK_WireCases_WireTransfers_WireTransferId FOREIGN KEY (WireTransferId)
                        REFERENCES dbo.WireTransfers(Id) ON DELETE CASCADE,
                    CONSTRAINT FK_WireCases_Banks_RequestedByBankId FOREIGN KEY (RequestedByBankId)
                        REFERENCES dbo.Banks(Id));
                CREATE INDEX IX_WireCases_WireTransferId_CreatedDate
                    ON dbo.WireCases(WireTransferId, CreatedDate);
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
