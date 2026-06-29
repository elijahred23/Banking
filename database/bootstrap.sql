:ON ERROR EXIT

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;

:r database/tables/001_Banks.sql
:r database/tables/002_Customers.sql
:r database/tables/003_Accounts.sql
:r database/tables/004_WireTransfers.sql
:r database/tables/005_IsoMessages.sql
:r database/tables/006_WireEvents.sql
:r database/tables/007_MessageDeliveries.sql
:r database/tables/008_FedSettlements.sql
:r database/tables/009_LedgerEntries.sql
:r database/tables/010_CorrespondentRelationships.sql
:r database/tables/011_PaymentRoutes.sql
:r database/tables/012_PaymentRouteSteps.sql
:r database/tables/013_WireCases.sql
:r database/tables/020_AchFiles.sql
:r database/tables/021_AchBatches.sql
:r database/tables/022_AchEntries.sql
:r database/tables/023_AchReturns.sql
:r database/tables/024_AchNotificationsOfChange.sql
:r database/tables/025_AchLedgerEntries.sql
:r database/tables/030_CheckCashLetters.sql
:r database/tables/031_CheckDeposits.sql
:r database/tables/032_CheckImages.sql
:r database/tables/033_CheckReturns.sql
:r database/tables/034_CheckLedgerEntries.sql
:r database/tables/035_CheckEvents.sql

PRINT 'BankingDb baseline schema is ready.';
