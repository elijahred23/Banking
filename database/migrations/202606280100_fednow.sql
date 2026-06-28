SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'dbo.WireTransfers', N'Rail') IS NULL
    ALTER TABLE dbo.WireTransfers ADD Rail nvarchar(max) NOT NULL
        CONSTRAINT DF_WireTransfers_Rail DEFAULT (N'Fedwire');

IF COL_LENGTH(N'dbo.Banks', N'FedNowEnabled') IS NULL
BEGIN
    ALTER TABLE dbo.Banks ADD
        FedNowEnabled bit NOT NULL CONSTRAINT DF_Banks_FedNowEnabled DEFAULT (1),
        FedNowSendEnabled bit NOT NULL CONSTRAINT DF_Banks_FedNowSendEnabled DEFAULT (1),
        FedNowReceiveEnabled bit NOT NULL CONSTRAINT DF_Banks_FedNowReceiveEnabled DEFAULT (1),
        FedNowRequestForPaymentEnabled bit NOT NULL CONSTRAINT DF_Banks_FedNowRfpEnabled DEFAULT (1),
        FedNowOnline bit NOT NULL CONSTRAINT DF_Banks_FedNowOnline DEFAULT (1);
END;

COMMIT TRANSACTION;
