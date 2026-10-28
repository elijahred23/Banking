IF OBJECT_ID(N'dbo.StudioDays', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.StudioDays (
 Id uniqueidentifier NOT NULL CONSTRAINT PK_StudioDays PRIMARY KEY,
 BankId uniqueidentifier NOT NULL, Kind nvarchar(8) NOT NULL, Phase int NOT NULL,
 BusinessDate date NOT NULL, StartedAt datetimeoffset NOT NULL, EndedAt datetimeoffset NULL,
 OpeningCash decimal(19,4) NOT NULL, OpeningCustomerBalance decimal(19,4) NOT NULL,
 OpeningHolds decimal(19,4) NOT NULL, StartedBy nvarchar(120) NOT NULL,
 PendingIncident nvarchar(32) NOT NULL, PendingIncidentCaseId uniqueidentifier NULL,
 CONSTRAINT FK_StudioDays_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id),
 CONSTRAINT FK_StudioDays_OperationsCases_PendingIncidentCaseId FOREIGN KEY (PendingIncidentCaseId) REFERENCES dbo.OperationsCases(Id));
 CREATE UNIQUE INDEX IX_StudioDays_BankId_Kind ON dbo.StudioDays(BankId, Kind) WHERE EndedAt IS NULL;
 CREATE INDEX IX_StudioDays_BankId_StartedAt ON dbo.StudioDays(BankId, StartedAt);
 CREATE INDEX IX_StudioDays_PendingIncidentCaseId ON dbo.StudioDays(PendingIncidentCaseId);
END;
