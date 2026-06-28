IF OBJECT_ID(N'dbo.AchLedgerEntries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AchLedgerEntries (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_AchLedgerEntries PRIMARY KEY,
        JournalId uniqueidentifier NOT NULL, AchEntryId uniqueidentifier NOT NULL,
        AccountCode nvarchar(80) NOT NULL, AccountName nvarchar(120) NOT NULL,
        Debit decimal(19,4) NOT NULL, Credit decimal(19,4) NOT NULL,
        Description nvarchar(240) NOT NULL, CreatedDate datetimeoffset NOT NULL,
        CONSTRAINT FK_AchLedgerEntries_AchEntries_AchEntryId FOREIGN KEY (AchEntryId) REFERENCES dbo.AchEntries(Id) ON DELETE CASCADE,
        CONSTRAINT CK_AchLedgerEntries_OneSide CHECK ((Debit > 0 AND Credit = 0) OR (Credit > 0 AND Debit = 0)));
    CREATE INDEX IX_AchLedgerEntries_AchEntryId ON dbo.AchLedgerEntries(AchEntryId);
    CREATE INDEX IX_AchLedgerEntries_JournalId ON dbo.AchLedgerEntries(JournalId);
END;
