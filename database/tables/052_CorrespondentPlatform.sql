-- Correspondent book payments and funding; balances remain in the existing correspondent ledger.
IF OBJECT_ID(N'dbo.CorrespondentServiceAgreements', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CorrespondentServiceAgreements (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_CorrespondentServiceAgreements PRIMARY KEY,
        AccountId uniqueidentifier NOT NULL,
        CreatedBy nvarchar(120) NOT NULL,
        IssuerApproved bit NOT NULL,
        RespondentApproved bit NOT NULL,
        Suspended bit NOT NULL,
        TransferLimit decimal(19,4) NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        CONSTRAINT FK_CorrespondentServiceAgreements_CorrespondentAccounts_AccountId FOREIGN KEY (AccountId) REFERENCES dbo.CorrespondentAccounts(Id)
    );
    CREATE UNIQUE INDEX IX_CorrespondentServiceAgreements_AccountId ON dbo.CorrespondentServiceAgreements(AccountId);
END;
IF OBJECT_ID(N'dbo.CorrespondentBookOrders', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CorrespondentBookOrders (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_CorrespondentBookOrders PRIMARY KEY,
        SourceAccountId uniqueidentifier NOT NULL,
        DestinationAccountId uniqueidentifier NOT NULL,
        Amount decimal(19,4) NOT NULL,
        CreatedBy nvarchar(120) NOT NULL,
        DecidedBy nvarchar(120) NULL,
        Status nvarchar(20) NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        DecidedAt datetimeoffset NULL,
        CONSTRAINT FK_CorrespondentBookOrders_CorrespondentAccounts_SourceAccountId FOREIGN KEY (SourceAccountId) REFERENCES dbo.CorrespondentAccounts(Id),
        CONSTRAINT FK_CorrespondentBookOrders_CorrespondentAccounts_DestinationAccountId FOREIGN KEY (DestinationAccountId) REFERENCES dbo.CorrespondentAccounts(Id)
    );
    CREATE INDEX IX_CorrespondentBookOrders_SourceAccountId_Status ON dbo.CorrespondentBookOrders(SourceAccountId,Status);
    CREATE INDEX IX_CorrespondentBookOrders_DestinationAccountId ON dbo.CorrespondentBookOrders(DestinationAccountId);
END;
IF OBJECT_ID(N'dbo.CorrespondentFundingRules', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CorrespondentFundingRules (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_CorrespondentFundingRules PRIMARY KEY,
        SourceAccountId uniqueidentifier NOT NULL,
        DestinationAccountId uniqueidentifier NOT NULL,
        Strategy nvarchar(20) NOT NULL,
        Threshold decimal(19,4) NOT NULL,
        Target decimal(19,4) NOT NULL,
        Maximum decimal(19,4) NOT NULL,
        NextRunAt datetimeoffset NOT NULL,
        CreatedBy nvarchar(120) NOT NULL,
        IssuerApproved bit NOT NULL,
        SourceApproved bit NOT NULL,
        Enabled bit NOT NULL,
        CONSTRAINT FK_CorrespondentFundingRules_CorrespondentAccounts_SourceAccountId FOREIGN KEY (SourceAccountId) REFERENCES dbo.CorrespondentAccounts(Id),
        CONSTRAINT FK_CorrespondentFundingRules_CorrespondentAccounts_DestinationAccountId FOREIGN KEY (DestinationAccountId) REFERENCES dbo.CorrespondentAccounts(Id)
    );
    CREATE INDEX IX_CorrespondentFundingRules_SourceAccountId ON dbo.CorrespondentFundingRules(SourceAccountId);
    CREATE INDEX IX_CorrespondentFundingRules_DestinationAccountId ON dbo.CorrespondentFundingRules(DestinationAccountId);
    CREATE INDEX IX_CorrespondentFundingRules_Enabled_NextRunAt ON dbo.CorrespondentFundingRules(Enabled,NextRunAt);
END;
IF OBJECT_ID(N'dbo.CorrespondentFundingRuns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CorrespondentFundingRuns (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_CorrespondentFundingRuns PRIMARY KEY,
        RuleId uniqueidentifier NOT NULL,
        ScheduledFor datetimeoffset NOT NULL,
        Amount decimal(19,4) NOT NULL,
        Outcome nvarchar(200) NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        CONSTRAINT FK_CorrespondentFundingRuns_CorrespondentFundingRules_RuleId FOREIGN KEY (RuleId) REFERENCES dbo.CorrespondentFundingRules(Id)
    );
    CREATE UNIQUE INDEX IX_CorrespondentFundingRuns_RuleId_ScheduledFor ON dbo.CorrespondentFundingRuns(RuleId,ScheduledFor);
END;
IF OBJECT_ID(N'dbo.CorrespondentAuthorizationEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CorrespondentAuthorizationEvents (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_CorrespondentAuthorizationEvents PRIMARY KEY,
        AccountId uniqueidentifier NOT NULL,
        SubjectId uniqueidentifier NOT NULL,
        BankId uniqueidentifier NOT NULL,
        Actor nvarchar(120) NOT NULL,
        Action nvarchar(40) NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        CONSTRAINT FK_CorrespondentAuthorizationEvents_CorrespondentAccounts_AccountId FOREIGN KEY (AccountId) REFERENCES dbo.CorrespondentAccounts(Id),
        CONSTRAINT FK_CorrespondentAuthorizationEvents_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id)
    );
    CREATE INDEX IX_CorrespondentAuthorizationEvents_AccountId_CreatedAt ON dbo.CorrespondentAuthorizationEvents(AccountId,CreatedAt);
    CREATE INDEX IX_CorrespondentAuthorizationEvents_BankId ON dbo.CorrespondentAuthorizationEvents(BankId);
END;
