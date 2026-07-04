IF COL_LENGTH(N'dbo.LearningAttempts', N'LearnerExplanation') IS NULL
    ALTER TABLE dbo.LearningAttempts ADD LearnerExplanation nvarchar(1000) NULL,
        ExplanationScore int NOT NULL CONSTRAINT DF_LearningAttempts_ExplanationScore DEFAULT (0);
