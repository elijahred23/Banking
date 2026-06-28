SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- Forward-only ACH/NACHA learning schema. Kept self-contained for deployment runners.
:r database/tables/020_AchFiles.sql
:r database/tables/021_AchBatches.sql
:r database/tables/022_AchEntries.sql
:r database/tables/023_AchReturns.sql
:r database/tables/024_AchNotificationsOfChange.sql
:r database/tables/025_AchLedgerEntries.sql

COMMIT TRANSACTION;
