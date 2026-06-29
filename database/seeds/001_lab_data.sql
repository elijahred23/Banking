SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @Banks TABLE
    (
        Id uniqueidentifier NOT NULL,
        Name nvarchar(120) NOT NULL,
        RoutingNumber nvarchar(9) NOT NULL,
        FedParticipantId nvarchar(12) NOT NULL,
        Bic nvarchar(11) NOT NULL,
        TownName nvarchar(35) NOT NULL,
        CountryCode nvarchar(2) NOT NULL,
        MasterAccountBalance decimal(19, 4) NOT NULL
    );

    INSERT INTO @Banks (Id, Name, RoutingNumber, FedParticipantId, Bic, TownName, CountryCode, MasterAccountBalance)
    VALUES
        ('10000000-0000-0000-0000-000000000001', N'Bankers Bank',             N'101000019', N'BANKERS',   N'BAKRUS44XXX', N'Tulsa',         N'US', 50000000.0000),
        ('10000000-0000-0000-0000-000000000002', N'First Oklahoma Bank',      N'103000648', N'FIRSTOK',   N'FIOKUS44XXX', N'Oklahoma City', N'US', 40000000.0000),
        ('10000000-0000-0000-0000-000000000003', N'Community National Bank',  N'111901234', N'COMMUNITY', N'CNATUS44XXX', N'Tulsa',         N'US', 25000000.0000),
        ('10000000-0000-0000-0000-000000000004', N'Red River Bank',           N'111000753', N'REDRIVER',  N'RRBAUS44XXX', N'Tulsa',         N'US', 30000000.0000),
        ('10000000-0000-0000-0000-000000000005', N'Euro Demo Bank',           N'000000001', N'EURODEMO',  N'EUDMDEFFXXX', N'Frankfurt',     N'DE',        0.0000),
        ('10000000-0000-0000-0000-000000000006', N'Britannia Demo Bank',      N'000000002', N'BRITDEMO',  N'BRDMGB2LXXX', N'London',        N'GB',        0.0000),
        ('10000000-0000-0000-0000-000000000007', N'Big New York Correspondent Bank', N'000000003', N'BIGNEWYORK', N'BNYCUS33XXX', N'New York', N'US', 100000000.0000);

    INSERT INTO dbo.Banks (Id, Name, RoutingNumber, FedParticipantId, Bic, TownName, CountryCode, MasterAccountBalance)
    SELECT seed.Id, seed.Name, seed.RoutingNumber, seed.FedParticipantId, seed.Bic, seed.TownName,
           seed.CountryCode, seed.MasterAccountBalance
    FROM @Banks AS seed
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM dbo.Banks AS existing
        WHERE existing.RoutingNumber = seed.RoutingNumber
    );

    DECLARE @Customers TABLE
    (
        Id uniqueidentifier NOT NULL,
        BankRoutingNumber nvarchar(9) NOT NULL,
        Name nvarchar(120) NOT NULL
    );

    INSERT INTO @Customers (Id, BankRoutingNumber, Name)
    VALUES
        ('20000000-0000-0000-0000-000000000001', N'101000019', N'John Smith'),
        ('20000000-0000-0000-0000-000000000002', N'103000648', N'Mary Jones'),
        ('20000000-0000-0000-0000-000000000003', N'111901234', N'Alice Carter'),
        ('20000000-0000-0000-0000-000000000004', N'111000753', N'Robert Lee'),
        ('20000000-0000-0000-0000-000000000005', N'000000001', N'Anna Müller'),
        ('20000000-0000-0000-0000-000000000006', N'000000002', N'James Wilson');

    INSERT INTO dbo.Customers (Id, BankId, Name)
    SELECT seed.Id, bank.Id, seed.Name
    FROM @Customers AS seed
    INNER JOIN dbo.Banks AS bank
        ON bank.RoutingNumber = seed.BankRoutingNumber
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM dbo.Customers AS existing
        WHERE existing.BankId = bank.Id
          AND existing.Name = seed.Name
    );

    DECLARE @Accounts TABLE
    (
        Id uniqueidentifier NOT NULL,
        BankRoutingNumber nvarchar(9) NOT NULL,
        CustomerName nvarchar(120) NOT NULL,
        AccountNumber nvarchar(34) NOT NULL,
        Balance decimal(19, 4) NOT NULL
    );

    INSERT INTO @Accounts (Id, BankRoutingNumber, CustomerName, AccountNumber, Balance)
    VALUES
        ('30000000-0000-0000-0000-000000000001', N'101000019', N'John Smith',   N'123456', 125000.0000),
        ('30000000-0000-0000-0000-000000000002', N'103000648', N'Mary Jones',   N'654321',  25000.0000),
        ('30000000-0000-0000-0000-000000000003', N'111901234', N'Alice Carter', N'445566',  80000.0000),
        ('30000000-0000-0000-0000-000000000004', N'111000753', N'Robert Lee',   N'778899',  55000.0000),
        ('30000000-0000-0000-0000-000000000005', N'000000001', N'Anna Müller',  N'DE89370400440532013000', 75000.0000),
        ('30000000-0000-0000-0000-000000000006', N'000000002', N'James Wilson', N'GB82WEST12345698765432', 60000.0000);

    INSERT INTO dbo.Accounts (Id, CustomerId, AccountNumber, Balance)
    SELECT seed.Id, customer.Id, seed.AccountNumber, seed.Balance
    FROM @Accounts AS seed
    INNER JOIN dbo.Banks AS bank
        ON bank.RoutingNumber = seed.BankRoutingNumber
    INNER JOIN dbo.Customers AS customer
        ON customer.BankId = bank.Id
       AND customer.Name = seed.CustomerName
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM dbo.Accounts AS existing
        WHERE existing.AccountNumber = seed.AccountNumber
    );

    DECLARE @BankersBankId uniqueidentifier = (SELECT Id FROM dbo.Banks WHERE Bic = N'BAKRUS44XXX');
    DECLARE @FirstOklahomaBankId uniqueidentifier = (SELECT Id FROM dbo.Banks WHERE Bic = N'FIOKUS44XXX');
    DECLARE @NewYorkBankId uniqueidentifier = (SELECT Id FROM dbo.Banks WHERE Bic = N'BNYCUS33XXX');
    DECLARE @EuroBankId uniqueidentifier = (SELECT Id FROM dbo.Banks WHERE Bic = N'EUDMDEFFXXX');
    DECLARE @BritanniaBankId uniqueidentifier = (SELECT Id FROM dbo.Banks WHERE Bic = N'BRDMGB2LXXX');

    INSERT INTO dbo.CorrespondentRelationships
        (Id, FromBankId, ToBankId, CurrencyCode, Rail, RelationshipType, IsActive, Priority, CreatedDate)
    SELECT NEWID(), edge.FromBankId, edge.ToBankId, N'USD', N'SWIFT', edge.RelationshipType,
        1, edge.Priority, SYSDATETIMEOFFSET()
    FROM (VALUES
        (@BankersBankId, @FirstOklahomaBankId, N'Direct', 1),
        (@BankersBankId, @NewYorkBankId, N'Intermediary', 1),
        (@NewYorkBankId, @EuroBankId, N'Direct', 1),
        (@NewYorkBankId, @BritanniaBankId, N'Direct', 2)
    ) AS edge(FromBankId, ToBankId, RelationshipType, Priority)
    WHERE edge.FromBankId IS NOT NULL AND edge.ToBankId IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM dbo.CorrespondentRelationships AS existing
          WHERE existing.FromBankId = edge.FromBankId AND existing.ToBankId = edge.ToBankId
            AND existing.CurrencyCode = N'USD' AND existing.Rail = N'SWIFT');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
