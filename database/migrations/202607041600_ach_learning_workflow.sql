IF OBJECT_ID(N'dbo.AchEntries', N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.AchEntries', N'CreatedBy') IS NULL
BEGIN
    ALTER TABLE dbo.AchEntries ADD
        CreatedBy nvarchar(120) NULL,
        ApprovedBy nvarchar(120) NULL,
        ApprovedDate datetimeoffset NULL,
        ApprovalDecisionReason nvarchar(500) NULL;
END;
