# Correspondent account statements and reconciliation

The reporting center is a learning-only correspondent banking subsystem. Statements are immutable snapshots of finalized account ledger entries, not reconstructions from wire statuses or generic bank-master balances. It supports daily `camt.053.001.08`, on-demand intraday `camt.052.001.08`, and booked-entry `camt.054.001.08` notifications, with XML, PDF and CSV downloads.

## Run the first end-to-end scenario

Run Web first so the idempotent schema upgrade completes, then keep the existing WireService, MessageManager and FedwireSimulator running. Web hosts the statement posting/scheduling/delivery worker alongside its durable outbox publisher; no additional application is needed.

1. Sign in at **Bankers Bank** as **Approver**. Open **Treasury → Statements & reconciliation**. Create a respondent account for a community bank, identifier `LAB0042`, with a $500,000 opening book position.
2. This opening position is explicit synthetic lab setup on both banks' books. It does not transfer reserve cash or reset existing balances. Use ordinary payments for subsequent cash movements.
3. Switch to the respondent's **Maker** persona. Open the correspondent account and request a $100,000 Fedwire institution transfer to another domestic participant. The amount is reserved in the correspondent account without a posted debit.
4. Switch to Bankers Bank's **Approver** and approve the pending instruction in its Wires worklist. WireService builds the `pacs.009`; the existing simulator settles it. Once the issuer's finalized reserve journal is present, the statement worker posts the correspondent account debit and publishes a durable respondent posting notice. Rejections release the reservation.
5. At a third bank, originate and approve a $250,000 institution transfer to Bankers Bank using the existing wire workflow. As Bankers Bank **Operations**, open the correspondent account and assign the completed incoming FI wire to this respondent. A wire may be assigned once. Its account credit waits for its finalized reserve journal.
6. Configure the account's daily `camt.053` schedule as Bankers Bank **Operations**: IANA time zone, local reporting time (for example 18:00), and optional ISO-format holidays. Schedule controls are on the account page.
7. Advance the shared simulation clock to the due reporting time using the existing Operations clock controls. The account's first period begins when the schedule is created. Scheduled daily periods include their start and exclude their end; the next period begins exactly at the previous end.
8. The worker generates an immutable statement with opening $500,000, debit $100,000, credit $250,000 and closing $650,000, provided all three setup/posting steps fall in that reporting interval. It queues delivery through the durable outbox.
9. Switch to respondent **Operations**. The delivered statement appears in its inbox. Download XML, PDF or CSV, inspect booked/value dates, expand transaction evidence, and reconcile against its own due-from ledger. Both parties see the reconciliation history.

The issuer can assess an approved service fee from the account page. A $25 fee changes a $650,000 closing position to $649,975. Fee journals are book entries; reserve cash is not moved again.

## Account and journal mapping

Existing correspondent relationships were routing edges, without respondent account balances. Three supporting tables now provide the missing book foundation: `CorrespondentAccounts`, `CorrespondentPaymentLinks`, and `CorrespondentLedgerEntries`.

- An account explicitly identifies its issuer and respondent. Each bank sees only its own relationships; account and statement identifiers supplied by a browser are checked against the signed-in bank.
- Pending account-funded wires are ordinary issuer-owned Fedwire institution transfers requiring its approval. The payment link reserves the respondent balance and idempotently associates the cash posting with one account.
- The issuer's finalized wire journal is the posting source. A settled status without its cash journal is insufficient. An ambiguous, unbalanced, rejected, or unaccepted cash source does not generate an account movement.
- The issuer posts a balanced due-to/clearing reclassification; the existing wire journal already posted reserve cash. Fees use due-to/fee-income. All amounts retain decimal(19,4) precision.
- Respondent notices create an independent balanced due-from/payment-clearing or fee-expense journal. Their booking timestamps use the simulation clock when processed; the issuer's value date is preserved. The two banks' ledgers can therefore have genuine timing differences.
- Journals are append-only through EF and uniquely keyed by bank, posting ID and account/offset line. Delivery replay cannot post twice. The unified journal and general reconciliation controls include correspondent book entries under the explicit `CorrespondentBook` classification; this is not another payment rail.

The initial adapters cover correspondent-funded institution Fedwires, assigned incoming institution Fedwires, and service fees. They do not automatically turn unrelated ACH, check, customer, master-account or SWIFT activity into correspondent postings. Adding those adapters requires an explicit account ownership/mapping contract; such activity is not fabricated in a statement.

## Snapshots, corrections and formats

The five reporting tables are `AccountStatements`, `AccountStatementEntries`, `StatementSchedules`, `StatementDeliveries`, and `StatementReconciliations`.

