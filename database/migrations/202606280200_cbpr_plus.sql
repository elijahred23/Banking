SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'dbo.Banks', N'Bic') IS NULL
BEGIN
    ALTER TABLE dbo.Banks ADD
        Bic nvarchar(11) NOT NULL CONSTRAINT DF_Banks_Bic DEFAULT (N'UNKNOWNXX'),
        TownName nvarchar(35) NOT NULL CONSTRAINT DF_Banks_TownName DEFAULT (N'Unknown'),
        CountryCode nvarchar(2) NOT NULL CONSTRAINT DF_Banks_CountryCode DEFAULT (N'US'),
        SwiftEnabled bit NOT NULL CONSTRAINT DF_Banks_SwiftEnabled DEFAULT (1);

    EXEC(N'UPDATE dbo.Banks SET
        Bic = CASE RoutingNumber
            WHEN N''101000019'' THEN N''BAKRUS44XXX''
            WHEN N''103000648'' THEN N''FIOKUS44XXX''
            WHEN N''111901234'' THEN N''CNATUS44XXX''
            WHEN N''111000753'' THEN N''RRBAUS44XXX''
            ELSE N''ZZZZUS'' + RIGHT(RoutingNumber, 5)
        END,
        TownName = CASE WHEN RoutingNumber = N''103000648'' THEN N''Oklahoma City'' ELSE N''Tulsa'' END;');
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.Banks') AND name = N'IX_Banks_Bic')
    EXEC(N'CREATE UNIQUE INDEX IX_Banks_Bic ON dbo.Banks (Bic);');

COMMIT TRANSACTION;
