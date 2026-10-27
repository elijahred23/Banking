IF OBJECT_ID(N'dbo.StudioSettings', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.StudioSettings (
 Id uniqueidentifier NOT NULL CONSTRAINT PK_StudioSettings PRIMARY KEY,
 BankId uniqueidentifier NOT NULL, Kind nvarchar(8) NOT NULL,
 Rail nvarchar(24) NOT NULL, WireScenario nvarchar(32) NOT NULL, AchScenario nvarchar(32) NOT NULL,
 RemainingRuns int NULL, Preset nvarchar(32) NOT NULL, UseRoster bit NOT NULL, UpdatedDate datetimeoffset NOT NULL,
 CONSTRAINT FK_StudioSettings_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id));
 CREATE UNIQUE INDEX IX_StudioSettings_BankId_Kind ON dbo.StudioSettings(BankId, Kind);
END;
IF OBJECT_ID(N'dbo.PayrollMembers', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.PayrollMembers (
 Id uniqueidentifier NOT NULL CONSTRAINT PK_PayrollMembers PRIMARY KEY,
 BankId uniqueidentifier NOT NULL, EmployerAccountId uniqueidentifier NOT NULL,
 CheckingAccountId uniqueidentifier NOT NULL, SavingsAccountId uniqueidentifier NULL,
 NetPay decimal(19,4) NOT NULL, SavingsPercent decimal(5,2) NOT NULL, Enabled bit NOT NULL, NextPayDate date NOT NULL,
 CONSTRAINT FK_PayrollMembers_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id),
 CONSTRAINT FK_PayrollMembers_Accounts_EmployerAccountId FOREIGN KEY (EmployerAccountId) REFERENCES dbo.Accounts(Id),
 CONSTRAINT FK_PayrollMembers_Accounts_CheckingAccountId FOREIGN KEY (CheckingAccountId) REFERENCES dbo.Accounts(Id),
 CONSTRAINT FK_PayrollMembers_Accounts_SavingsAccountId FOREIGN KEY (SavingsAccountId) REFERENCES dbo.Accounts(Id));
 CREATE UNIQUE INDEX IX_PayrollMembers_BankId_EmployerAccountId_CheckingAccountId ON dbo.PayrollMembers(BankId, EmployerAccountId, CheckingAccountId);
 CREATE INDEX IX_PayrollMembers_BankId_NextPayDate ON dbo.PayrollMembers(BankId, NextPayDate);
 CREATE INDEX IX_PayrollMembers_EmployerAccountId ON dbo.PayrollMembers(EmployerAccountId);
 CREATE INDEX IX_PayrollMembers_CheckingAccountId ON dbo.PayrollMembers(CheckingAccountId);
 CREATE INDEX IX_PayrollMembers_SavingsAccountId ON dbo.PayrollMembers(SavingsAccountId);
END;
IF OBJECT_ID(N'dbo.BalanceReceipts', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.BalanceReceipts (
 Id uniqueidentifier NOT NULL CONSTRAINT PK_BalanceReceipts PRIMARY KEY, BankId uniqueidentifier NOT NULL,
 PaymentKey uniqueidentifier NOT NULL, Rail nvarchar(24) NOT NULL, AccountCode nvarchar(80) NOT NULL,
 AccountLabel nvarchar(120) NOT NULL, Movement nvarchar(16) NOT NULL,
 [Before] decimal(19,4) NOT NULL, Delta decimal(19,4) NOT NULL, [After] decimal(19,4) NOT NULL,
 CreatedDate datetimeoffset NOT NULL,
 CONSTRAINT FK_BalanceReceipts_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id));
 CREATE UNIQUE INDEX IX_BalanceReceipts_BankId_PaymentKey_AccountCode_Movement ON dbo.BalanceReceipts(BankId, PaymentKey, AccountCode, Movement);
 CREATE INDEX IX_BalanceReceipts_BankId_CreatedDate ON dbo.BalanceReceipts(BankId, CreatedDate);
END;
