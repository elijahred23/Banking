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

PRINT 'BankingDb baseline schema is ready.';
