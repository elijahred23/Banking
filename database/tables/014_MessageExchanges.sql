IF OBJECT_ID(N'dbo.MessageExchanges', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MessageExchanges
    (
        Id uniqueidentifier NOT NULL,
        BankId uniqueidentifier NOT NULL,
        CounterpartyBankId uniqueidentifier NULL,
        Type nvarchar(max) NOT NULL,
        Status nvarchar(max) NOT NULL,
        Rail nvarchar(max) NOT NULL,
        Subject nvarchar(120) NOT NULL,
        Details nvarchar(500) NOT NULL,
        AccountNumber nvarchar(34) NULL,
        Amount decimal(19,4) NULL,
        CreatedDate datetimeoffset NOT NULL,
        UpdatedDate datetimeoffset NOT NULL,
        CONSTRAINT PK_MessageExchanges PRIMARY KEY (Id),
        CONSTRAINT FK_MessageExchanges_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks (Id),
        CONSTRAINT FK_MessageExchanges_Banks_CounterpartyBankId FOREIGN KEY (CounterpartyBankId)
            REFERENCES dbo.Banks (Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.MessageExchanges') AND name = N'IX_MessageExchanges_BankId_CreatedDate')
    CREATE INDEX IX_MessageExchanges_BankId_CreatedDate ON dbo.MessageExchanges (BankId, CreatedDate);

IF COL_LENGTH(N'dbo.IsoMessages', N'MessageExchangeId') IS NULL
    ALTER TABLE dbo.IsoMessages ADD MessageExchangeId uniqueidentifier NULL;

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_IsoMessages_MessageExchanges_MessageExchangeId')
    ALTER TABLE dbo.IsoMessages ADD CONSTRAINT FK_IsoMessages_MessageExchanges_MessageExchangeId
        FOREIGN KEY (MessageExchangeId) REFERENCES dbo.MessageExchanges (Id) ON DELETE CASCADE;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.IsoMessages') AND name = N'IX_IsoMessages_MessageExchangeId')
    CREATE INDEX IX_IsoMessages_MessageExchangeId ON dbo.IsoMessages (MessageExchangeId);
