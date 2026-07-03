IF OBJECT_ID(N'dbo.LearningAttempts', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.LearningAttempts (Id uniqueidentifier NOT NULL CONSTRAINT PK_LearningAttempts PRIMARY KEY, BankId uniqueidentifier NOT NULL, MissionId nvarchar(40) NOT NULL, QuestionId nvarchar(40) NOT NULL, SelectedAnswer nvarchar(200) NOT NULL, IsCorrect bit NOT NULL, CreatedDate datetimeoffset NOT NULL, CONSTRAINT FK_LearningAttempts_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id) ON DELETE CASCADE);
 CREATE INDEX IX_LearningAttempts_BankId_MissionId_CreatedDate ON dbo.LearningAttempts(BankId, MissionId, CreatedDate);
END;
