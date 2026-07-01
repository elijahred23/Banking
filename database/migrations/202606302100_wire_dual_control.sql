SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'dbo.WireTransfers', N'CustomerReference') IS NULL
    ALTER TABLE dbo.WireTransfers ADD
        CustomerReference nvarchar(35) NULL,
        CreatedBy nvarchar(120) NULL,
        ApprovedBy nvarchar(120) NULL,
        ApprovedDate datetimeoffset NULL;

IF COL_LENGTH(N'dbo.WireTransfers', N'ComplianceStatus') IS NULL
    ALTER TABLE dbo.WireTransfers ADD
        ComplianceStatus nvarchar(30) NULL,
        ComplianceReason nvarchar(500) NULL,
        ComplianceReviewedBy nvarchar(120) NULL,
        ComplianceReviewedDate datetimeoffset NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'dbo.WireTransfers')
        AND name = N'IX_WireTransfers_BankId_CustomerReference')
    EXEC(N'CREATE UNIQUE INDEX IX_WireTransfers_BankId_CustomerReference
        ON dbo.WireTransfers(BankId, CustomerReference)
        WHERE CustomerReference IS NOT NULL;');

IF OBJECT_ID(N'dbo.OutboxMessages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OutboxMessages (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_OutboxMessages PRIMARY KEY,
        Queue nvarchar(120) NOT NULL, Payload nvarchar(max) NOT NULL,
        Attempts int NOT NULL, LastError nvarchar(1000) NULL,
        CreatedDate datetimeoffset NOT NULL, NextAttemptDate datetimeoffset NULL,
        PublishedDate datetimeoffset NULL);
    CREATE INDEX IX_OutboxMessages_PublishedDate_NextAttemptDate
        ON dbo.OutboxMessages(PublishedDate, NextAttemptDate);
END;

IF OBJECT_ID(N'dbo.OutboxMessages', N'U') IS NOT NULL
    AND COL_LENGTH(N'dbo.OutboxMessages', N'NextAttemptDate') IS NULL
    ALTER TABLE dbo.OutboxMessages ADD NextAttemptDate datetimeoffset NULL;

COMMIT TRANSACTION;
