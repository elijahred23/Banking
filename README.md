# Fedwire Lab

A .NET 10 learning system that models a two-bank Fedwire payment. It uses the existing SQL Server on `localhost:11433` for durable state and the existing RabbitMQ instance on `localhost:5672` for application events.

The lab emphasizes observable payment behavior rather than a single happy path:

- explicit beneficiary account and routing information;
- `head.001` business headers, UETRs, and semantic ISO profile validation;
- `pacs.008` instructions and `pacs.002` `PDNG`, `ACSC`, and `RJCT` statuses;
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

Open the URL printed by `Banking.Web` and sign in with `operator` / `fedwire-lab`. Start the web project first on a fresh database; it creates the schema and seed data. To exercise the sample, create a wire from John Smith at Bankers Bank to Mary Jones at First Oklahoma Bank, beneficiary account `654321`. Use the scenario selector to exercise pending, rejection, and validation-failure paths.

Configuration can be overridden with standard .NET environment variables, for example `ConnectionStrings__DefaultConnection`, `RabbitMq__HostName`, `RabbitMq__UserName`, and `RabbitMq__Password`.

Version-controlled table definitions and the location for future migration scripts are documented in [`database/README.md`](database/README.md).

## Projects

- `Banking.Web`: authenticated MVC inquiry, persona switching, wire entry, timelines, and ISO history.
- `Banking.WireService`: funds/OFAC/sanctions simulation and `pacs.008` generation.
- `Banking.MessageManager`: outbound delivery tracking plus inbound status/payment routing.
- `Banking.FedwireSimulator`: idempotent master-account settlement, IMAD/OMAD assignment, `pacs.002`, and forwarding of the original `pacs.008`.
- `Banking.Domain` and `Banking.Infrastructure`: contracts, ISO translation, EF Core, and messaging.

`FED.OUTBOUND` and `FED.INBOUND` are transport-abstraction queues carried by RabbitMQ in the default laptop profile. An optional IBM MQ container is included (`docker compose --profile ibmmq up -d`), but the application does not claim to use IBM MQ until an IBM XMS transport implementation is supplied. The `IMessageBus` boundary is the replacement point.

The web application applies the included idempotent lab schema upgrade at startup so existing local databases gain held balances, beneficiary fields, scenarios, and journal entries. The equivalent forward SQL migration is in `database/migrations`. Use managed EF migrations before evolving this schema beyond the lab.

## Verification

```bash
dotnet test Banking.slnx
```

The domain tests verify generated headers and UETRs, supported Fed statuses, semantic profile validation, and malformed XML handling.
