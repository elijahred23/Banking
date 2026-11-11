IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.Banks') AND name = N'IX_Banks_FedParticipantId')
    CREATE UNIQUE INDEX IX_Banks_FedParticipantId ON dbo.Banks(FedParticipantId);
