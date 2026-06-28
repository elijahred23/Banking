# Migrations

Put forward-only schema changes in this directory using:

```text
YYYYMMDDHHMM_description.sql
```

Copy `_template.sql.example` when starting a migration. Each migration should:

- be safe to run once against the expected prior schema;
- use a transaction when SQL Server permits it;
- fail immediately with `XACT_ABORT` and `THROW`;
- update the matching baseline file under `../tables/`;
- include verification queries as comments when useful.

Apply migrations explicitly in filename order with `sqlcmd -d BankingDb -i <file>`.
