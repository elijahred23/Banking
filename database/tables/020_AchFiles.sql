IF OBJECT_ID(N'dbo.AchFiles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AchFiles (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_AchFiles PRIMARY KEY,
        OriginatingBankId uniqueidentifier NOT NULL,
        ImmediateDestinationRoutingNumber nvarchar(9) NOT NULL,
        ImmediateOriginRoutingNumber nvarchar(9) NOT NULL,
        FileIdModifier nvarchar(1) NOT NULL,
        RawNachaPayload nvarchar(max) NOT NULL,
        Status nvarchar(20) NOT NULL,
        CreatedDate datetimeoffset NOT NULL,
        CONSTRAINT FK_AchFiles_Banks_OriginatingBankId FOREIGN KEY (OriginatingBankId) REFERENCES dbo.Banks(Id));
    CREATE INDEX IX_AchFiles_CreatedDate ON dbo.AchFiles(CreatedDate);
    CREATE INDEX IX_AchFiles_OriginatingBankId ON dbo.AchFiles(OriginatingBankId);
END;
