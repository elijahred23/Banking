SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'dbo.WireTransfers', N'TransferType') IS NULL
    ALTER TABLE dbo.WireTransfers ADD TransferType nvarchar(max) NOT NULL
        CONSTRAINT DF_WireTransfers_TransferType DEFAULT (N'CustomerCreditTransfer');

COMMIT TRANSACTION;
