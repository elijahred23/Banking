# Database

SQL Server assets for `BankingDb` live here.

## Layout

- `tables/`: the baseline definition for each table, ordered by dependency.
- `migrations/`: immutable, ordered scripts for changes after the baseline.
- `seeds/`: repeatable data sets for local and demonstration environments.
- `bootstrap.sql`: creates the complete baseline schema in an empty database.
- `seed.sql`: applies the local lab seed data.

Run the baseline from the repository root:

```bash
sqlcmd -S localhost,11433 -U sa -C -d BankingDb -i database/bootstrap.sql
```

The command prompts for the SQL password. The target database must already exist.

Seed the lab data from the repository root:

```bash
sqlcmd -S localhost,11433 -U sa -C -d BankingDb -i database/seed.sql
```

The seed script is idempotent: rerunning it does not duplicate banks, customers, accounts, or
correspondent relationships.

## Conventions

1. Keep each table definition in its own file under `tables/`.
2. When the schema changes, update the matching table definition and add a forward-only migration under `migrations/`.
3. Name migrations `YYYYMMDDHHMM_description.sql` using UTC time.
4. Never edit a migration after it has been applied to a shared environment.
5. Keep the EF model in `BankingDbContext` synchronized with these scripts.

The application currently uses EF Core `EnsureCreated` for local first-run setup. These scripts provide a reviewable baseline and a controlled location for subsequent schema changes.

For existing lab databases, `202606280000_payment_learning.sql` adds held balances,
explicit beneficiary data, processing scenarios, and the balanced journal table. Web
startup runs the same upgrade idempotently for the default local workflow.

`202606282000_add_check_processing.sql` adds image cash letters, check deposits,
front/back TIFF blobs, returns, event timelines, and balanced check journals. Web
startup applies the equivalent idempotent upgrade to existing local databases.

`202606282100_correspondent_routing.sql` adds configured SWIFT correspondent edges and the
persisted route/route-step history used for direct and one-intermediary payment delivery.

`202606282300_pacs009_institution_transfers.sql` distinguishes customer `pacs.008` wires from
financial institution `pacs.009` liquidity transfers. Web startup applies the same column
upgrade idempotently for existing local databases.

`202606291900_nonvalue_message_workflows.sql` adds independently owned ISO message exchanges
for request-for-payment, account-reporting, and system-event conversations.
