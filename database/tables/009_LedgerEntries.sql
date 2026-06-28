IF OBJECT_ID(N'dbo.LedgerEntries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LedgerEntries
    (
        Id uniqueidentifier NOT NULL,
        JournalId uniqueidentifier NOT NULL,
        WireTransferId uniqueidentifier NOT NULL,
        AccountCode nvarchar(80) NOT NULL,
        AccountName nvarchar(120) NOT NULL,
        Debit decimal(19, 4) NOT NULL,
        Credit decimal(19, 4) NOT NULL,
        Description nvarchar(240) NOT NULL,
        CreatedDate datetimeoffset NOT NULL,
        CONSTRAINT PK_LedgerEntries PRIMARY KEY (Id),
        CONSTRAINT FK_LedgerEntries_WireTransfers_WireTransferId FOREIGN KEY (WireTransferId)
            REFERENCES dbo.WireTransfers (Id) ON DELETE CASCADE,
        CONSTRAINT CK_LedgerEntries_OneSide CHECK
            ((Debit > 0 AND Credit = 0) OR (Credit > 0 AND Debit = 0))
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.LedgerEntries') AND name = N'IX_LedgerEntries_WireTransferId')
    CREATE INDEX IX_LedgerEntries_WireTransferId ON dbo.LedgerEntries (WireTransferId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.LedgerEntries') AND name = N'IX_LedgerEntries_JournalId')
    CREATE INDEX IX_LedgerEntries_JournalId ON dbo.LedgerEntries (JournalId);
