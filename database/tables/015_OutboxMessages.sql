IF OBJECT_ID(N'dbo.OutboxMessages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OutboxMessages (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_OutboxMessages PRIMARY KEY,
        Queue nvarchar(120) NOT NULL,
        BankId uniqueidentifier NULL,
        Payload nvarchar(max) NOT NULL,
        Attempts int NOT NULL,
        LastError nvarchar(1000) NULL,
        CreatedDate datetimeoffset NOT NULL,
        NextAttemptDate datetimeoffset NULL,
        PublishedDate datetimeoffset NULL,
        CONSTRAINT FK_OutboxMessages_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id));
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.OutboxMessages') AND name = N'IX_OutboxMessages_PublishedDate_NextAttemptDate')
    CREATE INDEX IX_OutboxMessages_PublishedDate_NextAttemptDate ON dbo.OutboxMessages(PublishedDate, NextAttemptDate);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.OutboxMessages') AND name = N'IX_OutboxMessages_BankId_PublishedDate')
    CREATE INDEX IX_OutboxMessages_BankId_PublishedDate ON dbo.OutboxMessages(BankId, PublishedDate);
