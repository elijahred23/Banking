# Bank Payments Lab

A .NET 10 learning system that models Fedwire, FedNow, SWIFT CBPR+, batch-oriented ACH customer payments, and image-based check processing. It uses SQL Server on `localhost:11433` for durable state and RabbitMQ on `localhost:5672` for application events.

The lab emphasizes observable payment behavior rather than a single happy path:

- explicit beneficiary account and routing information;
- `head.001` business headers, UETRs, and semantic ISO profile validation;
- a version-aware catalog and generic header/envelope construction for the supported `admi`, `pacs`, `pain`, and `camt` wire-message families;
- concrete `pacs.008` customer transfers and `pacs.009.001.08` financial institution liquidity transfers, with `pacs.002` `PDNG`, `ACSC`, and `RJCT` statuses;
- a separate FedNow rail with `FEDNOW.OUTBOUND`/`FEDNOW.INBOUND` queues, participant capability and availability checks, receiver `ACCP`/`ACWP` processing states, immediate final settlement, duplicate-safe processing, and a $10 million customer-credit-transfer limit;
- international USD wires over a SWIFT CBPR+ serial-method learning flow with dedicated queues, IBAN and BICFI routing, structured cross-border party addresses, `pacs.008.001.08`, `INDA`, `SHAR`, and `ACSP`/`ACCC`/`RJCT` statuses;
- customer funds holds that become posted debits only after final settlement;
- balanced debit/credit journals for outgoing and incoming posting; and
- selectable pending, network-rejection, and malformed-message learning scenarios.
- bank-scoped maker, approver, compliance, and operations personas with dual-control release;
- unique customer payment references, durable outbox publication, and human compliance review queues;
- ACH maker/approver release, entry validation, scheduled batch cutoff, annotated fixed-width NACHA files and sandbox validation, FedACH-style settlement, returns, notifications of change, accounting reconstruction, blind operations cases, and EFTPS-style CCD+ addenda.
- check capture, MICR validation, front/back TIFF storage, simplified image cash letters, paying-bank presentment, settlement, and returns.
- an operator dashboard with live RabbitMQ queue depth, cross-rail lifecycle counts, payment exceptions, master-account movements, balanced-journal controls, and rail health.

## Run

Prerequisites: .NET 10, SQL Server on `localhost:11433`, and RabbitMQ on `localhost:5672`. The configured RabbitMQ credentials are `taskwrapper` / `taskwrapper123`; SQL Server uses the `BankingDb` database.

```bash
python3 run_all.py
```

The script builds the solution once, starts the web app before the worker services,
and stops every project when you press Ctrl+C. Run `python3 run_all.py --help` for
configuration and build options.

To debug the complete stack in VS Code, open **Run and Debug**, select
**Banking: All services**, and press F5. The compound configuration builds the solution
once, starts every web/worker/simulator project under the debugger, opens the web app,
and stops the full group when the debugging session ends. SQL Server and RabbitMQ must
already be running at the addresses listed above.

Open the URL printed by `Banking.Web`. The login page lets you select a bank and click the Maker, Approver, Compliance, or Operations persona; the shared lab password `fedwire-lab` is prefilled. Create a payment as the maker, click **Switch user**, and release it as the approver. Choose the receiving bank at login to inspect its received payments. Start the web project first on a fresh database because it creates and upgrades the schema.

## Guided learning

After signing in, open **Learn** to use the guided curriculum. The Learning Center includes role- and topic-based paths across wires, ACH, checks, accounting, non-value messages, and reliability. Durable mission runs capture before-state balances, attach to bank-scoped simulator records, and verify transport, business acceptance, settlement, and accounting independently. The evidence workbench provides presentation-only event replay, expected-versus-observed balances, and journal reconstruction; the ISO explorer provides message comparison and a browser-only malformed-XML sandbox. Controlled fault presets, live diagnostic cases, rail-selection cases, confidence-rated assessments, cohort attribution, assignments, and evidence exports support individual or instructor-led use. Progress remains bank-scoped so maker-to-approver role changes preserve coordinated exercises. Reset removes only learning records and preserves payment data.

