IF OBJECT_ID(N'dbo.LearningProgress', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.LearningProgress (Id uniqueidentifier NOT NULL CONSTRAINT PK_LearningProgress PRIMARY KEY, BankId uniqueidentifier NOT NULL, MissionId nvarchar(40) NOT NULL, Prediction nvarchar(200) NULL, IsComplete bit NOT NULL, CorrectAnswers int NOT NULL, AnsweredQuestions int NOT NULL, NextReviewDate datetimeoffset NULL, UpdatedDate datetimeoffset NOT NULL, CONSTRAINT FK_LearningProgress_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id) ON DELETE CASCADE);
 CREATE UNIQUE INDEX IX_LearningProgress_BankId_MissionId ON dbo.LearningProgress(BankId, MissionId);
END;
