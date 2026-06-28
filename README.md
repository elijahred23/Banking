# Fed Payments Lab

A .NET 10 learning system that models two-bank Fedwire and FedNow payments. It uses SQL Server on `localhost:11433` for durable state and RabbitMQ on `localhost:5672` for application events.

The lab emphasizes observable payment behavior rather than a single happy path:

- explicit beneficiary account and routing information;
- `head.001` business headers, UETRs, and semantic ISO profile validation;
- a version-aware catalog and generic header/envelope construction for the supported `admi`, `pacs`, `pain`, and `camt` wire-message families;
- concrete `pacs.008` instructions and `pacs.002` `PDNG`, `ACSC`, and `RJCT` statuses;
- a separate FedNow rail with `FEDNOW.OUTBOUND`/`FEDNOW.INBOUND` queues, participant capability and availability checks, receiver `ACCP`/`ACWP` processing states, immediate final settlement, duplicate-safe processing, and a $10 million customer-credit-transfer limit;
- customer funds holds that become posted debits only after final settlement;
- balanced debit/credit journals for outgoing and incoming posting; and
- selectable pending, Fed-rejection, and malformed-message learning scenarios.

## Run

Prerequisites: .NET 10, SQL Server on `localhost:11433`, and RabbitMQ on `localhost:5672`. The configured RabbitMQ credentials are `taskwrapper` / `taskwrapper123`; SQL Server uses the `BankingDb` database.

```bash
python3 run_all.py
```

The script builds the solution once, starts the web app before the worker services,
and stops every project when you press Ctrl+C. Run `python3 run_all.py --help` for
configuration and build options.

Open the URL printed by `Banking.Web` and sign in with `operator` / `fedwire-lab`. Start the web project first on a fresh database; it creates the schema and seed data. Create a payment from John Smith at Bankers Bank to Mary Jones at First Oklahoma Bank, beneficiary account `654321`, and select either rail. Use the scenario selector to exercise pending, rejection, and validation-failure paths.

Configuration can be overridden with standard .NET environment variables, for example `ConnectionStrings__DefaultConnection`, `RabbitMq__HostName`, `RabbitMq__UserName`, and `RabbitMq__Password`.

Version-controlled table definitions and the location for future migration scripts are documented in [`database/README.md`](database/README.md).

## Projects

- `Banking.Web`: authenticated MVC inquiry, persona switching, wire entry, timelines, and ISO history.
- `Banking.WireService`: funds/OFAC/sanctions simulation and `pacs.008` generation.
- `Banking.MessageManager`: outbound delivery tracking plus inbound status/payment routing.
- `Banking.FedwireSimulator`: idempotent master-account settlement, IMAD/OMAD assignment, `pacs.002`, and forwarding of the original `pacs.008`.
- `Banking.FedNowSimulator`: independent instant-payment processing, participation/availability and beneficiary checks, receiver confirmation, idempotent settlement, FedNow `pacs.002` statuses, and delivery of the settled `pacs.008`.
- `Banking.Domain` and `Banking.Infrastructure`: contracts, ISO translation, EF Core, and messaging.

`FED.OUTBOUND`/`FED.INBOUND` and `FEDNOW.OUTBOUND`/`FEDNOW.INBOUND` are transport-abstraction queues carried by RabbitMQ in the default laptop profile. An optional IBM MQ container is included (`docker compose --profile ibmmq up -d`), but the application does not claim to use IBM MQ until an IBM XMS transport implementation is supplied. The `IMessageBus` boundary is the replacement point.

## FedNow message scope

The FedNow profile covers the public message set described by the Federal Reserve: value messages (`pacs.008`, `pacs.004`, `pacs.009`), status and non-value exchanges (`pacs.002`, `pacs.028`, `camt.026`, `camt.028`, `camt.029`, `camt.055`, `camt.056`, `pain.013`, `pain.014`), reports (`camt.052`, `camt.054`, `camt.060`), and system messages (`admi.002`, `admi.004`, `admi.006`, `admi.007`, `admi.011`, `admi.998`). `FedNowProfile` records categories, acknowledgement requirements, and expected response types; the generic ISO service constructs and identifies each message.

This remains a learning simulator, not a production FedNow connection. Public Federal Reserve documentation requires the private MyStandards usage guidelines, Technical Specifications, participant onboarding, endpoint connectivity, message signing/key management, size controls, and certification testing. Those external controls are deliberately not represented as completed production capabilities here.

The web application applies the included idempotent lab schema upgrade at startup so existing local databases gain held balances, beneficiary fields, scenarios, and journal entries. The equivalent forward SQL migration is in `database/migrations`. Use managed EF migrations before evolving this schema beyond the lab.

## Verification

```bash
dotnet test Banking.slnx
```

The domain tests verify generated headers and UETRs, catalog coverage, generic construction of every supported wire-message type, supported Fed statuses, semantic profile validation, and malformed XML handling. Generic construction validates the ISO envelope, message identity, and header consistency; scheme-specific business-rule or XSD validation must be added with each concrete workflow.
