IF OBJECT_ID(N'dbo.WireCases', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.WireCases
    (
        Id uniqueidentifier NOT NULL,
        WireTransferId uniqueidentifier NOT NULL,
        RequestedByBankId uniqueidentifier NOT NULL,
        Type nvarchar(max) NOT NULL,
        Status nvarchar(max) NOT NULL,
        Reason nvarchar(500) NOT NULL,
        RequestMessageType nvarchar(20) NOT NULL,
        ResponseMessageType nvarchar(20) NULL,
        Resolution nvarchar(500) NULL,
        CreatedDate datetimeoffset NOT NULL,
        UpdatedDate datetimeoffset NOT NULL,
        CONSTRAINT PK_WireCases PRIMARY KEY (Id),
        CONSTRAINT FK_WireCases_WireTransfers_WireTransferId FOREIGN KEY (WireTransferId)
            REFERENCES dbo.WireTransfers (Id) ON DELETE CASCADE,
        CONSTRAINT FK_WireCases_Banks_RequestedByBankId FOREIGN KEY (RequestedByBankId)
            REFERENCES dbo.Banks (Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.WireCases') AND name = N'IX_WireCases_WireTransferId_CreatedDate')
    CREATE INDEX IX_WireCases_WireTransferId_CreatedDate ON dbo.WireCases (WireTransferId, CreatedDate);
