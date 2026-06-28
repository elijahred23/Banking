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
        CONSTRAINT FK_CheckLedgerEntries_CheckDeposits_CheckDepositId FOREIGN KEY (CheckDepositId) REFERENCES dbo.CheckDeposits(Id) ON DELETE CASCADE,
        CONSTRAINT CK_CheckLedgerEntries_OneSide CHECK ((Debit > 0 AND Credit = 0) OR (Credit > 0 AND Debit = 0))
    );
    CREATE INDEX IX_CheckLedgerEntries_CheckDepositId ON dbo.CheckLedgerEntries(CheckDepositId);
    CREATE INDEX IX_CheckLedgerEntries_JournalId ON dbo.CheckLedgerEntries(JournalId);
END;
