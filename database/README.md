# Database

ACH entry approval audit fields support the lab's maker/approver learning workflow. They record authorization evidence only; payment acceptance, file delivery, settlement, and accounting remain separate durable states.

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

`202607021800_learning_progress.sql` adds durable mission progress and knowledge-check attempts
for the guided curriculum and instructor dashboard.

`202607022100_message_reliability.sql` adds bank-attributable outbox records, a durable consumer
inbox, and an operations dead-letter store. Consumers retry three times; operations users may
replay only failed messages owned by their active bank.

`202607031800_learning_evidence.sql` adds evidence-backed mission runs, learner/cohort attribution,
confidence ratings, and instructor assignments. Runs retain before-state balance snapshots and
links to bank-scoped simulator items; they do not duplicate or alter payment truth.

`202607032200_simulation_operations_and_availability.sql` adds the shared lab clock, operational
case work queue, account availability components, and bank exposure/liquidity limits. These are
teaching controls and do not represent regulatory compliance or a production core ledger.

`202607032230_learning_explanation_rubric.sql` stores learner explanations and transparent rubric
scores. `202607032245_individual_learning_progress.sql` separates individual/cohort progress while
keeping payment evidence and role handoffs bank-scoped.

## Fictional traffic population

`seeds/002_traffic_population.sql` and the forward-only data migration
`migrations/202610261800_traffic_population.sql` add 44 fictional customers and 76
accounts across the four original domestic banks. Each bank receives an employer,
a supplier business, a utility company, and eight people with checking and savings.
The same catalog is applied idempotently by `TrafficPopulation.SeedAsync` during
web startup. SQL seed numbers and opening balances match that catalog.

Account numbers use the reserved lab pattern `90{routingNumber}{profileNumber:00}`.
Profiles 01–03 are business operating accounts, 04–11 personal checking, and
12–19 personal savings. Labels are teaching metadata inferred from this seed catalog;
no new account-product schema or authentication users are introduced. Only missing
accounts get an opening balance. Existing balances, holds, customers, and payment
history are never reset. The existing EF entities and baseline table definitions
are unchanged because this is an additive dataset, not a schema change.

ACH generator schedules reuse `SimulationStates` under `ach-traffic:{bankId:N}:{mode}`
and never change the `default` learning clock. Effective dates continue to follow
that shared clock and the domain banking-day calendar.