The expanded operations curriculum also includes a durable, manually advanced lab clock;
ledger/collected/available balance views; pending and provisional check-credit memo state;
bank liquidity and ACH exposure controls; deterministic duplicate, delayed, and out-of-order
delivery scenarios; a cross-rail trial balance; and an operational case queue with ownership,
notes, due times, and resolution. Mission runs explicitly attach to the item created by that
browser session, and knowledge checks require a short evidence-based explanation scored by a
visible concept rubric. These remain teaching approximations, not operating-rule compliance,
production accounting, sanctions screening, or resilience certification.

Configuration can be overridden with standard .NET environment variables, for example `ConnectionStrings__DefaultConnection`, `RabbitMq__HostName`, `RabbitMq__UserName`, and `RabbitMq__Password`.

Version-controlled table definitions and the location for future migration scripts are documented in [`database/README.md`](database/README.md).

Run `bash tests/run-external-integration.sh` to start disposable SQL Server and RabbitMQ
containers and exercise the durable inbox, broker delivery, settlement posting, and balanced
journal path. The containers and integration database are removed when the test exits.

## Projects

- `Banking.Web`: bank-scoped, role-based MVC inquiry, dual-control wire entry, timelines, ISO history, durable outbox publication, and the cross-rail Operations dashboard.
- Settled outgoing wires support return requests, while network-submitted outgoing wires support investigations. The wire detail workflow persists each case, generates rail-appropriate ISO 20022 requests and responses, and records accepted `pacs.004` returns with balanced customer and settlement journals.
- The wires feature generates, validates, correlates, and persists `pacs.008`, `pacs.009`, `pacs.004`, `pain.013`, `pain.014`, `camt.110`, `pacs.028`, `camt.056`, `camt.029`, `admi.007`, `pacs.002`, `admi.002`, `camt.052`, `camt.060`, `admi.004`, and `admi.011` messages through payment, servicing, or non-value workflows.
- Drawdown requests, account reporting, and participant broadcasts use standalone persisted conversations rather than an unrelated wire: `pain.013` is acknowledged and held for the debtor bank's explicit decision; approval or rejection returns `pain.014`, and approval creates the resulting `pacs.008` payment. `camt.060` returns `camt.052`, and `admi.004` returns `admi.011`.
- `Banking.WireService`: customer-funds or master-account-liquidity validation plus `pacs.008` and `pacs.009` generation.
- `Banking.AchService`: ACH validation, open-batch grouping, cutoff, trace assignment, and NACHA file generation.
- `Banking.CheckService`: MICR/image validation and simplified X9.37-style image cash letter generation.
- `Banking.CheckImageExchangeSimulator`: paying-bank routing, duplicate detection, check settlement, and returns.
- `Banking.MessageManager`: outbound delivery tracking plus inbound status/payment routing.
- `Banking.FedwireSimulator`: idempotent master-account settlement, IMAD/OMAD assignment, `pacs.002`, and forwarding of the original `pacs.008` or `pacs.009`.
- `Banking.FedNowSimulator`: independent instant-payment processing, participation/availability and beneficiary checks, receiver confirmation, idempotent settlement, FedNow `pacs.002` statuses, and delivery of settled `pacs.008` and `pacs.009` transfers.
- `Banking.SwiftSimulator`: learning-only FINplus transport and serial-correspondent processing, CBPR+ validation, per-leg payment statuses, and delivery of the original `pacs.008`.
- `Banking.FedAchSimulator`: NACHA file validation plus simulated settlement, returns, and notifications of change.
- `Banking.Domain` and `Banking.Infrastructure`: contracts, ISO translation, EF Core, and messaging.

## Check Processing scope

The check rail is a learning-only Image Cash Letter simulator. It models check
capture, MICR parsing, front/back TIFF image storage, simplified X9.37/X9.100-187-style
file creation, Check 21 concepts, paying-bank presentment, settlement, duplicate
presentment scenarios, and returns. Checks use dedicated `CHECK.*` queues and are
not represented as ACH entries.

