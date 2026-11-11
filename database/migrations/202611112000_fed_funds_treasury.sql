-- Simulated unsecured overnight federal funds agreements; no independent cash settlement engine.

IF OBJECT_ID(N'dbo.FedFundsTrades', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FedFundsTrades (
        Id uniqueidentifier NOT NULL,
        LenderBankId uniqueidentifier NOT NULL,
        BorrowerBankId uniqueidentifier NOT NULL,
        ProposalBankId uniqueidentifier NOT NULL,
        Principal decimal(19,4) NOT NULL,
        AnnualRate decimal(12,8) NOT NULL,
        ValueDate date NOT NULL,
        MaturityDate date NOT NULL,
        Status nvarchar(30) NOT NULL,
        CreditReserved bit NOT NULL,
        CreatedBy nvarchar(120) NOT NULL,
        AcceptedBy nvarchar(120) NULL,
        CreatedAt datetimeoffset NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_FedFundsTrades PRIMARY KEY (Id),
        CONSTRAINT FK_FedFundsTrades_Banks_LenderBankId FOREIGN KEY (LenderBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_FedFundsTrades_Banks_BorrowerBankId FOREIGN KEY (BorrowerBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_FedFundsTrades_Banks_ProposalBankId FOREIGN KEY (ProposalBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT CK_FedFundsTrades_Terms CHECK (LenderBankId <> BorrowerBankId AND Principal > 0 AND AnnualRate >= 0 AND AnnualRate <= 100 AND MaturityDate > ValueDate AND (ProposalBankId = LenderBankId OR ProposalBankId = BorrowerBankId))
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FedFundsTrades') AND name = N'IX_FedFundsTrades_LenderBankId_Status')
    CREATE INDEX IX_FedFundsTrades_LenderBankId_Status ON dbo.FedFundsTrades(LenderBankId, Status);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FedFundsTrades') AND name = N'IX_FedFundsTrades_BorrowerBankId_Status')
    CREATE INDEX IX_FedFundsTrades_BorrowerBankId_Status ON dbo.FedFundsTrades(BorrowerBankId, Status);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FedFundsTrades') AND name = N'IX_FedFundsTrades_Status_MaturityDate')
    CREATE INDEX IX_FedFundsTrades_Status_MaturityDate ON dbo.FedFundsTrades(Status, MaturityDate);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FedFundsTrades') AND name = N'IX_FedFundsTrades_ProposalBankId')
    CREATE INDEX IX_FedFundsTrades_ProposalBankId ON dbo.FedFundsTrades(ProposalBankId);

IF OBJECT_ID(N'dbo.FedFundsQuotes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FedFundsQuotes (
        Id uniqueidentifier NOT NULL,
        BankId uniqueidentifier NOT NULL,
        Side nvarchar(10) NOT NULL,
        MinAmount decimal(19,4) NOT NULL,
        MaxAmount decimal(19,4) NOT NULL,
        Rate decimal(12,8) NOT NULL,
        ExpiresAt datetimeoffset NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        CONSTRAINT PK_FedFundsQuotes PRIMARY KEY (Id),
        CONSTRAINT FK_FedFundsQuotes_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT CK_FedFundsQuotes_Terms CHECK (MinAmount > 0 AND MaxAmount >= MinAmount AND Rate >= 0 AND Rate <= 100)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FedFundsQuotes') AND name = N'IX_FedFundsQuotes_BankId_ExpiresAt')
    CREATE INDEX IX_FedFundsQuotes_BankId_ExpiresAt ON dbo.FedFundsQuotes(BankId, ExpiresAt);

IF OBJECT_ID(N'dbo.FedFundsCreditLines', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FedFundsCreditLines (
        Id uniqueidentifier NOT NULL,
        LenderBankId uniqueidentifier NOT NULL,
        BorrowerBankId uniqueidentifier NOT NULL,
        ApprovedLimit decimal(19,4) NOT NULL,
        ReservedAmount decimal(19,4) NOT NULL,
        IsActive bit NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_FedFundsCreditLines PRIMARY KEY (Id),
        CONSTRAINT FK_FedFundsCreditLines_Banks_LenderBankId FOREIGN KEY (LenderBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_FedFundsCreditLines_Banks_BorrowerBankId FOREIGN KEY (BorrowerBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT CK_FedFundsCreditLines_Terms CHECK (LenderBankId <> BorrowerBankId AND ApprovedLimit >= 0 AND ReservedAmount >= 0 AND ReservedAmount <= ApprovedLimit)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FedFundsCreditLines') AND name = N'IX_FedFundsCreditLines_LenderBankId_BorrowerBankId')
    CREATE UNIQUE INDEX IX_FedFundsCreditLines_LenderBankId_BorrowerBankId ON dbo.FedFundsCreditLines(LenderBankId, BorrowerBankId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FedFundsCreditLines') AND name = N'IX_FedFundsCreditLines_BorrowerBankId')
    CREATE INDEX IX_FedFundsCreditLines_BorrowerBankId ON dbo.FedFundsCreditLines(BorrowerBankId);

IF OBJECT_ID(N'dbo.FedFundsApprovals', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FedFundsApprovals (
        Id uniqueidentifier NOT NULL,
        TradeId uniqueidentifier NOT NULL,
        BankId uniqueidentifier NOT NULL,
        OperatorId nvarchar(120) NOT NULL,
        Approved bit NOT NULL,
        DecidedAt datetimeoffset NOT NULL,
        CONSTRAINT PK_FedFundsApprovals PRIMARY KEY (Id),
        CONSTRAINT FK_FedFundsApprovals_FedFundsTrades_TradeId FOREIGN KEY (TradeId) REFERENCES dbo.FedFundsTrades(Id),
        CONSTRAINT FK_FedFundsApprovals_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FedFundsApprovals') AND name = N'IX_FedFundsApprovals_TradeId_BankId')
    CREATE UNIQUE INDEX IX_FedFundsApprovals_TradeId_BankId ON dbo.FedFundsApprovals(TradeId, BankId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FedFundsApprovals') AND name = N'IX_FedFundsApprovals_BankId')
    CREATE INDEX IX_FedFundsApprovals_BankId ON dbo.FedFundsApprovals(BankId);

IF OBJECT_ID(N'dbo.FedFundsAccruals', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FedFundsAccruals (
        Id uniqueidentifier NOT NULL,
        TradeId uniqueidentifier NOT NULL,
        AccrualDate date NOT NULL,
        Amount decimal(19,4) NOT NULL,
        LenderJournalId uniqueidentifier NOT NULL,
        BorrowerJournalId uniqueidentifier NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        CONSTRAINT PK_FedFundsAccruals PRIMARY KEY (Id),
        CONSTRAINT FK_FedFundsAccruals_FedFundsTrades_TradeId FOREIGN KEY (TradeId) REFERENCES dbo.FedFundsTrades(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FedFundsAccruals') AND name = N'IX_FedFundsAccruals_TradeId_AccrualDate')
    CREATE UNIQUE INDEX IX_FedFundsAccruals_TradeId_AccrualDate ON dbo.FedFundsAccruals(TradeId, AccrualDate);

IF OBJECT_ID(N'dbo.FedFundsPaymentObligations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FedFundsPaymentObligations (
        Id uniqueidentifier NOT NULL,
        TradeId uniqueidentifier NOT NULL,
        Type nvarchar(20) NOT NULL,
        Amount decimal(19,4) NOT NULL,
        DueDate date NOT NULL,
        Status nvarchar(20) NOT NULL,
        CorrelationId uniqueidentifier NOT NULL,
        WireTransferId uniqueidentifier NULL,
        Attempts int NOT NULL,
        LenderJournalId uniqueidentifier NULL,
        BorrowerJournalId uniqueidentifier NULL,
        CreatedAt datetimeoffset NOT NULL,
        CONSTRAINT PK_FedFundsPaymentObligations PRIMARY KEY (Id),
        CONSTRAINT FK_FedFundsPaymentObligations_FedFundsTrades_TradeId FOREIGN KEY (TradeId) REFERENCES dbo.FedFundsTrades(Id),
        CONSTRAINT FK_FedFundsPaymentObligations_WireTransfers_WireTransferId FOREIGN KEY (WireTransferId) REFERENCES dbo.WireTransfers(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FedFundsPaymentObligations') AND name = N'IX_FedFundsPaymentObligations_TradeId_Type')
    CREATE UNIQUE INDEX IX_FedFundsPaymentObligations_TradeId_Type ON dbo.FedFundsPaymentObligations(TradeId, Type);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FedFundsPaymentObligations') AND name = N'IX_FedFundsPaymentObligations_CorrelationId')
    CREATE UNIQUE INDEX IX_FedFundsPaymentObligations_CorrelationId ON dbo.FedFundsPaymentObligations(CorrelationId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FedFundsPaymentObligations') AND name = N'IX_FedFundsPaymentObligations_WireTransferId')
    CREATE INDEX IX_FedFundsPaymentObligations_WireTransferId ON dbo.FedFundsPaymentObligations(WireTransferId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FedFundsPaymentObligations') AND name = N'IX_FedFundsPaymentObligations_Status_DueDate')
    CREATE INDEX IX_FedFundsPaymentObligations_Status_DueDate ON dbo.FedFundsPaymentObligations(Status, DueDate);

IF OBJECT_ID(N'dbo.FedFundsEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FedFundsEvents (
        Id uniqueidentifier NOT NULL,
        TradeId uniqueidentifier NOT NULL,
        EventType nvarchar(60) NOT NULL,
        ActorId nvarchar(120) NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        Details nvarchar(1000) NOT NULL,
        CONSTRAINT PK_FedFundsEvents PRIMARY KEY (Id),
        CONSTRAINT FK_FedFundsEvents_FedFundsTrades_TradeId FOREIGN KEY (TradeId) REFERENCES dbo.FedFundsTrades(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.FedFundsEvents') AND name = N'IX_FedFundsEvents_TradeId_CreatedAt')
    CREATE INDEX IX_FedFundsEvents_TradeId_CreatedAt ON dbo.FedFundsEvents(TradeId, CreatedAt);
