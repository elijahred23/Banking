IF COL_LENGTH(N'dbo.LearningAttempts', N'LearnerAlias') IS NULL
 ALTER TABLE dbo.LearningAttempts ADD LearnerAlias nvarchar(80) NOT NULL CONSTRAINT DF_LearningAttempts_LearnerAlias DEFAULT (N'Bank team');
IF COL_LENGTH(N'dbo.LearningAttempts', N'Confidence') IS NULL
 ALTER TABLE dbo.LearningAttempts ADD Confidence int NOT NULL CONSTRAINT DF_LearningAttempts_Confidence DEFAULT (3);
IF COL_LENGTH(N'dbo.LearningProgress', N'NextReviewDate') IS NULL
 ALTER TABLE dbo.LearningProgress ADD NextReviewDate datetimeoffset NULL;

IF OBJECT_ID(N'dbo.LearningRuns', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.LearningRuns (Id uniqueidentifier NOT NULL CONSTRAINT PK_LearningRuns PRIMARY KEY, BankId uniqueidentifier NOT NULL, MissionId nvarchar(40) NOT NULL, LearnerAlias nvarchar(80) NOT NULL, Rail nvarchar(30) NOT NULL, ItemType nvarchar(20) NULL, ItemId uniqueidentifier NULL, Prediction nvarchar(200) NULL, BaselineCustomerBalance decimal(19,4) NOT NULL, BaselineHeldBalance decimal(19,4) NOT NULL, BaselineMasterBalance decimal(19,4) NOT NULL, Status nvarchar(20) NOT NULL, EvidenceSummary nvarchar(2000) NULL, EvidenceScore int NOT NULL, StartedDate datetimeoffset NOT NULL, UpdatedDate datetimeoffset NOT NULL, CompletedDate datetimeoffset NULL, CONSTRAINT FK_LearningRuns_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id) ON DELETE CASCADE);
 CREATE INDEX IX_LearningRuns_BankId_MissionId_StartedDate ON dbo.LearningRuns(BankId, MissionId, StartedDate);
 CREATE INDEX IX_LearningRuns_BankId_ItemType_ItemId ON dbo.LearningRuns(BankId, ItemType, ItemId);
END;

IF OBJECT_ID(N'dbo.LearningAssignments', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.LearningAssignments (Id uniqueidentifier NOT NULL CONSTRAINT PK_LearningAssignments PRIMARY KEY, BankId uniqueidentifier NOT NULL, MissionId nvarchar(40) NOT NULL, Cohort nvarchar(80) NOT NULL, AssignedBy nvarchar(120) NOT NULL, DueDate datetimeoffset NULL, IsActive bit NOT NULL, CreatedDate datetimeoffset NOT NULL, CONSTRAINT FK_LearningAssignments_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id) ON DELETE CASCADE);
 CREATE INDEX IX_LearningAssignments_BankId_Cohort_MissionId_IsActive ON dbo.LearningAssignments(BankId, Cohort, MissionId, IsActive);
END;