This is not production check processing or bank certification. It does not
implement the licensed ANSI X9.100-187 specification, full X9.100-181 TIFF image
quality rules, full X9.100-140 substitute-check requirements, ECCHO rules,
Federal Reserve image cash letter certification, production endorsement rules,
fraud detection, maker signature verification, deposit holds, Reg CC availability
logic, or legal substitute-check warranty workflows.

To exercise the standard flow, use Bankers Bank, its `123456` depositing account,
MICR line `t103000648t 654321o 1001`, and small front/back TIFF files. Switch to
First Oklahoma Bank afterward to inspect the paying-bank view. In the application,
open **How to create → Checks** for the complete walkthrough; the Checks page also
includes a generator for downloadable synthetic front/back TIFF fixtures.

`FED.OUTBOUND`/`FED.INBOUND`, `FEDNOW.OUTBOUND`/`FEDNOW.INBOUND`, and `SWIFT.OUTBOUND`/`SWIFT.INBOUND` are transport-abstraction queues carried by RabbitMQ in the default laptop profile. An optional IBM MQ container is included (`docker compose --profile ibmmq up -d`), but the application does not claim to use IBM MQ until an IBM XMS transport implementation is supplied. The `IMessageBus` boundary is the replacement point.

## ACH/NACHA scope

The ACH rail is a learning-only FedACH-style simulator. It models ODFI/RDFI roles,
ABA routing-number validation, NACHA-style 94-character files, batches, entry
details, addenda records, settlement, returns, and notifications of change.

Supported examples include PPD credits/debits, CCD credits/debits, and a narrow
EFTPS-style CCD+ tax-payment addenda flow. ACH uses its own file contracts and
`ACH.*` queues; it is not transported as an ISO 20022 or `FedEnvelope` message.
Entry capture uses a teaching maker/approver boundary before ODFI processing. Entry validation enforces supported SEC/purpose/direction combinations, ABA prefix
and check-digit rules, cent precision, and Federal Reserve banking-day effective
dates. ODFI rejections remain distinct from operator file rejection and RDFI return
files; simulated RDFI returns reverse the original settlement with balanced journals.

This is not production ACH certification. It does not implement the full Nacha
Operating Rules, FedLine connectivity, full prefunding/risk controls or production exposure management,
authoritative Same Day ACH processing windows, OFAC compliance, account validation services,
or Federal Reserve/Treasury certification.

## SWIFT CBPR+ scope

The included profile is a deliberately narrow USD customer-credit-transfer teaching subset. It generates `pacs.008.001.08`, models the serial method, validates IBANs using ISO 13616 mod-97, routes institutions by BIC, and requires town and country in structured addresses. Seeded German and UK institutions let operators send cross-border wires in either direction and inspect the receiving-bank view.

SWIFT is a messaging network, not the settlement system. The simulator therefore labels balance movement as simulated correspondent positions. It is not production CBPR+ conformance: the private MyStandards usage guidelines, FINplus connectivity, PKI/signing, sanctions controls, correspondent account configuration, currency/FX handling, cover payments, and Vendor Readiness testing are not implemented. See Swift's [CBPR+ readiness requirements](https://www.swift.com/cbpr-self-attestation) and [structured-address timeline](https://www.swift.com/standards/iso-20022/removal-unstructured-address).

For USD SWIFT wires, active `CorrespondentRelationships` are resolved direct-first and then
through at most one intermediary. The selected `PaymentRoute` and its ordered steps are retained
with the payment. Message Manager advances a single original `pacs.008` and UETR across those
steps, recording a separate delivery message ID and status for each leg. The seeded Bankers Bank
route to Euro Demo Bank is `Bankers Bank → Big New York Correspondent Bank → Euro Demo Bank`.

## FedNow message scope

