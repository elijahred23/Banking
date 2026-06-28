IF OBJECT_ID(N'dbo.Banks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Banks
    (
        Id uniqueidentifier NOT NULL,
        Name nvarchar(120) NOT NULL,
        RoutingNumber nvarchar(9) NOT NULL,
        FedParticipantId nvarchar(12) NOT NULL,
        Bic nvarchar(11) NOT NULL,
        TownName nvarchar(35) NOT NULL,
        CountryCode nvarchar(2) NOT NULL,
        MasterAccountBalance decimal(19, 4) NOT NULL,
        FedNowEnabled bit NOT NULL CONSTRAINT DF_Banks_FedNowEnabled DEFAULT (1),
        FedNowSendEnabled bit NOT NULL CONSTRAINT DF_Banks_FedNowSendEnabled DEFAULT (1),
        FedNowReceiveEnabled bit NOT NULL CONSTRAINT DF_Banks_FedNowReceiveEnabled DEFAULT (1),
        FedNowRequestForPaymentEnabled bit NOT NULL CONSTRAINT DF_Banks_FedNowRfpEnabled DEFAULT (1),
        FedNowOnline bit NOT NULL CONSTRAINT DF_Banks_FedNowOnline DEFAULT (1),
        SwiftEnabled bit NOT NULL CONSTRAINT DF_Banks_SwiftEnabled DEFAULT (1),
        CONSTRAINT PK_Banks PRIMARY KEY (Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Banks') AND name = N'IX_Banks_RoutingNumber')
    CREATE UNIQUE INDEX IX_Banks_RoutingNumber ON dbo.Banks (RoutingNumber);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Banks') AND name = N'IX_Banks_Bic')
    CREATE UNIQUE INDEX IX_Banks_Bic ON dbo.Banks (Bic);
