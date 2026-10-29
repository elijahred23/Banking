IF OBJECT_ID(N'dbo.BankReportTasks', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.BankReportTasks (
 Id uniqueidentifier NOT NULL CONSTRAINT PK_BankReportTasks PRIMARY KEY,
 BankId uniqueidentifier NOT NULL, Name nvarchar(100) NOT NULL,
 Kind nvarchar(24) NOT NULL, Frequency nvarchar(24) NOT NULL,
 DailyAtUtc time NOT NULL, LookbackHours int NOT NULL, Enabled bit NOT NULL,
 NextRunAt datetimeoffset NOT NULL, CreatedBy nvarchar(120) NOT NULL,
 CreatedAt datetimeoffset NOT NULL, UpdatedAt datetimeoffset NOT NULL,
 CONSTRAINT FK_BankReportTasks_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id));
 CREATE INDEX IX_BankReportTasks_BankId_CreatedAt ON dbo.BankReportTasks(BankId, CreatedAt);
 CREATE INDEX IX_BankReportTasks_Enabled_NextRunAt ON dbo.BankReportTasks(Enabled, NextRunAt);
END;
IF OBJECT_ID(N'dbo.BankReportRuns', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.BankReportRuns (
 Id uniqueidentifier NOT NULL CONSTRAINT PK_BankReportRuns PRIMARY KEY,
 BankId uniqueidentifier NOT NULL, TaskId uniqueidentifier NOT NULL,
 TaskName nvarchar(100) NOT NULL, Kind nvarchar(24) NOT NULL, LookbackHours int NOT NULL,
 Status nvarchar(24) NOT NULL, RequestedBy nvarchar(120) NOT NULL,
 RequestedAt datetimeoffset NOT NULL, ScheduledFor datetimeoffset NULL,
 StartedAt datetimeoffset NULL, CompletedAt datetimeoffset NULL,
 LeaseToken uniqueidentifier NULL, LeaseUntil datetimeoffset NULL, Attempts int NOT NULL,
 Error nvarchar(1000) NULL, ReportJson nvarchar(max) NULL,
 CONSTRAINT FK_BankReportRuns_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id),
 CONSTRAINT FK_BankReportRuns_BankReportTasks_TaskId FOREIGN KEY (TaskId) REFERENCES dbo.BankReportTasks(Id));
 CREATE UNIQUE INDEX IX_BankReportRuns_TaskId ON dbo.BankReportRuns(TaskId) WHERE CompletedAt IS NULL;
 CREATE INDEX IX_BankReportRuns_BankId_RequestedAt ON dbo.BankReportRuns(BankId, RequestedAt);
 CREATE INDEX IX_BankReportRuns_Status_LeaseUntil_RequestedAt ON dbo.BankReportRuns(Status, LeaseUntil, RequestedAt);
END;
