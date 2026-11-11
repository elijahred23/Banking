-- Immutable correspondent statement snapshots and independently posted due-to/due-from ledgers.

IF OBJECT_ID(N'dbo.CorrespondentAccounts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CorrespondentAccounts (
        Id uniqueidentifier NOT NULL,
        IssuerBankId uniqueidentifier NOT NULL,
        RespondentBankId uniqueidentifier NOT NULL,
        AccountNumber nvarchar(34) NOT NULL,
        OpeningBalance decimal(19,4) NOT NULL,
        IssuerBalance decimal(19,4) NOT NULL,
        RespondentBalance decimal(19,4) NOT NULL,
        HeldBalance decimal(19,4) NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_CorrespondentAccounts PRIMARY KEY (Id),
        CONSTRAINT FK_CorrespondentAccounts_Banks_IssuerBankId FOREIGN KEY (IssuerBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_CorrespondentAccounts_Banks_RespondentBankId FOREIGN KEY (RespondentBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT CK_CorrespondentAccounts_Ownership CHECK (IssuerBankId <> RespondentBankId AND OpeningBalance >= 0 AND IssuerBalance >= 0 AND HeldBalance >= 0 AND HeldBalance <= IssuerBalance)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CorrespondentAccounts') AND name = N'IX_CorrespondentAccounts_IssuerBankId_AccountNumber')
    CREATE UNIQUE INDEX IX_CorrespondentAccounts_IssuerBankId_AccountNumber ON dbo.CorrespondentAccounts(IssuerBankId, AccountNumber);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CorrespondentAccounts') AND name = N'IX_CorrespondentAccounts_IssuerBankId_RespondentBankId')
    CREATE UNIQUE INDEX IX_CorrespondentAccounts_IssuerBankId_RespondentBankId ON dbo.CorrespondentAccounts(IssuerBankId, RespondentBankId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CorrespondentAccounts') AND name = N'IX_CorrespondentAccounts_RespondentBankId')
    CREATE INDEX IX_CorrespondentAccounts_RespondentBankId ON dbo.CorrespondentAccounts(RespondentBankId);

IF OBJECT_ID(N'dbo.CorrespondentPaymentLinks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CorrespondentPaymentLinks (
        Id uniqueidentifier NOT NULL,
        AccountId uniqueidentifier NOT NULL,
        WireTransferId uniqueidentifier NOT NULL,
        IsCredit bit NOT NULL,
        Status nvarchar(20) NOT NULL,
        ValueDate date NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        CONSTRAINT PK_CorrespondentPaymentLinks PRIMARY KEY (Id),
        CONSTRAINT FK_CorrespondentPaymentLinks_CorrespondentAccounts_AccountId FOREIGN KEY (AccountId) REFERENCES dbo.CorrespondentAccounts(Id),
        CONSTRAINT FK_CorrespondentPaymentLinks_WireTransfers_WireTransferId FOREIGN KEY (WireTransferId) REFERENCES dbo.WireTransfers(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CorrespondentPaymentLinks') AND name = N'IX_CorrespondentPaymentLinks_WireTransferId')
    CREATE UNIQUE INDEX IX_CorrespondentPaymentLinks_WireTransferId ON dbo.CorrespondentPaymentLinks(WireTransferId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CorrespondentPaymentLinks') AND name = N'IX_CorrespondentPaymentLinks_AccountId')
    CREATE INDEX IX_CorrespondentPaymentLinks_AccountId ON dbo.CorrespondentPaymentLinks(AccountId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CorrespondentPaymentLinks') AND name = N'IX_CorrespondentPaymentLinks_Status_CreatedAt')
    CREATE INDEX IX_CorrespondentPaymentLinks_Status_CreatedAt ON dbo.CorrespondentPaymentLinks(Status, CreatedAt);

IF OBJECT_ID(N'dbo.CorrespondentLedgerEntries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CorrespondentLedgerEntries (
        Id uniqueidentifier NOT NULL,
        AccountId uniqueidentifier NOT NULL,
        BankId uniqueidentifier NOT NULL,
        PostingId uniqueidentifier NOT NULL,
        JournalId uniqueidentifier NOT NULL,
        IsAccountLine bit NOT NULL,
        AccountCode nvarchar(80) NOT NULL,
        Debit decimal(19,4) NOT NULL,
        Credit decimal(19,4) NOT NULL,
        BookingAt datetimeoffset NOT NULL,
        ValueDate date NOT NULL,
        WireTransferId uniqueidentifier NULL,
        CorrelationId uniqueidentifier NOT NULL,
        Reference nvarchar(200) NOT NULL,
        CONSTRAINT PK_CorrespondentLedgerEntries PRIMARY KEY (Id),
        CONSTRAINT FK_CorrespondentLedgerEntries_CorrespondentAccounts_AccountId FOREIGN KEY (AccountId) REFERENCES dbo.CorrespondentAccounts(Id),
        CONSTRAINT FK_CorrespondentLedgerEntries_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_CorrespondentLedgerEntries_WireTransfers_WireTransferId FOREIGN KEY (WireTransferId) REFERENCES dbo.WireTransfers(Id),
        CONSTRAINT CK_CorrespondentLedgerEntries_OneSide CHECK ((Debit > 0 AND Credit = 0) OR (Credit > 0 AND Debit = 0))
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CorrespondentLedgerEntries') AND name = N'IX_CorrespondentLedgerEntries_BankId_PostingId_IsAccountLine')
    CREATE UNIQUE INDEX IX_CorrespondentLedgerEntries_BankId_PostingId_IsAccountLine ON dbo.CorrespondentLedgerEntries(BankId, PostingId, IsAccountLine);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CorrespondentLedgerEntries') AND name = N'IX_CorrespondentLedgerEntries_AccountId_BankId_BookingAt')
    CREATE INDEX IX_CorrespondentLedgerEntries_AccountId_BankId_BookingAt ON dbo.CorrespondentLedgerEntries(AccountId, BankId, BookingAt);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CorrespondentLedgerEntries') AND name = N'IX_CorrespondentLedgerEntries_BankId_CorrelationId')
    CREATE INDEX IX_CorrespondentLedgerEntries_BankId_CorrelationId ON dbo.CorrespondentLedgerEntries(BankId, CorrelationId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CorrespondentLedgerEntries') AND name = N'IX_CorrespondentLedgerEntries_WireTransferId')
    CREATE INDEX IX_CorrespondentLedgerEntries_WireTransferId ON dbo.CorrespondentLedgerEntries(WireTransferId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.CorrespondentLedgerEntries') AND name = N'IX_CorrespondentLedgerEntries_JournalId')
    CREATE INDEX IX_CorrespondentLedgerEntries_JournalId ON dbo.CorrespondentLedgerEntries(JournalId);

IF OBJECT_ID(N'dbo.AccountStatements', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AccountStatements (
        Id uniqueidentifier NOT NULL,
        AccountId uniqueidentifier NOT NULL,
        IssuerBankId uniqueidentifier NOT NULL,
        RecipientBankId uniqueidentifier NOT NULL,
        StatementNumber nvarchar(100) NOT NULL,
        Kind nvarchar(20) NOT NULL,
        PeriodStart datetimeoffset NOT NULL,
        PeriodEnd datetimeoffset NOT NULL,
        Version int NOT NULL,
        SequenceNumber int NOT NULL,
        PreviousStatementId uniqueidentifier NULL,
        NotificationPostingId uniqueidentifier NULL,
        OpeningBalance decimal(19,4) NOT NULL,
        ClosingBalance decimal(19,4) NOT NULL,
        TotalDebits decimal(19,4) NOT NULL,
        TotalCredits decimal(19,4) NOT NULL,
        GenerationDifference decimal(19,4) NOT NULL,
        IssuerName nvarchar(120) NOT NULL,
        RecipientName nvarchar(120) NOT NULL,
        AccountNumber nvarchar(34) NOT NULL,
        XmlPayload nvarchar(max) NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        CONSTRAINT PK_AccountStatements PRIMARY KEY (Id),
        CONSTRAINT FK_AccountStatements_CorrespondentAccounts_AccountId FOREIGN KEY (AccountId) REFERENCES dbo.CorrespondentAccounts(Id),
        CONSTRAINT FK_AccountStatements_Banks_IssuerBankId FOREIGN KEY (IssuerBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_AccountStatements_Banks_RecipientBankId FOREIGN KEY (RecipientBankId) REFERENCES dbo.Banks(Id),
        CONSTRAINT FK_AccountStatements_AccountStatements_PreviousStatementId FOREIGN KEY (PreviousStatementId) REFERENCES dbo.AccountStatements(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AccountStatements') AND name = N'IX_AccountStatements_StatementNumber')
    CREATE UNIQUE INDEX IX_AccountStatements_StatementNumber ON dbo.AccountStatements(StatementNumber);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AccountStatements') AND name = N'IX_AccountStatements_AccountId_Kind_PeriodEnd_Version')
    CREATE UNIQUE INDEX IX_AccountStatements_AccountId_Kind_PeriodEnd_Version ON dbo.AccountStatements(AccountId, Kind, PeriodEnd, Version);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AccountStatements') AND name = N'IX_AccountStatements_AccountId_SequenceNumber')
    CREATE UNIQUE INDEX IX_AccountStatements_AccountId_SequenceNumber ON dbo.AccountStatements(AccountId, SequenceNumber);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AccountStatements') AND name = N'IX_AccountStatements_AccountId_NotificationPostingId')
    CREATE UNIQUE INDEX IX_AccountStatements_AccountId_NotificationPostingId ON dbo.AccountStatements(AccountId, NotificationPostingId) WHERE NotificationPostingId IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AccountStatements') AND name = N'IX_AccountStatements_IssuerBankId_CreatedAt')
    CREATE INDEX IX_AccountStatements_IssuerBankId_CreatedAt ON dbo.AccountStatements(IssuerBankId, CreatedAt);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AccountStatements') AND name = N'IX_AccountStatements_RecipientBankId_CreatedAt')
    CREATE INDEX IX_AccountStatements_RecipientBankId_CreatedAt ON dbo.AccountStatements(RecipientBankId, CreatedAt);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AccountStatements') AND name = N'IX_AccountStatements_PreviousStatementId')
    CREATE INDEX IX_AccountStatements_PreviousStatementId ON dbo.AccountStatements(PreviousStatementId);

IF OBJECT_ID(N'dbo.AccountStatementEntries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.AccountStatementEntries (
        Id uniqueidentifier NOT NULL,
        StatementId uniqueidentifier NOT NULL,
        LedgerEntryId uniqueidentifier NOT NULL,
        PostingId uniqueidentifier NOT NULL,
        CorrelationId uniqueidentifier NOT NULL,
        WireTransferId uniqueidentifier NULL,
        Amount decimal(19,4) NOT NULL,
        IsCredit bit NOT NULL,
        BookingAt datetimeoffset NOT NULL,
        ValueDate date NOT NULL,
        Reference nvarchar(200) NOT NULL,
        CONSTRAINT PK_AccountStatementEntries PRIMARY KEY (Id),
        CONSTRAINT FK_AccountStatementEntries_AccountStatements_StatementId FOREIGN KEY (StatementId) REFERENCES dbo.AccountStatements(Id),
        CONSTRAINT FK_AccountStatementEntries_CorrespondentLedgerEntries_LedgerEntryId FOREIGN KEY (LedgerEntryId) REFERENCES dbo.CorrespondentLedgerEntries(Id),
        CONSTRAINT FK_AccountStatementEntries_WireTransfers_WireTransferId FOREIGN KEY (WireTransferId) REFERENCES dbo.WireTransfers(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AccountStatementEntries') AND name = N'IX_AccountStatementEntries_StatementId_LedgerEntryId')
    CREATE UNIQUE INDEX IX_AccountStatementEntries_StatementId_LedgerEntryId ON dbo.AccountStatementEntries(StatementId, LedgerEntryId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AccountStatementEntries') AND name = N'IX_AccountStatementEntries_LedgerEntryId')
    CREATE INDEX IX_AccountStatementEntries_LedgerEntryId ON dbo.AccountStatementEntries(LedgerEntryId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.AccountStatementEntries') AND name = N'IX_AccountStatementEntries_WireTransferId')
    CREATE INDEX IX_AccountStatementEntries_WireTransferId ON dbo.AccountStatementEntries(WireTransferId);

IF OBJECT_ID(N'dbo.StatementSchedules', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StatementSchedules (
        Id uniqueidentifier NOT NULL,
        AccountId uniqueidentifier NOT NULL,
        TimeZone nvarchar(100) NOT NULL,
        LocalTime time NOT NULL,
        HolidaysJson nvarchar(max) NOT NULL,
        PeriodStart datetimeoffset NOT NULL,
        NextRunAt datetimeoffset NOT NULL,
        Enabled bit NOT NULL,
        LeaseToken uniqueidentifier NULL,
        LeaseUntil datetimeoffset NULL,
        LastError nvarchar(1000) NULL,
        CreatedAt datetimeoffset NOT NULL,
        CONSTRAINT PK_StatementSchedules PRIMARY KEY (Id),
        CONSTRAINT FK_StatementSchedules_CorrespondentAccounts_AccountId FOREIGN KEY (AccountId) REFERENCES dbo.CorrespondentAccounts(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StatementSchedules') AND name = N'IX_StatementSchedules_AccountId')
    CREATE UNIQUE INDEX IX_StatementSchedules_AccountId ON dbo.StatementSchedules(AccountId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StatementSchedules') AND name = N'IX_StatementSchedules_Enabled_NextRunAt')
    CREATE INDEX IX_StatementSchedules_Enabled_NextRunAt ON dbo.StatementSchedules(Enabled, NextRunAt);

IF OBJECT_ID(N'dbo.StatementDeliveries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StatementDeliveries (
        Id uniqueidentifier NOT NULL,
        StatementId uniqueidentifier NOT NULL,
        RecipientBankId uniqueidentifier NOT NULL,
        Status nvarchar(20) NOT NULL,
        AttemptCount int NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        DeliveredAt datetimeoffset NULL,
        CONSTRAINT PK_StatementDeliveries PRIMARY KEY (Id),
        CONSTRAINT FK_StatementDeliveries_AccountStatements_StatementId FOREIGN KEY (StatementId) REFERENCES dbo.AccountStatements(Id),
        CONSTRAINT FK_StatementDeliveries_Banks_RecipientBankId FOREIGN KEY (RecipientBankId) REFERENCES dbo.Banks(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StatementDeliveries') AND name = N'IX_StatementDeliveries_StatementId_RecipientBankId')
    CREATE UNIQUE INDEX IX_StatementDeliveries_StatementId_RecipientBankId ON dbo.StatementDeliveries(StatementId, RecipientBankId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StatementDeliveries') AND name = N'IX_StatementDeliveries_RecipientBankId')
    CREATE INDEX IX_StatementDeliveries_RecipientBankId ON dbo.StatementDeliveries(RecipientBankId);

IF OBJECT_ID(N'dbo.StatementReconciliations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StatementReconciliations (
        Id uniqueidentifier NOT NULL,
        StatementId uniqueidentifier NOT NULL,
        BankId uniqueidentifier NOT NULL,
        ExpectedBalance decimal(19,4) NOT NULL,
        ActualBalance decimal(19,4) NOT NULL,
        Difference decimal(19,4) NOT NULL,
        Status nvarchar(30) NOT NULL,
        FindingsJson nvarchar(max) NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        CONSTRAINT PK_StatementReconciliations PRIMARY KEY (Id),
        CONSTRAINT FK_StatementReconciliations_AccountStatements_StatementId FOREIGN KEY (StatementId) REFERENCES dbo.AccountStatements(Id),
        CONSTRAINT FK_StatementReconciliations_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StatementReconciliations') AND name = N'IX_StatementReconciliations_StatementId_CreatedAt')
    CREATE INDEX IX_StatementReconciliations_StatementId_CreatedAt ON dbo.StatementReconciliations(StatementId, CreatedAt);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StatementReconciliations') AND name = N'IX_StatementReconciliations_BankId')
    CREATE INDEX IX_StatementReconciliations_BankId ON dbo.StatementReconciliations(BankId);
