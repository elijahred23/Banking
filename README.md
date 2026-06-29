# Bank Payments Lab

A .NET 10 learning system that models Fedwire, FedNow, SWIFT CBPR+, batch-oriented ACH customer payments, and image-based check processing. It uses SQL Server on `localhost:11433` for durable state and RabbitMQ on `localhost:5672` for application events.

The lab emphasizes observable payment behavior rather than a single happy path:

- explicit beneficiary account and routing information;
- `head.001` business headers, UETRs, and semantic ISO profile validation;
- a version-aware catalog and generic header/envelope construction for the supported `admi`, `pacs`, `pain`, and `camt` wire-message families;
- concrete `pacs.008` instructions and `pacs.002` `PDNG`, `ACSC`, and `RJCT` statuses;
- a separate FedNow rail with `FEDNOW.OUTBOUND`/`FEDNOW.INBOUND` queues, participant capability and availability checks, receiver `ACCP`/`ACWP` processing states, immediate final settlement, duplicate-safe processing, and a $10 million customer-credit-transfer limit;
- international USD wires over a SWIFT CBPR+ serial-method learning flow with dedicated queues, IBAN and BICFI routing, structured cross-border party addresses, `pacs.008.001.08`, `INDA`, `SHAR`, and `ACSP`/`ACCC`/`RJCT` statuses;
- customer funds holds that become posted debits only after final settlement;
- balanced debit/credit journals for outgoing and incoming posting; and
- selectable pending, network-rejection, and malformed-message learning scenarios.
- ACH entry validation, scheduled batch cutoff, fixed-width NACHA files, FedACH-style settlement, returns, notifications of change, and EFTPS-style CCD+ addenda.
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

Open the URL printed by `Banking.Web` and sign in with `operator` / `fedwire-lab`. Start the web project first on a fresh database; it creates the schema and seed data. For an international wire, send from John Smith at Bankers Bank to Anna Müller at Euro Demo Bank using IBAN `DE89370400440532013000` and the SWIFT international wire rail. Switch the active-bank persona to Euro Demo Bank to see the received wire. Use the scenario selector to exercise pending, rejection, and validation-failure paths.

Configuration can be overridden with standard .NET environment variables, for example `ConnectionStrings__DefaultConnection`, `RabbitMq__HostName`, `RabbitMq__UserName`, and `RabbitMq__Password`.

Version-controlled table definitions and the location for future migration scripts are documented in [`database/README.md`](database/README.md).

## Projects

- `Banking.Web`: authenticated MVC inquiry, persona switching, wire entry, timelines, ISO history, and the cross-rail Operations dashboard.
- `Banking.WireService`: funds/OFAC/sanctions simulation and `pacs.008` generation.
- `Banking.AchService`: ACH validation, open-batch grouping, cutoff, trace assignment, and NACHA file generation.
- `Banking.CheckService`: MICR/image validation and simplified X9.37-style image cash letter generation.
- `Banking.CheckImageExchangeSimulator`: paying-bank routing, duplicate detection, check settlement, and returns.
- `Banking.MessageManager`: outbound delivery tracking plus inbound status/payment routing.
- `Banking.FedwireSimulator`: idempotent master-account settlement, IMAD/OMAD assignment, `pacs.002`, and forwarding of the original `pacs.008`.
- `Banking.FedNowSimulator`: independent instant-payment processing, participation/availability and beneficiary checks, receiver confirmation, idempotent settlement, FedNow `pacs.002` statuses, and delivery of the settled `pacs.008`.
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

This is not production ACH certification. It does not implement the full Nacha
Operating Rules, FedLine connectivity, prefunding/risk controls, exposure limits,
full Same Day ACH processing windows, OFAC compliance, account validation services,
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

The FedNow profile covers the public message set described by the Federal Reserve: value messages (`pacs.008`, `pacs.004`, `pacs.009`), status and non-value exchanges (`pacs.002`, `pacs.028`, `camt.026`, `camt.028`, `camt.029`, `camt.055`, `camt.056`, `pain.013`, `pain.014`), reports (`camt.052`, `camt.054`, `camt.060`), and system messages (`admi.002`, `admi.004`, `admi.006`, `admi.007`, `admi.011`, `admi.998`). `FedNowProfile` records categories, acknowledgement requirements, and expected response types; the generic ISO service constructs and identifies each message.

This remains a learning simulator, not a production FedNow connection. Public Federal Reserve documentation requires the private MyStandards usage guidelines, Technical Specifications, participant onboarding, endpoint connectivity, message signing/key management, size controls, and certification testing. Those external controls are deliberately not represented as completed production capabilities here.

The web application applies the included idempotent lab schema upgrade at startup so existing local databases gain held balances, beneficiary fields, scenarios, journal entries, BICs, structured locations, and SWIFT capability flags. Equivalent forward SQL migrations are in `database/migrations`. Use managed EF migrations before evolving this schema beyond the lab.

## Verification

```bash
dotnet test Banking.slnx
```

The domain tests verify generated headers and UETRs, catalog coverage, generic construction of every supported wire-message type, Fed and CBPR+ statuses, semantic profile validation, and malformed XML handling. Generic construction validates the ISO envelope, message identity, and header consistency; authoritative scheme XSD and MyStandards validation remain external requirements.
