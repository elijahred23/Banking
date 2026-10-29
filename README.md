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

## Studio learning benches

Both `/Simulator` and `/AchSimulator` open in Simulation mode, leading with live bank activity and a workload desk. Switch to Guided learning for a live observation guide and the practice bench below the live board. The mode preference is saved per studio in this browser when storage is available; `?mode=learning` or `?mode=simulation` overrides the saved choice on navigation. Mode changes do not start/stop generation or change permissions. Workload filters summarize the current bank-scoped recent snapshot (40 wires / 80 ACH entries), preserve the selected filter on refresh, and show eight matches with an option to show all. Review includes recorded exceptions and screening holds, not necessarily unresolved cases. Pace presets prepare the existing generator controls; Start / update applies them. Choose a fictional $125 outgoing credit scenario, predict each next step, check the explanation, then advance through delivery, acceptance, settlement, and accounting. Six scenarios per studio cover normal processing, delays, rejection, duplicate delivery, and rail-specific exceptions: ACH returns/corrections or wire compliance/cancellation requests.

The bench includes sender balance and journal explanations, bank/role perspectives, clickable instruction-to-message field mappings, a rail comparison, and a simplified processing-window experiment. Links lead to the existing NACHA builder, ISO explorer, operations workflows, and evidence-based missions. Practice stays in page memory and survives live snapshot refreshes, but resets on navigation/reload. It never changes permissions, shared time, payments, or ledger state and does not award durable mission completion. Timing assumes fictional weekday windows without holidays; message excerpts and accounting describe a simplified outgoing credit, not a certified rail implementation.

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

## Simulated banking days, incidents, and operations inbox

Both studios now offer **Start a simulated banking day** for Operations users.
The morning, afternoon, and deadline phases schedule bounded generator workloads;
advance after each workload has generated, or close early to carry work forward.
Wire days generate 6 / 4 / 3 Fedwire instructions. ACH days generate two runs each
of payroll, suppliers, and utility collections using the existing fictional
businesses and accounts. Enabled payroll rosters can be selected when present;
funds, liquidity, exposure, backlogs, and account eligibility still constrain work.
The phase agenda does not move the shared clock or represent actual rail hours.

The **Incident director** queues a fault for the next eligible generated wire or
ACH run, persists an operations case, and consumes it atomically with generation.
Wire faults include delay, rejection, duplicate/out-of-order delivery, and screening
review. ACH faults include control failure, closed/missing account returns, and
account corrections. ACH faults apply to all entries in that run and may affect a
shared file. Closing a day cancels an uninjected fault; injected work remains in
the normal pipeline. Workers and the broker are not intentionally stopped.

The **Operations inbox** links actual payment observations, investigation responses,
and durable cases to evidence and existing authorized workflows. Notes and reviewed
resolutions do not change payment/compliance state. The **customer inquiry** exercise
checks a selected status category against the current payment state and saves the
explanation for human review; it never sends a customer message or awards mission
completion. Guided Learning adds prompts alongside the same live scenarios.

The **session debrief** includes all generated source instructions from that day's
start/end interval, correlated journal balance checks, outstanding work, and bank
opening/current cash, customer balances, and holds. Returned payments keep original
and reversing journals. Full-bank balance changes include activity outside the day,
and asynchronous posting gaps may be transient, so these observations are not an
automatic reconciliation certificate. Closed-day evidence keeps updating as late
outcomes arrive. Starting the next day preserves the prior day's database history;
the studios display the latest day.

## Campaign Room

Open **Campaigns**, or the Campaign Room link in either studio. Operations users can
save immutable scenario versions (phase workloads, generation pace, receiver
profiles, scheduled incidents, debit-authorization requirements, hints, and an
outstanding-work target), then create a 2–10 banking-day campaign. Each campaign
snapshots its scenario. Starting successive days preserves accounts and cases and
uses the next banking date; it never advances shared lab time. Workloads/profiles
are repeatable, while sampled amounts and customer selection remain random.

The room shows customer cash-flow stories, pre-settlement face-amount forecasts,
operational evidence targets, and role-addressed handoff notes. Maker, Approver,
Compliance, and Operations personas can view their received/sent handoffs without
being granted another role's permissions. Templates and campaigns are bank-scoped;
new versions do not rewrite running campaigns. Mixed receiver behavior is stable
per receiver bank: Slow delays wire outcomes or schedules ACH for a later banking
day; AccountProblem simulates a wire rejection or an account-closed ACH return.
Explicit queued incidents override the profile's fault outcome.

ACH campaigns can require fictional debit authorizations. Operations records a
collection account, payer routing/account, expiration, and per-entry limit; revoke
blocks future generated collections without canceling previously generated work.
These records are teaching evidence, not real mandates or full ACH authorization
compliance. Rejected operator files can be repaired entry by entry as a Maker:
the original file remains immutable, one replacement is allowed per original,
and the replacement requires a different approver, even for All-permissions users.
The normal pipeline validates, batches, and builds new controls after approval.
Returns and entries with posted accounting are excluded from this repair workflow.

