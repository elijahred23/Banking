IF OBJECT_ID(N'dbo.OperationsCases', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OperationsCases
    (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_OperationsCases PRIMARY KEY,
        BankId uniqueidentifier NOT NULL,
        Rail nvarchar(30) NOT NULL,
        CaseType nvarchar(60) NOT NULL,
        Reference nvarchar(120) NOT NULL,
        Summary nvarchar(240) NOT NULL,
        Status nvarchar(20) NOT NULL,
        Severity nvarchar(20) NOT NULL,
        AssignedTo nvarchar(120) NULL,
        Notes nvarchar(2000) NULL,
        Resolution nvarchar(500) NULL,
        DueDate datetimeoffset NOT NULL,
        CreatedDate datetimeoffset NOT NULL,
        UpdatedDate datetimeoffset NOT NULL,
        ResolvedDate datetimeoffset NULL,
        CONSTRAINT FK_OperationsCases_Banks_BankId FOREIGN KEY (BankId) REFERENCES dbo.Banks(Id)
    );
END;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.OperationsCases') AND name = N'IX_OperationsCases_BankId_Status_DueDate')
    CREATE INDEX IX_OperationsCases_BankId_Status_DueDate ON dbo.OperationsCases(BankId, Status, DueDate);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.OperationsCases') AND name = N'IX_OperationsCases_BankId_Rail_Reference')
    CREATE INDEX IX_OperationsCases_BankId_Rail_Reference ON dbo.OperationsCases(BankId, Rail, Reference);