The FedNow profile covers the public message set described by the Federal Reserve: value messages (`pacs.008`, `pacs.004`, `pacs.009`), status and non-value exchanges (`pacs.002`, `pacs.028`, `camt.026`, `camt.028`, `camt.029`, `camt.055`, `camt.056`, `pain.013`, `pain.014`), reports (`camt.052`, `camt.054`, `camt.060`), and system messages (`admi.002`, `admi.004`, `admi.006`, `admi.007`, `admi.011`, `admi.998`). `FedNowProfile` records categories, acknowledgement requirements, and expected response types. The end-to-end `pacs.009.001.08` flow moves the active bank's own master-account liquidity over Fedwire or FedNow; the customer-transfer $10 million limit does not apply. SWIFT CBPR+ `pacs.009` and cover payments remain outside this lab profile.

This remains a learning simulator, not a production FedNow connection. Public Federal Reserve documentation requires the private MyStandards usage guidelines, Technical Specifications, participant onboarding, endpoint connectivity, message signing/key management, size controls, and certification testing. Those external controls are deliberately not represented as completed production capabilities here.

The web application applies the included idempotent lab schema upgrade at startup so existing local databases gain held balances, beneficiary fields, scenarios, journal entries, BICs, structured locations, and SWIFT capability flags. Equivalent forward SQL migrations are in `database/migrations`. Use managed EF migrations before evolving this schema beyond the lab.

## Verification

```bash
dotnet test Banking.slnx
```

The domain tests verify generated headers and UETRs, catalog coverage, generic construction of every supported wire-message type, Fed and CBPR+ statuses, semantic profile validation, and malformed XML handling. Generic construction validates the ISO envelope, message identity, and header consistency; authoritative scheme XSD and MyStandards validation remain external requirements.

## Live wire traffic studio

Sign in as **Operations** (or **All permissions**) and open **Wire studio** at
`/Simulator`. Choose a pace and start traffic for the active bank. The new
`Banking.WireTrafficSimulator` service is discovered by `python3 run_all.py`.
It generates domestic Fedwire customer wires between open US-bank accounts,
then releases them on a later tick with a distinct simulated approver identity.
This is an explicit lab automation policy; it cannot approve human-created wires
or bypass compliance review. Customer amounts are capped at $250 and adapt to
available funds. Five outstanding wires per originating bank bound the backlog.
The usual WireService, MessageManager, FedwireSimulator, and web outbox publisher
must be running. Accepted wires generate the destination bank's incoming record
through the existing settlement and accounting path.

The page refreshes every 1.5 seconds, highlights changed balances, and shows
expandable payment timelines with links to ISO messages and accounting details.
Cash, customers, and wire records remain scoped to the active bank; switch bank
from the header to follow the receiving side. The latest 40 generated incoming
and outgoing wire records are shown. Generation starts paused and continues
while the page is closed until paused explicitly. Pause stops new generation
and simulated approvals; already released payments finish normally. No history,
payments, or journals are deleted.

Traffic schedules reuse the existing `SimulationStates` store with names
`wire-traffic:{bankId:N}`. For those names only, `CurrentUtc` means next generation
time and `SpeedMultiplier` means wires per minute (1–12); `UpdatedDate` is the
worker heartbeat. The `default` learning clock remains independent. Schedules,
wire creation, approval, and outbox insertion are transactional, with serializable
coordination across generator instances. Failed ticks retry on the next pass;
committed approvals enqueue once and the existing inbox/outbox handles delivery
retries. This adds no schema migration and never connects to real wire rails.

## ACH city studio and expanded lab customers

Open `/AchSimulator` as **Operations** or **All permissions**, or choose **ACH studio**
in the navigation. The new `Banking.AchTrafficSimulator` service starts automatically
with `python3 run_all.py` and is included in the VS Code all-services configuration.
Enable an activity for the active bank:

- **Payroll:** PPD checking credits; random paychecks from $650–$2,400.
- **Supplier payments:** CCD checking credits; random invoices from $125–$1,800.
- **Utility collections:** WEB checking debits; random bills from $35–$220 using
  explicitly fictional teaching authorizations.
