IF OBJECT_ID(N'dbo.MessageDeliveries', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MessageDeliveries
    (
        Id uniqueidentifier NOT NULL,
        WireTransferId uniqueidentifier NOT NULL,
        Destination nvarchar(80) NOT NULL,
        Status nvarchar(max) NOT NULL,
        Attempts int NOT NULL,
        LastError nvarchar(500) NULL,
        UpdatedDate datetimeoffset NOT NULL,
        CONSTRAINT PK_MessageDeliveries PRIMARY KEY (Id)
    );
END;
