IF OBJECT_ID(N'dbo.ConsumerInboxMessages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ConsumerInboxMessages (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_ConsumerInboxMessages PRIMARY KEY,
        Queue nvarchar(120) NOT NULL,
        MessageId nvarchar(128) NOT NULL,
        PayloadHash nvarchar(64) NOT NULL,
        BankId uniqueidentifier NULL,
        Attempts int NOT NULL,
        LastError nvarchar(1000) NULL,
        FirstReceivedDate datetimeoffset NOT NULL,
        LastAttemptDate datetimeoffset NULL,
        CompletedDate datetimeoffset NULL,
        CONSTRAINT FK_ConsumerInboxMessages_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id));
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ConsumerInboxMessages') AND name = N'IX_ConsumerInboxMessages_Queue_MessageId')
    CREATE UNIQUE INDEX IX_ConsumerInboxMessages_Queue_MessageId ON dbo.ConsumerInboxMessages(Queue, MessageId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.ConsumerInboxMessages') AND name = N'IX_ConsumerInboxMessages_BankId_CompletedDate')
    CREATE INDEX IX_ConsumerInboxMessages_BankId_CompletedDate ON dbo.ConsumerInboxMessages(BankId, CompletedDate);
