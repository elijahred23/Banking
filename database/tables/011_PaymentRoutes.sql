IF OBJECT_ID(N'dbo.PaymentRoutes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PaymentRoutes
    (
        Id uniqueidentifier NOT NULL,
        PaymentId uniqueidentifier NOT NULL,
        Rail nvarchar(30) NOT NULL,
        CurrencyCode nvarchar(3) NOT NULL,
        OriginBankId uniqueidentifier NOT NULL,
        DestinationBankId uniqueidentifier NOT NULL,
        RouteStatus nvarchar(30) NOT NULL,
        CreatedDate datetimeoffset NOT NULL,
        CONSTRAINT PK_PaymentRoutes PRIMARY KEY (Id),
        CONSTRAINT FK_PaymentRoutes_WireTransfers_PaymentId FOREIGN KEY (PaymentId)
            REFERENCES dbo.WireTransfers(Id) ON DELETE CASCADE,
        CONSTRAINT FK_PaymentRoutes_OriginBank FOREIGN KEY (OriginBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_PaymentRoutes_DestinationBank FOREIGN KEY (DestinationBankId) REFERENCES dbo.Banks(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PaymentRoutes')
    AND name = N'IX_PaymentRoutes_PaymentId')
    CREATE UNIQUE INDEX IX_PaymentRoutes_PaymentId ON dbo.PaymentRoutes (PaymentId);