Wire investigation conversations provide delayed, persisted desk replies and
follow-up requests for correlation details. They are labeled teaching messages,
not ISO exchanges or independent beneficiary-posting confirmation. Actual ISO
investigations/cancellations/returns remain in the authorized wire case workflow.
Treasury requests never mint cash: use an ordinary source-bank financial-institution
wire and independent approval, then match its incoming settlement, funding reference,
and balanced journal evidence. Matching a request never posts another journal.

Recorded session replay synchronizes recorded payment events, messages, file
creation, and journal lines. It does not invent timestamps for unrecorded states,
reprocess payments, or reconstruct unavailable historical balances. The transcript
and interactive stepping are presentation-only. Campaign target assessments use
recorded evidence and remain distinct from durable guided-mission completion.

## Unified payment desk

Operations users can open **Payment desk** (`/PaymentDesk`) for a responsive,
searchable cross-rail queue, explicit blockers and action owners, funding/exposure
planning, and reconciliation controls. The queue covers the latest 100 bank-visible
wires and 100 ACH entries; totals labeled as forecasts and reconciliation controls
are bank-wide. Receiving banks do not see unreleased originating ACH instructions.
Shared attention explanations also appear in both studios and payment details.

**Advance shared clock to next date** selects the earliest future banking date of
this bank's batched ACH entries, or the next banking day when none exists. The
server recomputes the date under a serializable transaction. This advances the
shared clock for every bank, including eligible work outside the current bank.
It does not force settlement, pause workers, or change generator pace. The existing
short ACH file cutoff, wire processing, and investigation reply delays still use
wall time. This is an eligible-date step, not a full event scheduler or an
authoritative operating-hours profile.

Reconciliation includes missing ACH/wire journals, expected-versus-observed customer
holds, and individual journal controls. Downloaded JSON captures scoped evidence;
it is not an independently imported operator statement. Review explanations are
saved with freshly computed counts in the existing Operations case queue for human
assessment. They never automatically certify a day or complete a learning mission.
Six repeatable fictional decision exercises cover unknown wire outcomes, future
payroll, ACH returns, duplicate delivery, wire cancellation after settlement, and
NOCs. They require a prediction and explanation before feedback and remain separate
from live payments. Keyword feedback is explicitly a self-review aid.

This release provides the shared observation and review interface. Full deterministic
isolated campaign reruns, authoritative processing-window scheduling, bulk NACHA
intake, interactive receiver decisions, and new correspondent fee/posting-exception
workflows remain future extensions; existing campaign and case workflows are linked
from the desk.

## Scheduled tasks and bank reports

Operations users can open **Tasks & reports** (`/Reports`) to create bank-owned
report tasks, choose every 15 minutes / hourly / daily at a UTC time, enable or pause
scheduling, or **Run now**. New tasks default to paused. Four templates capture
payment activity, reconciliation controls, liquidity/account balances, and exceptions
with open operations cases. A dedicated bank selector uses the existing lab context
switcher; every task, run, report detail, status request, and download is checked
against the active bank. Maker/approver/compliance personas do not gain report-operations
permissions through links or posted bank IDs.

The web host runs the scheduler and report worker independently. Work is durable in
SQL Server and continues when the report page closes, but the host must be running
to execute jobs. Schedules use wall-clock UTC, not the shared learning clock; daily
runs include weekends because they are observations, not rail processing. Missed
intervals coalesce into one current run after downtime instead of fabricating
historical reports. Only one run per task can be queued/running at a time. Expired
three-minute leases can reclaim the same run up to three times; report generation
has a two-minute timeout. Stale lease holders cannot overwrite a recovered or
completed run. Other generation failures remain visible for an explicit fresh Run
now; the next scheduled interval can also generate a new run. Pausing a schedule
preserves queued work and history. Editing a task never rewrites prior run settings.

The report library filters by type and UTC request-date range, with 25 runs per page.
Each report retains requester, scheduled/manual origin, status, attempts, timestamps,
and its JSON snapshot. The detail page automatically observes completion and offers
a JSON download. Activity and payment-exception reports use instruction creation
in the configured 1–744 hour rolling window and states observed at capture; they do
not reconstruct historical states at the window end. Controls, account balances,
and unresolved cases are capture-time observations. Full aggregate counts are
accompanied by bounded evidence lists (100 wires/100 ACH entries or 200 control,
account, or case rows). Account identifiers in liquidity reports are masked.
Sequential reads can observe transient changes while payment processing continues.
These learning reports neither move funds nor certify an operator statement,
regulatory report, or atomic end-of-day reconciliation.

To exercise scheduling, concurrent manual requests, lease recovery, immutable report
completion, pause/manual behavior, and idempotent startup upgrades against an isolated
temporary local SQL Server database:

```bash
BANKING_RUN_REPORT_INTEGRATION=1 dotnet test tests/Banking.Domain.Tests --filter FullyQualifiedName~BankReportIntegrationTests
```

