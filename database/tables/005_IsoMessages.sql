IF OBJECT_ID(N'dbo.IsoMessages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.IsoMessages
    (
        Id uniqueidentifier NOT NULL,
        WireTransferId uniqueidentifier NULL,
        MessageExchangeId uniqueidentifier NULL,
        MessageType nvarchar(20) NOT NULL,
        Direction nvarchar(max) NOT NULL,
        XmlPayload nvarchar(max) NOT NULL,
        CreatedDate datetimeoffset NOT NULL,
        CONSTRAINT PK_IsoMessages PRIMARY KEY (Id),
        CONSTRAINT FK_IsoMessages_WireTransfers_WireTransferId FOREIGN KEY (WireTransferId)
            REFERENCES dbo.WireTransfers (Id) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.IsoMessages') AND name = N'IX_IsoMessages_WireTransferId')
    CREATE INDEX IX_IsoMessages_WireTransferId ON dbo.IsoMessages (WireTransferId);
