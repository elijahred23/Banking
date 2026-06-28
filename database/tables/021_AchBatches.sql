IF OBJECT_ID(N'dbo.AchBatches', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AchBatches (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_AchBatches PRIMARY KEY,
        OriginatingBankId uniqueidentifier NOT NULL,
        AchFileId uniqueidentifier NULL,
        SecCode nvarchar(max) NOT NULL,
        CompanyName nvarchar(16) NOT NULL,
        CompanyId nvarchar(10) NOT NULL,
        EffectiveEntryDate date NOT NULL,
        ServiceClassCode nvarchar(3) NOT NULL,
        Status nvarchar(20) NOT NULL,
        BatchNumber int NOT NULL,
        CONSTRAINT FK_AchBatches_Banks_OriginatingBankId FOREIGN KEY (OriginatingBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_AchBatches_AchFiles_AchFileId FOREIGN KEY (AchFileId) REFERENCES dbo.AchFiles(Id));
    CREATE INDEX IX_AchBatches_EffectiveEntryDate ON dbo.AchBatches(EffectiveEntryDate);
    CREATE INDEX IX_AchBatches_OriginatingBankId ON dbo.AchBatches(OriginatingBankId);
    CREATE INDEX IX_AchBatches_AchFileId ON dbo.AchBatches(AchFileId);
END;
