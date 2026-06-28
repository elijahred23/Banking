IF OBJECT_ID(N'dbo.WireEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.WireEvents
    (
        Id uniqueidentifier NOT NULL,
        WireTransferId uniqueidentifier NOT NULL,
        EventType nvarchar(60) NOT NULL,
        Description nvarchar(500) NOT NULL,
        CreatedDate datetimeoffset NOT NULL,
        CONSTRAINT PK_WireEvents PRIMARY KEY (Id),
        CONSTRAINT FK_WireEvents_WireTransfers_WireTransferId FOREIGN KEY (WireTransferId)
            REFERENCES dbo.WireTransfers (Id) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.WireEvents') AND name = N'IX_WireEvents_WireTransferId')
    CREATE INDEX IX_WireEvents_WireTransferId ON dbo.WireEvents (WireTransferId);
