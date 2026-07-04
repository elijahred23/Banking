IF OBJECT_ID(N'dbo.LearningAssignments', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.LearningAssignments (Id uniqueidentifier NOT NULL CONSTRAINT PK_LearningAssignments PRIMARY KEY, BankId uniqueidentifier NOT NULL, MissionId nvarchar(40) NOT NULL, Cohort nvarchar(80) NOT NULL, AssignedBy nvarchar(120) NOT NULL, DueDate datetimeoffset NULL, IsActive bit NOT NULL, CreatedDate datetimeoffset NOT NULL, CONSTRAINT FK_LearningAssignments_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id) ON DELETE CASCADE);
 CREATE INDEX IX_LearningAssignments_BankId_Cohort_MissionId_IsActive ON dbo.LearningAssignments(BankId, Cohort, MissionId, IsActive);
END;
