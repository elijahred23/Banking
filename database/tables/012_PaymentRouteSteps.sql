IF OBJECT_ID(N'dbo.PaymentRouteSteps', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PaymentRouteSteps
    (
        Id uniqueidentifier NOT NULL,
        PaymentRouteId uniqueidentifier NOT NULL,
        StepNumber int NOT NULL,
        FromBankId uniqueidentifier NOT NULL,
        ToBankId uniqueidentifier NOT NULL,
        StepType nvarchar(40) NOT NULL,
        Status nvarchar(30) NOT NULL,
        MessageId nvarchar(100) NULL,
        Uetr uniqueidentifier NULL,
        CreatedDate datetimeoffset NOT NULL,
        CompletedDate datetimeoffset NULL,
        CONSTRAINT PK_PaymentRouteSteps PRIMARY KEY (Id),
        CONSTRAINT FK_PaymentRouteSteps_Route FOREIGN KEY (PaymentRouteId)
            REFERENCES dbo.PaymentRoutes(Id) ON DELETE CASCADE,
        CONSTRAINT FK_PaymentRouteSteps_FromBank FOREIGN KEY (FromBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_PaymentRouteSteps_ToBank FOREIGN KEY (ToBankId) REFERENCES dbo.Banks(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PaymentRouteSteps')
    AND name = N'IX_PaymentRouteSteps_PaymentRouteId_StepNumber')
    CREATE UNIQUE INDEX IX_PaymentRouteSteps_PaymentRouteId_StepNumber
        ON dbo.PaymentRouteSteps (PaymentRouteId, StepNumber);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.PaymentRouteSteps')
    AND name = N'IX_PaymentRouteSteps_MessageId')
    CREATE INDEX IX_PaymentRouteSteps_MessageId ON dbo.PaymentRouteSteps (MessageId)
        WHERE MessageId IS NOT NULL;
