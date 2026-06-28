IF OBJECT_ID(N'dbo.WireTransfers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.WireTransfers
    (
        Id uniqueidentifier NOT NULL,
        CorrelationId uniqueidentifier NOT NULL,
        BankId uniqueidentifier NOT NULL,
        SenderBankId uniqueidentifier NOT NULL,
        ReceiverBankId uniqueidentifier NOT NULL,
        FromAccountId uniqueidentifier NULL,
        ToAccountId uniqueidentifier NULL,
        Direction nvarchar(max) NOT NULL,
        Amount decimal(19, 4) NOT NULL,
        Status nvarchar(max) NOT NULL,
        SenderName nvarchar(120) NOT NULL,
        ReceiverName nvarchar(120) NOT NULL,
        Imad nvarchar(35) NULL,
        Omad nvarchar(35) NULL,
        CreatedDate datetimeoffset NOT NULL,
        CONSTRAINT PK_WireTransfers PRIMARY KEY (Id),
        CONSTRAINT FK_WireTransfers_Banks_BankId FOREIGN KEY (BankId)
            REFERENCES dbo.Banks (Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.WireTransfers') AND name = N'IX_WireTransfers_BankId')
    CREATE INDEX IX_WireTransfers_BankId ON dbo.WireTransfers (BankId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.WireTransfers') AND name = N'IX_WireTransfers_CorrelationId')
    CREATE INDEX IX_WireTransfers_CorrelationId ON dbo.WireTransfers (CorrelationId);
