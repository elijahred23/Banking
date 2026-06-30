SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.IsoMessages')
    AND name = N'WireTransferId' AND is_nullable = 0)
    ALTER TABLE dbo.IsoMessages ALTER COLUMN WireTransferId uniqueidentifier NULL;

:r database/tables/014_MessageExchanges.sql

COMMIT TRANSACTION;
