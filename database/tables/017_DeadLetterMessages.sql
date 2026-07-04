IF OBJECT_ID(N'dbo.DeadLetterMessages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DeadLetterMessages (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_DeadLetterMessages PRIMARY KEY,
        ConsumerInboxMessageId uniqueidentifier NULL,
        BankId uniqueidentifier NULL,
        Queue nvarchar(120) NOT NULL,
        MessageId nvarchar(128) NOT NULL,
        Payload nvarchar(max) NOT NULL,
        Error nvarchar(1000) NOT NULL,
        Attempts int NOT NULL,
        FailedDate datetimeoffset NOT NULL,
        ReplayedDate datetimeoffset NULL,
        ReplayOutboxMessageId uniqueidentifier NULL,
        CONSTRAINT FK_DeadLetterMessages_ConsumerInboxMessages_ConsumerInboxMessageId FOREIGN KEY (ConsumerInboxMessageId) REFERENCES dbo.ConsumerInboxMessages(Id),
        CONSTRAINT FK_DeadLetterMessages_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id));
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.DeadLetterMessages') AND name = N'IX_DeadLetterMessages_BankId_ReplayedDate_FailedDate')
    CREATE INDEX IX_DeadLetterMessages_BankId_ReplayedDate_FailedDate ON dbo.DeadLetterMessages(BankId, ReplayedDate, FailedDate);
