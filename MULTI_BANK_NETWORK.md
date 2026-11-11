# Multi-bank Fedwire lab

This is a learning simulator with shared application code, SQL Server ownership boundaries, and shared RabbitMQ transport. It is not a production bank identity system or rail certification.

## Start and operate

Run the existing Web, WireService, MessageManager, and FedwireSimulator hosts with local SQL Server and RabbitMQ. Web performs the idempotent schema upgrade and seeds five additional synthetic participants without replacing existing banks, customers, or balances:

| Participant | Synthetic routing | Initial master balance |
| --- | --- | ---: |
| Central Oklahoma Bank | 990000001 | $5,000,000 |
| First Prairie Bank | 990000002 | $3,000,000 |
| Red River Network Bank | 990000003 | $2,500,000 |
| Heartland Trust Bank | 990000004 | $7,000,000 |
| Community Savings Bank | 990000005 | $1,500,000 |

These routing identifiers are placeholders. Each bank starts with a synthetic customer account numbered `<routing>01` and $100,000. Existing balances are never reset by seeding. The Red River network participant is named separately to preserve the repository's pre-existing Red River Bank.

1. Sign in as Central Oklahoma's Maker. Create a $25,000 Fedwire customer payment to First Prairie, using the beneficiary instructions obtained at that bank (the seeded account is `99000000201`).
2. Switch to a different Approver persona at Central Oklahoma and approve it. Self-approval is refused, including the all-permissions persona.
3. Observe network settlement and the outgoing customer debit after final acceptance.
4. Switch to First Prairie's Operations persona. Open **Wires → Incoming → Details**. A received customer payment remains **Settled · awaiting posting**; receipt does not credit the deposit account.
5. Inspect the original ISO message and settlement references, then select **Post beneficiary credit**. Posting checks account ownership and open status, and atomically saves the credit, balanced journal, audit event, balance receipt, and notification outbox record. Repeated/concurrent posting does not credit again.
6. Open **Reconcile bank with simulated network** at each bank. The report compares accepted network payments involving that bank with its own delivery, posting, and journal evidence. Pending delivery, pending posting, missing journals, multiple journals, unbalanced journals, and amount discrepancies are visible.
7. Use the separate **Network operator** login to open `/Network` and provision additional synthetic participants. Bank Operations and All permissions personas do not receive the NetworkOperator role.

Unknown or non-open beneficiary accounts stay received and uncredited. Posting attempts persist a beneficiary exception and reviewer in the wire timeline. The original network settlement is not reversed. The existing return workflow remains separate.

## Ownership and recovery

Bank-owned views resolve the signed-in bank claim. Customer directories and balances from other banks are not offered as wire, ACH, or check form choices. Participant routing is public within the lab; beneficiary/debtor instructions must be supplied separately. Existing instructions already owned by a bank may be reused.

The existing incoming WireTransfer is the durable receiving work item; its bank, correlation, timestamp, status, ISO messages, and audit events avoid a second competing payment record. A filtered unique index enforces one incoming work item per receiving bank and settlement correlation. A second unique index protects participant identity. Schema updates are synchronized across EF, startup, baseline table SQL, and forward migrations. If an existing database contains conflicting historical rows, investigate them before applying these indexes; no financial records are deleted by the upgrade.

Settlement saves final status and accepted delivery envelopes into the existing OutboxMessages table in the same serializable transaction as master balances and FedSettlement. **Keep Web's outbox publisher running** to recover unpublished envelopes after a simulator crash. Immediate simulator responses and outbox replay can both arrive. Incoming uniqueness, final-status guards, and posting concurrency controls tolerate those duplicates. Existing outbox attempts/errors and consumer inbox/dead-letter records provide transport recovery evidence; RabbitMQ is not the source of payment truth.

The current lab intentionally retains its shared password and switchable bank/persona identities. This is simulated independent operation, not independently administered credentials or database row-level security. Membership suspension, persistent statement snapshots, and a dedicated receiver availability switch are not added; reconciliation reports are calculated from durable records. The SQL network test exercises a delayed receiver by withholding delivery and recovering from outbox envelopes, using an injected transport rather than a live RabbitMQ outage.

## Verification

```sh
dotnet build Banking.slnx --no-restore
dotnet test Banking.slnx --no-build --no-restore
git diff --check
BANKING_RUN_EXTERNAL_INTEGRATION=1 dotnet test tests/Banking.Domain.Tests --no-build --no-restore --filter 'FullyQualifiedName~MultiBankNetworkIntegrationTests|FullyQualifiedName~FedwireSimulatorTests.Sql_replay'
```

The SQL tests create and delete isolated test databases on the configured local lab SQL Server. They cover five participants, concurrent duplicate settlement attempts, delayed durable delivery, duplicate receipt, bank ownership denial, concurrent beneficiary posting, independent balanced journals, insufficient master liquidity, rejection hold release, and conservation of master balances. Default test runs do not execute external scenarios unless the environment variable is enabled.
