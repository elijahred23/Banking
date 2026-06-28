IF OBJECT_ID(N'dbo.CheckEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CheckEvents (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_CheckEvents PRIMARY KEY,
        CheckDepositId uniqueidentifier NOT NULL,
        EventType nvarchar(60) NOT NULL,
        Description nvarchar(500) NOT NULL,
        CreatedDate datetimeoffset NOT NULL,
        CONSTRAINT FK_CheckEvents_CheckDeposits_CheckDepositId FOREIGN KEY (CheckDepositId) REFERENCES dbo.CheckDeposits(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_CheckEvents_CheckDepositId ON dbo.CheckEvents(CheckDepositId);
END;
