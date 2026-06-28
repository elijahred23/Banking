IF OBJECT_ID(N'dbo.Banks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Banks
    (
        Id uniqueidentifier NOT NULL,
        Name nvarchar(120) NOT NULL,
        RoutingNumber nvarchar(9) NOT NULL,
        FedParticipantId nvarchar(12) NOT NULL,
        MasterAccountBalance decimal(19, 4) NOT NULL,
        CONSTRAINT PK_Banks PRIMARY KEY (Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Banks') AND name = N'IX_Banks_RoutingNumber')
    CREATE UNIQUE INDEX IX_Banks_RoutingNumber ON dbo.Banks (RoutingNumber);
