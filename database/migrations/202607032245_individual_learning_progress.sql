IF COL_LENGTH(N'dbo.LearningProgress', N'LearnerAlias') IS NULL
    ALTER TABLE dbo.LearningProgress ADD LearnerAlias nvarchar(80) NOT NULL
        CONSTRAINT DF_LearningProgress_LearnerAlias DEFAULT (N'Bank team');
IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.LearningProgress')
    AND name = N'IX_LearningProgress_BankId_MissionId')
    DROP INDEX IX_LearningProgress_BankId_MissionId ON dbo.LearningProgress;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.LearningProgress')
    AND name = N'IX_LearningProgress_BankId_LearnerAlias_MissionId')
    CREATE UNIQUE INDEX IX_LearningProgress_BankId_LearnerAlias_MissionId
        ON dbo.LearningProgress(BankId, LearnerAlias, MissionId);
