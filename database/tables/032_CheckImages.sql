IF OBJECT_ID(N'dbo.CheckImages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CheckImages (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_CheckImages PRIMARY KEY,
        CheckDepositId uniqueidentifier NOT NULL,
        Side nvarchar(10) NOT NULL,
        Format nvarchar(10) NOT NULL,
        FileName nvarchar(120) NOT NULL,
        ContentType nvarchar(80) NOT NULL,
        Content varbinary(max) NOT NULL,
        SizeBytes int NOT NULL,
        Sha256Hash nvarchar(128) NOT NULL,
        CreatedDate datetimeoffset NOT NULL,
        CONSTRAINT FK_CheckImages_CheckDeposits_CheckDepositId FOREIGN KEY (CheckDepositId) REFERENCES dbo.CheckDeposits(Id) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX IX_CheckImages_CheckDepositId_Side ON dbo.CheckImages(CheckDepositId, Side);
END;
