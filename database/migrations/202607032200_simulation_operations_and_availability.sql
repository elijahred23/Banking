SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF COL_LENGTH(N'dbo.Accounts', N'PendingCreditBalance') IS NULL
    ALTER TABLE dbo.Accounts ADD
        PendingCreditBalance decimal(19,4) NOT NULL CONSTRAINT DF_Accounts_PendingCreditBalance DEFAULT (0),
        ProvisionalCreditBalance decimal(19,4) NOT NULL CONSTRAINT DF_Accounts_ProvisionalCreditBalance DEFAULT (0),
        UncollectedBalance decimal(19,4) NOT NULL CONSTRAINT DF_Accounts_UncollectedBalance DEFAULT (0),
        OverdraftLimit decimal(19,4) NOT NULL CONSTRAINT DF_Accounts_OverdraftLimit DEFAULT (0),
        Status nvarchar(20) NOT NULL CONSTRAINT DF_Accounts_Status DEFAULT (N'Open');
IF COL_LENGTH(N'dbo.Banks', N'FedwireCreditLimit') IS NULL
    ALTER TABLE dbo.Banks ADD
        FedwireCreditLimit decimal(19,4) NOT NULL CONSTRAINT DF_Banks_FedwireCreditLimit DEFAULT (0),
        AchExposureLimit decimal(19,4) NOT NULL CONSTRAINT DF_Banks_AchExposureLimit DEFAULT (1000000);
IF OBJECT_ID(N'dbo.SimulationStates', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SimulationStates (Id uniqueidentifier NOT NULL CONSTRAINT PK_SimulationStates PRIMARY KEY,
        Name nvarchar(60) NOT NULL, CurrentUtc datetimeoffset NOT NULL, IsPaused bit NOT NULL,
        SpeedMultiplier decimal(9,2) NOT NULL, UpdatedDate datetimeoffset NOT NULL);
    CREATE UNIQUE INDEX IX_SimulationStates_Name ON dbo.SimulationStates(Name);
END;
IF OBJECT_ID(N'dbo.OperationsCases', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OperationsCases (Id uniqueidentifier NOT NULL CONSTRAINT PK_OperationsCases PRIMARY KEY,
        BankId uniqueidentifier NOT NULL, Rail nvarchar(30) NOT NULL, CaseType nvarchar(60) NOT NULL,
        Reference nvarchar(120) NOT NULL, Summary nvarchar(240) NOT NULL, Status nvarchar(20) NOT NULL,
        Severity nvarchar(20) NOT NULL, AssignedTo nvarchar(120) NULL, Notes nvarchar(2000) NULL,
        Resolution nvarchar(500) NULL, DueDate datetimeoffset NOT NULL, CreatedDate datetimeoffset NOT NULL,
        UpdatedDate datetimeoffset NOT NULL, ResolvedDate datetimeoffset NULL,
        CONSTRAINT FK_OperationsCases_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id));
    CREATE INDEX IX_OperationsCases_BankId_Status_DueDate ON dbo.OperationsCases(BankId, Status, DueDate);
    CREATE INDEX IX_OperationsCases_BankId_Rail_Reference ON dbo.OperationsCases(BankId, Rail, Reference);
END;
COMMIT TRANSACTION;
