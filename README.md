# Fedwire Lab

A .NET 10 learning system that models a two-bank Fedwire payment. It uses the existing SQL Server on `localhost:11433` for durable state and the existing RabbitMQ instance on `localhost:5672` for application events.

## Run

Prerequisites: .NET 10, SQL Server on `localhost:11433`, and RabbitMQ on `localhost:5672`. The configured RabbitMQ credentials are `taskwrapper` / `taskwrapper123`; SQL Server uses the `BankingDb` database.

```bash
python3 run_all.py
```

The script builds the solution once, starts the web app before the worker services,
and stops every project when you press Ctrl+C. Run `python3 run_all.py --help` for
configuration and build options.

Open the URL printed by `Banking.Web` and sign in with `operator` / `fedwire-lab`. Start the web project first on a fresh database; it creates the schema and seed data. To exercise the sample, create a wire from John Smith at Bankers Bank to Mary Jones at First Oklahoma Bank.

Configuration can be overridden with standard .NET environment variables, for example `ConnectionStrings__DefaultConnection`, `RabbitMq__HostName`, `RabbitMq__UserName`, and `RabbitMq__Password`.

Version-controlled table definitions and the location for future migration scripts are documented in [`database/README.md`](database/README.md).

## Projects

- `Banking.Web`: authenticated MVC inquiry, persona switching, wire entry, timelines, and ISO history.
- `Banking.WireService`: funds/OFAC/sanctions simulation and `pacs.008` generation.
- `Banking.MessageManager`: outbound delivery tracking plus inbound status/payment routing.
- `Banking.FedwireSimulator`: idempotent master-account settlement, IMAD/OMAD assignment, `pacs.002`, and forwarding of the original `pacs.008`.
- `Banking.Domain` and `Banking.Infrastructure`: contracts, ISO translation, EF Core, and messaging.

`FED.OUTBOUND` and `FED.INBOUND` are transport-abstraction queues carried by RabbitMQ in the default laptop profile. An optional IBM MQ container is included (`docker compose --profile ibmmq up -d`), but the application does not claim to use IBM MQ until an IBM XMS transport implementation is supplied. The `IMessageBus` boundary is the replacement point.

The demo uses `EnsureCreated` to keep first-run setup simple. Use EF migrations before evolving this schema beyond the lab.
