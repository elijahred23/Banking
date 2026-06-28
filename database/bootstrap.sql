:ON ERROR EXIT

SET NOCOUNT ON;
SET XACT_ABORT ON;

:r database/tables/001_Banks.sql
:r database/tables/002_Customers.sql
:r database/tables/003_Accounts.sql
:r database/tables/004_WireTransfers.sql
:r database/tables/005_IsoMessages.sql
:r database/tables/006_WireEvents.sql
:r database/tables/007_MessageDeliveries.sql
:r database/tables/008_FedSettlements.sql
:r database/tables/009_LedgerEntries.sql
:r database/tables/020_AchFiles.sql
:r database/tables/021_AchBatches.sql
:r database/tables/022_AchEntries.sql
:r database/tables/023_AchReturns.sql
:r database/tables/024_AchNotificationsOfChange.sql
:r database/tables/025_AchLedgerEntries.sql

PRINT 'BankingDb baseline schema is ready.';
