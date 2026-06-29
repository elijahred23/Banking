SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.CorrespondentRelationships', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CorrespondentRelationships (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_CorrespondentRelationships PRIMARY KEY,
        FromBankId uniqueidentifier NOT NULL, ToBankId uniqueidentifier NOT NULL,
        CurrencyCode nvarchar(3) NOT NULL, Rail nvarchar(30) NOT NULL,
        RelationshipType nvarchar(40) NOT NULL,
        IsActive bit NOT NULL CONSTRAINT DF_CorrespondentRelationships_IsActive DEFAULT (1),
        Priority int NOT NULL CONSTRAINT DF_CorrespondentRelationships_Priority DEFAULT (100),
        CreatedDate datetimeoffset NOT NULL,
        CONSTRAINT FK_CorrespondentRelationships_FromBank FOREIGN KEY (FromBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_CorrespondentRelationships_ToBank FOREIGN KEY (ToBankId) REFERENCES dbo.Banks(Id));
    CREATE UNIQUE INDEX IX_CorrespondentRelationships_FromBankId_ToBankId_CurrencyCode_Rail
        ON dbo.CorrespondentRelationships(FromBankId, ToBankId, CurrencyCode, Rail);
END;

IF OBJECT_ID(N'dbo.PaymentRoutes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PaymentRoutes (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_PaymentRoutes PRIMARY KEY,
        PaymentId uniqueidentifier NOT NULL, Rail nvarchar(30) NOT NULL,
        CurrencyCode nvarchar(3) NOT NULL, OriginBankId uniqueidentifier NOT NULL,
        DestinationBankId uniqueidentifier NOT NULL, RouteStatus nvarchar(30) NOT NULL,
        CreatedDate datetimeoffset NOT NULL,
        CONSTRAINT FK_PaymentRoutes_WireTransfers_PaymentId FOREIGN KEY (PaymentId) REFERENCES dbo.WireTransfers(Id) ON DELETE CASCADE,
        CONSTRAINT FK_PaymentRoutes_OriginBank FOREIGN KEY (OriginBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_PaymentRoutes_DestinationBank FOREIGN KEY (DestinationBankId) REFERENCES dbo.Banks(Id));
    CREATE UNIQUE INDEX IX_PaymentRoutes_PaymentId ON dbo.PaymentRoutes(PaymentId);
END;

IF OBJECT_ID(N'dbo.PaymentRouteSteps', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PaymentRouteSteps (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_PaymentRouteSteps PRIMARY KEY,
        PaymentRouteId uniqueidentifier NOT NULL, StepNumber int NOT NULL,
        FromBankId uniqueidentifier NOT NULL, ToBankId uniqueidentifier NOT NULL,
        StepType nvarchar(40) NOT NULL, Status nvarchar(30) NOT NULL,
        MessageId nvarchar(100) NULL, Uetr uniqueidentifier NULL,
        CreatedDate datetimeoffset NOT NULL, CompletedDate datetimeoffset NULL,
        CONSTRAINT FK_PaymentRouteSteps_Route FOREIGN KEY (PaymentRouteId) REFERENCES dbo.PaymentRoutes(Id) ON DELETE CASCADE,
        CONSTRAINT FK_PaymentRouteSteps_FromBank FOREIGN KEY (FromBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_PaymentRouteSteps_ToBank FOREIGN KEY (ToBankId) REFERENCES dbo.Banks(Id));
    CREATE UNIQUE INDEX IX_PaymentRouteSteps_PaymentRouteId_StepNumber ON dbo.PaymentRouteSteps(PaymentRouteId, StepNumber);
    CREATE INDEX IX_PaymentRouteSteps_MessageId ON dbo.PaymentRouteSteps(MessageId) WHERE MessageId IS NOT NULL;
END;

COMMIT TRANSACTION;