A statement stores account and bank identification, period, sequence, version, signed balances/totals, entry-level ledger/payment references, and its generated XML. Statement and entry updates/deletes are rejected by the EF save boundary. Issuers can explicitly issue a new corrected version linked to the latest previous version of the same account/type/period. Originals and delivery history remain available.

Generation checks the ledger-derived closing balance against the issuer's stored position adjusted for subsequent postings. A balance break creates a held snapshot and an issuer operations case, with no delivery message. Normal snapshots produce one delivery record and an outbox command. Respondents cannot retrieve pending or held snapshots by guessing their URL or download ID.

Intraday reports include the cutoff instant so bookings at the current shared clock time are visible. Refreshing an unchanged cutoff returns the same snapshot; new postings at that clock time create a new immutable version. Daily intervals remain half open. Notifications are selected by stable posting ID, not artificial timestamp ordering, and do not assert a full-period opening or closing balance.

The XML builder validates the `.001.08` namespace, message root, USD amounts, booking/value dates and booked debit/credit indicators. This is a structural lab profile, not full official ISO schema validation or rail certification. Human-readable PDFs are generated in-process without external services, with repeated page headers, account identifiers, totals, wrapped references and page numbering. Their basic Latin display is intended for synthetic lab bank names; CSV and XML preserve Unicode. CSV fields escape quotes and neutralize formula-leading text.

## Schedules and recovery

Daily schedules have durable period cursors and short wall-clock worker leases, while due times follow the shared simulation clock. Time zones and per-account holiday lists govern eligible business days. DST gaps shift forward to a valid time; ambiguous times choose the later UTC occurrence. Weekend/holiday delays naturally extend the next statement's interval.

Missed runs are replayed in order, up to 20 per tick, rather than discarded or coalesced into an invented historical snapshot. Generation and cursor advancement commit atomically; unique account/type/period-end/version and account/sequence constraints protect against concurrent workers. Failed jobs retain their cursor, lease/error state, and recover after the lease expires. Pausing or editing a schedule does not erase overdue periods.

The existing outbox/inbox/dead-letter services transport `CORRESPONDENT.POSTING` and `STATEMENT.READY`. Inbox and dead-letter ownership resolve from their durable account/statement records. Bank Operations can inspect/replay failures through the existing operations tooling. Delivery receipt is distinct from respondent posting and from reconciliation. A duplicate delivery remains delivered without creating a second statement.

`RabbitMq:VirtualHost` is honored by the bus and monitor, allowing isolated lab/test environments; its default remains `/`. The operations queue monitor includes both new queues.

## Reconciliation

Reconciliation uses stable posting IDs first, then payment correlations, signed amounts and value dates. It checks missing debits/credits, amount mismatches, duplicate identifiers, independently stored due-from balances and matched out-of-period entries. Only differences fully explained by known matched timing items become `TimingDifference`; unrelated balance differences remain exceptions. Zero unexplained differences with no outstanding timing items become `Reconciled`.

Every run is an immutable result with expected/observed balances and detailed findings. True breaks open a respondent operations case; a later clear/timing-only rerun resolves that case without altering prior results. Delivery runs reconciliation automatically for full reports. If respondent posting arrives afterward, Operations can rerun reconciliation; booking in a later period remains an outstanding timing item rather than a false missing-payment break.

## Verification and schema

`051_AccountStatements.sql` is included by bootstrap and embedded for the startup upgrader. The forward script is `202611112200_account_statements.sql`. EF and SQL share money precision, restrictive FKs, explicit ownership/indexes, unique posting/statement keys and account rowversion control. Upgrades never delete financial history.

```sh
dotnet build Banking.slnx --no-restore
dotnet test Banking.slnx --no-build --no-restore
git diff --check
BANKING_RUN_EXTERNAL_INTEGRATION=1 dotnet test tests/Banking.Domain.Tests --no-build --no-restore --filter 'FullyQualifiedName~StatementIntegrationTests'
```

External tests create disposable named SQL databases and isolated RabbitMQ queue namespaces on the existing local containers. They exercise the $650,000 acceptance scenario through the actual wire/network/statement consumers, scheduled generation, automatic delivery and reconciliation, XML/PDF downloads, third-bank denial, concurrent generation, duplicate-safe mirror/delivery/notifications, delayed booking/timing items, corrected immutable versions, held balance breaks, same-clock intraday refresh, and rejection hold release. Unit tests cover arithmetic, structural ISO versions, reconciliation categories, DST/holiday scheduling, CSV safety and PDF pagination. PDF output has also been rendered and visually checked, including long-reference multipage statements.
