IF OBJECT_ID(N'dbo.CheckCashLetters', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CheckCashLetters (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_CheckCashLetters PRIMARY KEY,
        DepositoryBankId uniqueidentifier NOT NULL,
        DestinationRoutingNumber nvarchar(9) NOT NULL,
        OriginRoutingNumber nvarchar(9) NOT NULL,
        FileIdModifier nvarchar(1) NOT NULL,
        RawX937Payload nvarchar(max) NOT NULL,
        Status nvarchar(30) NOT NULL,
        CreatedDate datetimeoffset NOT NULL,
        CONSTRAINT FK_CheckCashLetters_Banks_DepositoryBankId FOREIGN KEY (DepositoryBankId) REFERENCES dbo.Banks(Id)
    );
    CREATE INDEX IX_CheckCashLetters_CreatedDate ON dbo.CheckCashLetters(CreatedDate);
    CREATE INDEX IX_CheckCashLetters_DepositoryBankId ON dbo.CheckCashLetters(DepositoryBankId);
END;
