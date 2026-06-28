IF OBJECT_ID(N'dbo.AchReturns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AchReturns (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_AchReturns PRIMARY KEY,
        AchEntryId uniqueidentifier NOT NULL, ReturnCode nvarchar(3) NOT NULL,
        Reason nvarchar(240) NOT NULL, ReceivedDate datetimeoffset NOT NULL,
        CONSTRAINT FK_AchReturns_AchEntries_AchEntryId FOREIGN KEY (AchEntryId) REFERENCES dbo.AchEntries(Id) ON DELETE CASCADE);
    CREATE INDEX IX_AchReturns_AchEntryId ON dbo.AchReturns(AchEntryId);
END;
