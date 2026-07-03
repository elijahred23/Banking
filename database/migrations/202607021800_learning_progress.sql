IF OBJECT_ID(N'dbo.LearningProgress', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.LearningProgress (Id uniqueidentifier NOT NULL CONSTRAINT PK_LearningProgress PRIMARY KEY, BankId uniqueidentifier NOT NULL, MissionId nvarchar(40) NOT NULL, Prediction nvarchar(200) NULL, IsComplete bit NOT NULL, CorrectAnswers int NOT NULL, AnsweredQuestions int NOT NULL, UpdatedDate datetimeoffset NOT NULL, CONSTRAINT FK_LearningProgress_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id) ON DELETE CASCADE);
 CREATE UNIQUE INDEX IX_LearningProgress_BankId_MissionId ON dbo.LearningProgress(BankId, MissionId);
END;
IF OBJECT_ID(N'dbo.LearningAttempts', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.LearningAttempts (Id uniqueidentifier NOT NULL CONSTRAINT PK_LearningAttempts PRIMARY KEY, BankId uniqueidentifier NOT NULL, MissionId nvarchar(40) NOT NULL, QuestionId nvarchar(40) NOT NULL, SelectedAnswer nvarchar(200) NOT NULL, IsCorrect bit NOT NULL, CreatedDate datetimeoffset NOT NULL, CONSTRAINT FK_LearningAttempts_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id) ON DELETE CASCADE);
 CREATE INDEX IX_LearningAttempts_BankId_MissionId_CreatedDate ON dbo.LearningAttempts(BankId, MissionId, CreatedDate);
END;
