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

## Studio banking days

`migrations/202610280100_studio_days.sql` adds `StudioDays`, with one active day per
bank/studio enforced by a filtered unique index. The startup upgrade runner applies
the matching embedded baseline SQL idempotently. Days retain opening balances,
phase, dates, and a queued incident reference; incident and customer-inquiry audit
records reuse bank-scoped `OperationsCases`. Starting or closing a day never resets
payments, accounts, or learning progress. Existing generator schedules and
`StudioSettings` provide bounded phase workloads. Workers consume a queued incident
in the same serializable transaction that creates its payment/run and changes the
case reference to that payment. An incident remains queued if no eligible payment
can be generated. Close cancels only queued incidents and future generation;
already released work continues. Shared lab time is never changed by day phases.

## Campaign Room extension

`migrations/202610280200_studio_campaigns.sql` and `tables/048_StudioCampaigns.sql`
add bank-scoped scenario versions, campaign snapshots, fictional debit authorizations,
ACH repair links, append-only handoff/conversation notes, and funding requests.
`StudioDays.CampaignId` is nullable so existing days retain their history. The startup
runner applies the embedded script after StudioDays; table creation, column upgrades,
foreign keys, and indexes are idempotent. The prior forward migration is unchanged.

Repair links uniquely identify both original and replacement entries. Funding links
uniquely identify an incoming wire and never introduce a separate cash-posting path.
Scenario snapshots do not change when a new scenario version is authored. Revocation
and conversation due times are durable; response visibility is read-only and does
not require a worker. Existing generator transactions apply campaign profiles,
authorization checks, and one-shot incidents together with payment creation.
All monetary fields use decimal(19,4), and all new ownership/foreign-key queries have
matching indexes. No schema or startup operation resets payment or learning data.

`202610290045_bank_report_jobs.sql` adds bank-owned report tasks and immutable run
snapshots. `049_BankReportJobs.sql` is the matching baseline and embedded startup
upgrade, applied through `LabSchemaUpgradeRunner` from `ServiceRegistration`.
Tasks store UTC wall-clock schedules and a rolling activity window. A filtered
unique index permits only one unfinished run per task; a lease token protects
completion against stale workers. Reports and failures are retained when schedules
are edited or paused. The web host enqueues due work and consumes it independently;
SQL Server remains the durable job queue and report store. No extra broker queues
or external scheduler installation is required.

Operational jobs reuse `BankReportTasks`, `BankReportRuns`, `OperationsCases`, and
`StudioNotes`; the additional enum names fit the existing `nvarchar(24)` columns.
No schema change is required. Operational effects and their run evidence JSON commit
atomically under the existing lease token; a recovered worker retains the saved
snapshot. Case deduplication uses serializable bank/type/reference lookups over the
existing bank-scoped case indexes; resolved cases are retained rather than reopened.

### Independent incoming Fedwire posting

`202611111800_incoming_wire_uniqueness.sql` enforces one incoming wire per receiving bank and settlement correlation. Startup applies the same index. Existing duplicate incoming correlations must be investigated before upgrading; the upgrade does not delete financial records. Incoming `Received` Fedwire customer payments now await Operations posting from wire details. Existing completed payments remain unchanged. Settlement atomically creates outbox status and delivery records; the Web outbox publisher must run to recover unpublished deliveries. Immediate simulator responses can also arrive, and consumers must tolerate both copies.

`202611111801_network_participant_identity.sql` adds unique simulated Fed participant identities. The Network operator provisioning workflow uses the existing Banks/Customers/Accounts schema; no separate bank database or duplicate payment tables are introduced. See `../MULTI_BANK_NETWORK.md` for operator steps and recovery requirements.

### Treasury / federal funds

`050_FedFunds.sql` and `202611112000_fed_funds_treasury.sql` introduce the seven agreement, quote, credit-line, approval, accrual, obligation, and event tables. Startup loads the baseline as the embedded `FedFunds.sql` resource through `LabSchemaUpgradeRunner`; EF uses matching checks, precisions, restrictive foreign keys, unique keys, and rowversions. The Treasury worker must run alongside the existing Web outbox publisher and wire/network workers for automatic processing. No new cash settlement mechanism is added. See `../FED_FUNDS.md` for configuration, operator steps, accounting, and isolated SQL/Rabbit tests.

### Correspondent statements and reconciliation

`051_AccountStatements.sql` / `202611112200_account_statements.sql` add eight tables: correspondent account/payment-link/journal foundations plus immutable statement headers/entries, daily schedules, deliveries, and reconciliation results. EF, startup's embedded `AccountStatements.sql`, baseline bootstrap, and the forward migration share the same precision, FKs, indexes, checks, and unique keys. Web hosts the statement worker and existing outbox publisher; wire settlement remains in the existing workers. See `../STATEMENTS.md` for the operational workflow, synthetic opening positions, ledger coverage, calendar behavior, recovery, and SQL/Rabbit tests.

### Correspondent book payments and authorized funding

`052_CorrespondentPlatform.sql` and migration `202611111800_correspondent_platform.sql`
add bilateral service agreements, reserved book payment orders, immutable authorization
receipts, weekday funding authority and durable execution evidence. The startup upgrader
applies the same embedded baseline idempotently. Existing correspondent account balances,
ledger, outbox, statements and reconciliation remain authoritative; customer Accounts are
not suitable for these bank-owned due-to/due-from positions.

Open `/Correspondent` (also linked from the statement center). Open synthetic accounts
through Statements, propose an agreement as issuer maker, and approve it using distinct
issuer/respondent approvers. Book payments require respondent maker and issuer approver.
Funding requires source respondent maker plus separate source/issuer approvals. Three
strategies support fixed amounts, destination threshold replenishment, and source excess
sweeps. Funding is strictly between pre-funded respondent accounts at the same issuer;
no reserve cash or loan is created. Existing Fed funds loans and external wire funding
remain separate workflows. Existing legacy wire-only accounts remain usable without an
agreement; configuring an agreement gates new correspondent wire requests as well.

Funding runs and ledger postings commit in one serializable transaction. No external
side effect occurs inside the transaction, so a long-running lease is unnecessary.
The unique rule/scheduled-time key and the serializable cursor update prevent duplicate
runs. Failures roll back both postings and cursor; delivery uses the existing outbox.
Daily UTC weekday schedules coalesce missed dates into one current-balance evaluation,
then advance to the next weekday at the current evaluation time. Weekends are skipped;
holiday calendars and arbitrary schedule frequencies are not yet supported for funding.
Funds held by pending orders are excluded from funding capacity. Agreement suspension
blocks new requests and book approvals; pending orders can still be rejected to release
holds. Disable a funding rule to revoke future execution. Rule terms are immutable:
create and approve a replacement instead of editing approved authority.

External funding obligations with unknown settlement, per-rail service permissions,
portfolio credit limits, and automatic Fed funds borrowing are future extensions.
Do not represent these book funding rules as external settlement or lending authority.
