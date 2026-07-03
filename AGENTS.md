# Bank Payments Lab contributor guidance

## Scope and intent

This repository is a learning simulator, not a production payment system or a claim of rail certification. Preserve that distinction in code, UI text, and documentation. New behavior should make payment state, messages, settlement, and accounting observable rather than hiding them behind a single happy path.

## Architecture

- `Banking.Domain` owns payment models, message contracts, rail rules, ISO construction, and workflow policies. Keep it independent of MVC, EF Core, RabbitMQ, and service-host concerns.
- `Banking.Infrastructure` owns persistence, messaging adapters, routing implementations, and check file/image infrastructure.
- `Banking.Web` owns authentication, bank/persona scoping, MVC views, learning workflows, durable outbox creation, and inquiry/operations UI.
- Worker and simulator projects should remain narrow queue consumers. Put shared rules and contracts in `Banking.Domain`; do not copy business logic between workers.
- SQL Server is durable state. RabbitMQ queues are transport boundaries, not the source of payment truth.

## Payment invariants

- Keep bank scoping on every user-visible query and mutation.
- Preserve maker/approver separation, role authorization, and explicit compliance/operations permissions.
- Treat transport delivery, business acceptance, and settlement as distinct states.
- Do not post customer or settlement accounting before final acceptance. Rejections must release applicable holds.
- Every posted journal must balance. Keep debit and credit entries auditable and correlated to the originating item.
- Preserve idempotency for queue consumers, settlement, returns, and retries.
- Keep rail semantics distinct: ACH is NACHA/file based; checks use image cash letters; wire rails use their supported ISO profiles.

## Change discipline

- Preserve unrelated working-tree changes. Do not rewrite or remove user data to simplify a feature.
- Prefer extending existing domain services and view models over embedding business rules in Razor or worker `Program.cs` files.
- When adding a queue or event, update both publisher and consumer contracts and make retry/idempotency behavior explicit.
- When adding durable data, follow `database/AGENTS.md` and keep the EF model, startup upgrade, baseline SQL, and forward migration synchronized.
- Do not edit generated `bin/`, `obj/`, or vendored `wwwroot/lib/` content.

## Verification

Run from the repository root:

```bash
dotnet build Banking.slnx --no-restore
dotnet test Banking.slnx --no-build --no-restore
git diff --check
```

For schema or startup changes, also start `Banking.Web` against the configured local SQL Server and confirm the idempotent upgrade succeeds. For asynchronous workflow changes, exercise the relevant worker/simulator path or add focused domain tests.

