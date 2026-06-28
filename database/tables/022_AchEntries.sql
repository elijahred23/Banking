IF OBJECT_ID(N'dbo.AchEntries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AchEntries (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_AchEntries PRIMARY KEY,
        OriginatingBankId uniqueidentifier NOT NULL,
        ReceivingBankId uniqueidentifier NULL,
        OriginatingAccountId uniqueidentifier NOT NULL,
        AchBatchId uniqueidentifier NULL,
        CompanyName nvarchar(16) NOT NULL, CompanyId nvarchar(10) NOT NULL,
        SecCode nvarchar(max) NOT NULL, ReceiverName nvarchar(22) NOT NULL,
        ReceivingRoutingNumber nvarchar(9) NOT NULL, ReceivingAccountNumber nvarchar(17) NOT NULL,
        TransactionCode nvarchar(max) NOT NULL, Amount decimal(19,4) NOT NULL,
        EntryDescription nvarchar(10) NOT NULL, EffectiveEntryDate date NOT NULL,
        Addenda05 nvarchar(80) NULL, ReturnCode nvarchar(3) NULL, TraceNumber nvarchar(15) NULL,
        Status nvarchar(max) NOT NULL, Purpose nvarchar(max) NOT NULL, Scenario nvarchar(max) NOT NULL,
        CreatedDate datetimeoffset NOT NULL,
        CONSTRAINT FK_AchEntries_Banks_OriginatingBankId FOREIGN KEY (OriginatingBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_AchEntries_Banks_ReceivingBankId FOREIGN KEY (ReceivingBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_AchEntries_Accounts_OriginatingAccountId FOREIGN KEY (OriginatingAccountId) REFERENCES dbo.Accounts(Id),
        CONSTRAINT FK_AchEntries_AchBatches_AchBatchId FOREIGN KEY (AchBatchId) REFERENCES dbo.AchBatches(Id));
    CREATE INDEX IX_AchEntries_TraceNumber ON dbo.AchEntries(TraceNumber);
    CREATE INDEX IX_AchEntries_ReceivingRoutingNumber ON dbo.AchEntries(ReceivingRoutingNumber);
    CREATE INDEX IX_AchEntries_OriginatingBankId ON dbo.AchEntries(OriginatingBankId);
    CREATE INDEX IX_AchEntries_ReceivingBankId ON dbo.AchEntries(ReceivingBankId);
    CREATE INDEX IX_AchEntries_OriginatingAccountId ON dbo.AchEntries(OriginatingAccountId);
    CREATE INDEX IX_AchEntries_AchBatchId ON dbo.AchEntries(AchBatchId);
END;
