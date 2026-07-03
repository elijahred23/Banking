# Database change guidance

This directory is the reviewable SQL representation of the schema used by EF Core and the local startup upgrader.

## Required synchronization

For every durable model change, update all applicable locations together:

1. The entity and configuration in `Banking.Domain` / `BankingDbContext`.
2. The idempotent upgrade SQL in `Banking.Infrastructure/ServiceRegistration.cs` for existing local databases.
3. The matching baseline definition in `database/tables/` for empty databases.
4. `database/bootstrap.sql` when adding a new table file.
5. A new forward-only script in `database/migrations/`.
6. `database/README.md` when the migration introduces a new subsystem or operational requirement.

## SQL conventions

- Migrations are immutable once created. Fix a released migration with a later migration.
- Use UTC timestamp prefixes: `YYYYMMDDHHMM_description.sql`.
- Make startup and seed scripts idempotent with `OBJECT_ID`, `COL_LENGTH`, or equivalent guards.
- Name primary keys, foreign keys, checks, defaults, and indexes explicitly.
- Match EF lengths, nullability, precision, cascade behavior, enum storage, and filtered indexes exactly.
- Add indexes for ownership/scoping queries, correlation identifiers, retry scans, and foreign keys used by workers.
- Preserve financial precision (`decimal(19,4)` unless a domain requirement says otherwise).
- Do not place secrets, production identifiers, or real customer/payment data in schema or seed files.

## Safety and verification

- Never drop or truncate lab data as part of a routine startup upgrade.
- Keep learning-data resets separate from payment/accounting data.
- Test both paths when possible: upgrade an existing local database and bootstrap an empty database.
- Run the repository build/tests and `git diff --check` after schema changes.

