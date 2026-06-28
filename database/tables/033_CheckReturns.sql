IF OBJECT_ID(N'dbo.CheckReturns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CheckReturns (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_CheckReturns PRIMARY KEY,
        CheckDepositId uniqueidentifier NOT NULL,
        ReturnCode nvarchar(10) NOT NULL,
        Reason nvarchar(240) NOT NULL,
        ReceivedDate datetimeoffset NOT NULL,
        CONSTRAINT FK_CheckReturns_CheckDeposits_CheckDepositId FOREIGN KEY (CheckDepositId) REFERENCES dbo.CheckDeposits(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_CheckReturns_CheckDepositId ON dbo.CheckReturns(CheckDepositId);
END;
