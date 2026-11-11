IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.WireTransfers') AND name = N'UX_WireTransfers_IncomingSettlement')
    CREATE UNIQUE INDEX UX_WireTransfers_IncomingSettlement ON dbo.WireTransfers(BankId, CorrelationId) WHERE Direction = N'Incoming';
