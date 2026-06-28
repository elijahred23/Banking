SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF EXISTS (SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.Accounts') AND name = N'AccountNumber' AND max_length < 68)
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

COMMIT TRANSACTION;
