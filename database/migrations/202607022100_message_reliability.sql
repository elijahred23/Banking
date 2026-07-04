SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'dbo.OutboxMessages', N'BankId') IS NULL
    ALTER TABLE dbo.OutboxMessages ADD BankId uniqueidentifier NULL;
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_OutboxMessages_Banks_BankId')
    ALTER TABLE dbo.OutboxMessages ADD CONSTRAINT FK_OutboxMessages_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.OutboxMessages') AND name = N'IX_OutboxMessages_BankId_PublishedDate')
    CREATE INDEX IX_OutboxMessages_BankId_PublishedDate ON dbo.OutboxMessages(BankId, PublishedDate);

IF OBJECT_ID(N'dbo.ConsumerInboxMessages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ConsumerInboxMessages (Id uniqueidentifier NOT NULL CONSTRAINT PK_ConsumerInboxMessages PRIMARY KEY,
        Queue nvarchar(120) NOT NULL, MessageId nvarchar(128) NOT NULL, PayloadHash nvarchar(64) NOT NULL,
        BankId uniqueidentifier NULL, Attempts int NOT NULL, LastError nvarchar(1000) NULL,
        FirstReceivedDate datetimeoffset NOT NULL, LastAttemptDate datetimeoffset NULL, CompletedDate datetimeoffset NULL,
        CONSTRAINT FK_ConsumerInboxMessages_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id));
    CREATE UNIQUE INDEX IX_ConsumerInboxMessages_Queue_MessageId ON dbo.ConsumerInboxMessages(Queue, MessageId);
    CREATE INDEX IX_ConsumerInboxMessages_BankId_CompletedDate ON dbo.ConsumerInboxMessages(BankId, CompletedDate);
END;

IF OBJECT_ID(N'dbo.DeadLetterMessages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DeadLetterMessages (Id uniqueidentifier NOT NULL CONSTRAINT PK_DeadLetterMessages PRIMARY KEY,
        ConsumerInboxMessageId uniqueidentifier NULL, BankId uniqueidentifier NULL, Queue nvarchar(120) NOT NULL,
        MessageId nvarchar(128) NOT NULL, Payload nvarchar(max) NOT NULL, Error nvarchar(1000) NOT NULL,
        Attempts int NOT NULL, FailedDate datetimeoffset NOT NULL, ReplayedDate datetimeoffset NULL,
        ReplayOutboxMessageId uniqueidentifier NULL,
        CONSTRAINT FK_DeadLetterMessages_ConsumerInboxMessages_ConsumerInboxMessageId FOREIGN KEY (ConsumerInboxMessageId) REFERENCES dbo.ConsumerInboxMessages(Id),
        CONSTRAINT FK_DeadLetterMessages_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id));
    CREATE INDEX IX_DeadLetterMessages_BankId_ReplayedDate_FailedDate ON dbo.DeadLetterMessages(BankId, ReplayedDate, FailedDate);
END;

COMMIT TRANSACTION;
