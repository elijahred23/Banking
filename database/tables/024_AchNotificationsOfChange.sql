IF OBJECT_ID(N'dbo.AchNotificationsOfChange', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AchNotificationsOfChange (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_AchNotificationsOfChange PRIMARY KEY,
        AchEntryId uniqueidentifier NOT NULL, ChangeCode nvarchar(3) NOT NULL,
        CorrectedData nvarchar(35) NOT NULL, Description nvarchar(240) NOT NULL,
        ReceivedDate datetimeoffset NOT NULL,
        CONSTRAINT FK_AchNotificationsOfChange_AchEntries_AchEntryId FOREIGN KEY (AchEntryId) REFERENCES dbo.AchEntries(Id) ON DELETE CASCADE);
    CREATE INDEX IX_AchNotificationsOfChange_AchEntryId ON dbo.AchNotificationsOfChange(AchEntryId);
END;