- **City mix:** randomly chooses one of those activities for each run.

Each run has up to four entries with a common run identifier in addenda 05. Amounts
have random cents and are capped by available funds. Dedicated seeded company
accounts originate activity; recipient accounts are open fictional customer accounts
at other domestic banks. Generation and approval use distinct simulator identities.
Human-created ACH entries cannot be approved by this generator. No new money moves
until the usual ACH workers accept, settle, and post the generated NACHA files.

The studio shows bank cash, customer balances and holds, expandable runs, per-entry
status, trace numbers, NACHA file status, and posting evidence. Originating banks can
open the NACHA explorer; both authorized sides can open their entry lifecycle.
The latest 80 visible generated entries are displayed, with refreshes every 1.5 seconds.
All balances and records stay scoped to the active bank. Changes highlight while
expanded cards and keyboard focus remain preserved during refreshes.

ACH uses the existing **shared lab clock**, not wall-clock settlement dates.
Weekend and holiday runs receive the next banking day's effective date and wait
until the shared clock reaches that date. The studio displays this explicitly and
links to Operations clock controls. Starting a generator never advances the shared
clock. The normal lab file cutoff is five seconds for eligible batches; this is a
teaching approximation rather than a production ACH schedule.

The ACH schedule uses `SimulationStates` names `ach-traffic:{bankId:N}:{mode}`;
only one mode per bank runs at a time. As with wire schedules, `CurrentUtc` means
next generation time, `SpeedMultiplier` means runs per minute (1–4), and
`UpdatedDate` is the heartbeat. A bank is limited to 24 outstanding generated entries.
Pause stops generation and simulator approvals; released entries and future-dated
batches remain durable and finish when eligible. The web outbox, AchService,
MessageManager, and FedAchSimulator must also run. New entries, separate approvals,
and outbox publication are coordinated transactionally; failed ticks retry with
fresh contexts and existing inbox/outbox rules handle transport redelivery.

Web startup now adds **44 fictional customers and 76 accounts** across the four
original domestic banks: three businesses and eight people per bank, with checking
and savings for each person. Opening balances are inserted only for missing accounts.
Rerunning startup or SQL seeds preserves existing balances, holds, payments, and
journals. These are customer profiles; the existing bank/persona sign-in stays the
same. The repeatable SQL dataset is `database/seeds/002_traffic_population.sql`.

Wire traffic now also samples random whole-cent amounts from $1 to its existing
$250 / available-funds cap. The wire studio and ACH studio share live balance
highlighting and refresh behavior.

To exercise the full generated ACH workflow against SQL Server and RabbitMQ:

```bash
BANKING_RUN_EXTERNAL_INTEGRATION=1 dotnet test Banking.slnx --filter FullyQualifiedName~AchTrafficIntegrationTests
```

This test uses a uniquely named temporary database and prefixed RabbitMQ queues,
checks all three activities through the actual workers, validates NACHA payloads,
verifies balanced journals and conserved balances, and redelivers final results to
verify no duplicate posting. It cleans up only its own test database and queues.

### Inspecting an ACH file before its cutoff

ACH batches with future effective dates have not been submitted yet. The studio,
batch list, and originating bank's entry details now offer **Inspect draft NACHA
file** and **Download draft .ach** after ODFI batching. The draft uses a separate
in-memory copy with illustrative trace numbers and file header values. Viewing,
validating, or downloading it never assigns durable traces, creates a saved file,
changes a payment status, or sends the batch. The actual file may combine batches
and receive different trace numbers when the eligible cutoff runs.

After a saved file exists, the same batch inspection links resolve to its exact
stored payload. The annotated explorer explains field positions in the file
header, batch header, entry details, addenda, batch/file controls, and padding.
Downloads contain the original draft/saved payload rather than edited sandbox
text. Full file access remains scoped to the originating bank; receiving banks
can switch lab context to inspect that originator's file. The Files page retains
saved payloads and now also offers direct `.ach` downloads.
