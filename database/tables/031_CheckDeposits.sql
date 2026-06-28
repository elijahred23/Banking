IF OBJECT_ID(N'dbo.CheckDeposits', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CheckDeposits (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_CheckDeposits PRIMARY KEY,
        DepositoryBankId uniqueidentifier NOT NULL,
        PayingBankId uniqueidentifier NULL,
        DepositingAccountId uniqueidentifier NOT NULL,
        CheckCashLetterId uniqueidentifier NULL,
        DepositorName nvarchar(120) NOT NULL,
        PayingRoutingNumber nvarchar(9) NOT NULL,
        PayingAccountNumber nvarchar(34) NOT NULL,
        CheckNumber nvarchar(15) NOT NULL,
        RawMicrLine nvarchar(80) NOT NULL,
        Amount decimal(19,4) NOT NULL,
        Status nvarchar(max) NOT NULL,
        Scenario nvarchar(max) NOT NULL,
        CorrelationId uniqueidentifier NOT NULL,
        ImageCashLetterPayload nvarchar(max) NULL,
        ReturnCode nvarchar(10) NULL,
        ReturnReason nvarchar(240) NULL,
        CreatedDate datetimeoffset NOT NULL,
        CONSTRAINT FK_CheckDeposits_Banks_DepositoryBankId FOREIGN KEY (DepositoryBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_CheckDeposits_Banks_PayingBankId FOREIGN KEY (PayingBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_CheckDeposits_Accounts_DepositingAccountId FOREIGN KEY (DepositingAccountId) REFERENCES dbo.Accounts(Id),
        CONSTRAINT FK_CheckDeposits_CheckCashLetters_CheckCashLetterId FOREIGN KEY (CheckCashLetterId) REFERENCES dbo.CheckCashLetters(Id)
    );
    CREATE UNIQUE INDEX IX_CheckDeposits_CorrelationId ON dbo.CheckDeposits(CorrelationId);
    CREATE INDEX IX_CheckDeposits_PayingRoutingNumber ON dbo.CheckDeposits(PayingRoutingNumber);
    CREATE INDEX IX_CheckDeposits_DuplicateDetection ON dbo.CheckDeposits(PayingRoutingNumber, PayingAccountNumber, CheckNumber, Amount);
    CREATE INDEX IX_CheckDeposits_DepositoryBankId ON dbo.CheckDeposits(DepositoryBankId);
    CREATE INDEX IX_CheckDeposits_PayingBankId ON dbo.CheckDeposits(PayingBankId);
    CREATE INDEX IX_CheckDeposits_DepositingAccountId ON dbo.CheckDeposits(DepositingAccountId);
    CREATE INDEX IX_CheckDeposits_CheckCashLetterId ON dbo.CheckDeposits(CheckCashLetterId);
END;
