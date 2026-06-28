IF OBJECT_ID(N'dbo.FedSettlements', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FedSettlements
    (
        Id uniqueidentifier NOT NULL,
        CorrelationId uniqueidentifier NOT NULL,
        Imad nvarchar(35) NOT NULL,
        Omad nvarchar(35) NOT NULL,
        StatusCode nvarchar(10) NOT NULL,
        CreatedDate datetimeoffset NOT NULL,
        CONSTRAINT PK_FedSettlements PRIMARY KEY (Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FedSettlements') AND name = N'IX_FedSettlements_CorrelationId')
    CREATE UNIQUE INDEX IX_FedSettlements_CorrelationId ON dbo.FedSettlements (CorrelationId);
