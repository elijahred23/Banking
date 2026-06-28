SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'dbo.Accounts', N'HeldBalance') IS NULL
    ALTER TABLE dbo.Accounts ADD HeldBalance decimal(19, 4) NOT NULL
        CONSTRAINT DF_Accounts_HeldBalance DEFAULT (0);

IF COL_LENGTH(N'dbo.WireTransfers', N'BeneficiaryAccountNumber') IS NULL
    ALTER TABLE dbo.WireTransfers ADD BeneficiaryAccountNumber nvarchar(24) NOT NULL
        CONSTRAINT DF_WireTransfers_BeneficiaryAccountNumber DEFAULT (N'UNKNOWN');

IF COL_LENGTH(N'dbo.WireTransfers', N'Scenario') IS NULL
    ALTER TABLE dbo.WireTransfers ADD Scenario nvarchar(max) NOT NULL
        CONSTRAINT DF_WireTransfers_Scenario DEFAULT (N'Standard');

IF OBJECT_ID(N'dbo.LedgerEntries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LedgerEntries
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_LedgerEntries PRIMARY KEY,
        JournalId uniqueidentifier NOT NULL,
        WireTransferId uniqueidentifier NOT NULL,
        AccountCode nvarchar(80) NOT NULL,
        AccountName nvarchar(120) NOT NULL,
        Debit decimal(19, 4) NOT NULL,
        Credit decimal(19, 4) NOT NULL,
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

COMMIT TRANSACTION;
