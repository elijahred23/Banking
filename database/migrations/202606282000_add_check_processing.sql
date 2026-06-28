SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- Learning-only image cash letter schema. Scripts are idempotent for local lab upgrades.
:r database/tables/030_CheckCashLetters.sql
:r database/tables/031_CheckDeposits.sql
:r database/tables/032_CheckImages.sql
:r database/tables/033_CheckReturns.sql
:r database/tables/034_CheckLedgerEntries.sql
:r database/tables/035_CheckEvents.sql

COMMIT TRANSACTION;