## Operational jobs

**Tasks & reports → Add all 12 jobs for this bank** installs any missing operational
job types as paused tasks without changing existing schedules. Use **Run now** or
edit and enable a UTC schedule. Operational templates describe their effects before
release; the four original observation-only report templates remain available.
Each job saves findings and actions in its run report and links to payment evidence
or the Operations inbox.

- **ACH batch preparation:** validates up to 200 already-released entries and groups
  them using the same shared processor as AchService. Approval evidence, bank/account
  ownership, validation, customer availability, and exposure checks remain required.
- **ACH cutoff processing:** closes eligible batches only on a shared-lab banking
  date. Saves the NACHA file and `ACH.BATCH.READY` outbox message in one transaction.
  Message Manager atomically records submission with a second original outbox envelope;
  the operator simulator still determines acceptance/settlement.
  The existing ACH worker continues its configured automatic lab cutoff; running a
  cutoff job uses that same service and cannot submit the same closed batch twice.
- **Return & NOC review:** creates deduplicated cases for persisted return/NOC source
  records, including older records. Does not generate another return or rewrite
  saved instructions. Review original/reversing journals or future correction data.
- **Stuck-payment detection:** wires without a recorded event for 15 minutes and ACH
  work older than an hour since saved-file creation or approval/capture create cases.
  Future-effective ACH batches and pending approvals are excluded. ACH has no full
  timestamped state history, so its age check is a labeled teaching approximation.
- **Settlement reconciliation:** checks missing wire/ACH journals, bank-visible
  journal balance, settled saved-file evidence for final ACH states, and accepted
  correlated Fedwire/FedNow settlement evidence.
- **Hold reconciliation:** opens cases for expected-versus-observed holds; no automatic
  release or accounting correction is performed.
- **Liquidity forecasting:** compares bank cash/credit capacity with outstanding
  outgoing Fedwire and ACH credit face amounts and checks validated ACH exposure.
  Combined figures are a conservative teaching forecast, not reserved liquidity or
  an ACH entitlement to Fedwire credit capacity. Funding gaps create review cases.
- **Duplicate-payment screening:** flags similar original outgoing instructions in
  the rolling activity window. Wire correlations are deduplicated, rails/types and
  origin/receiver details stay distinct, and ACH effective dates/direction matter.
  Suspected duplicates create review cases without blocking or releasing payments.
- **Delivery recovery:** makes up to 100 eligible failed original outbox records due
  for the existing publisher. Stable IDs, queues, payloads, error evidence, and attempt
  counts are preserved; future backoff is respected. Dead letters create cases and
  retain the existing authorized human replay workflow.
- **Approval reminders:** creates deduplicated Operations cases and role-addressed
  PaymentApprover handoffs in Campaigns for instructions awaiting approval at least
  15 minutes. No approval is performed and no external message is sent.
- **Operations case escalation:** raises overdue unresolved cases below High to High
  and lists unassigned cases. Ownership, work notes, Critical severity, and human
  resolution are preserved.
- **End-of-day close review:** captures unsettled work, bank cash, funding forecast,
  holds, returns, and control findings, and creates a daily review case. It does not
  certify or automatically close a banking day.

Case identity includes bank, job type, and source reference. Repeat observations
retain existing cases, including resolved human decisions for unchanged findings; changed captured evidence, new source events, and
new stalled payment states can create new cases. End-of-day cases are deduplicated
by lab date and wall-clock UTC date. Operational effects and their evidence snapshot
commit together under the current run lease; lease recovery reuses that snapshot
instead of applying effects again. The existing task/run/case/handoff tables are
reused; new enum names fit their existing SQL lengths, so no schema migration is
needed. All job schedules still require the web host to be running.

Verification of the operational jobs against an isolated temporary SQL database:

```bash
BANKING_RUN_JOB_INTEGRATION=1 dotnet test tests/Banking.Domain.Tests --filter FullyQualifiedName~OperationalJobIntegrationTests
```

The integration test covers all twelve jobs twice, case/handoff deduplication,
unchanged bank/customer cash, hold idempotency, bank isolation, approval boundaries,
future-effective batches/backoff, saved-file/outbox atomicity, and stale lease recovery.
The existing ACH traffic integration test exercises the extracted processor through
actual workers, NACHA operator results, settlement, and balanced accounting.

## Razor build stability

`Banking.Web.csproj` disables shared compiler reuse with
`UseSharedCompilation=false`. On the installed .NET SDK 10.0.301, repeated shared
builds can emit cascading Razor errors that treat valid HTML text such as “DUAL
CONTROL” as C# types. This was reproduced with shared compilation enabled and
eliminated by compiling the same views in a fresh compiler process. The setting
applies automatically to solution builds, `run_all.py`, and the VS Code build task;
no generated-file edits or recurring compiler-server reset is required. It affects
build-time compiler reuse, not application behavior or Razor validation.
