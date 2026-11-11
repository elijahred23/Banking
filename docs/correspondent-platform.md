# Correspondent banking platform

This is a USD learning simulator. Accounts, agreements, wire approvals, reservations,
network settlement, issuer journals, respondent mirror postings, and statements are
separate durable records. There is no live account connectivity or rail certification.

## Account and accounting controls

Open accounts in **Statements** and configure bilateral service agreements in
**Correspondent banking**. Each account belongs to an issuer and a respondent.
Opening balances represent the lab's opening positions; subsequent movement uses
immutable, balanced journals. A correction must be a new posting.

The correspondent dashboard reconstructs the issuer position as opening balance
plus account-line credits minus debits. The respondent reconstruction uses opening
balance plus debits minus credits. Available liquidity subtracts pending outgoing
book orders and unposted outgoing wire links. These reservations remain outstanding
until the posting worker processes final settlement or rejection, even if the wire
status has already changed. Differences against stored balances or reservations are
shown for investigation; the dashboard does not repair financial data.

Every EF save validates newly added correspondent journals for balance, one-sided
entries, four-decimal precision, a single account line, and consistent ownership,
posting, correlation, wire, and booking references. Existing journal immutability
and posting uniqueness protections remain in force.

Issuer/respondent mirror differences are displayed separately because delivery is
asynchronous. A mirror difference alone does not prove an accounting error.
Correspondent control findings also flow into the existing payment reconciliation
service and scheduled settlement-reconciliation operations cases.

## Routing and approval

The statement account's transfer request resolves a receiving affiliate at the same
issuer to an internal book order. It reserves funds and requires issuer approval by
an operator other than the maker. Both accounts require active bilateral agreements
and sufficient transfer limits. Approval posts the existing book journals and mirror
outbox notices; no Fedwire instruction or master-account movement is created.
A missing or suspended internal agreement fails the request rather than falling back
to external settlement.

Other receiving banks use the existing Fedwire institution-transfer workflow.
External account postings require both the finalized balanced cash journal and a
correlated accepted simulated Fed settlement record. A message acknowledgment alone
does not authorize the correspondent debit. Existing legacy wire accounts retain
their prior agreement behavior.

## Funding

The funding form supports fixed amounts, replenishment to target, replenishment to
minimum, and sweeps retaining a source balance. The preview uses current available
balances after holds and caps the amount by source liquidity and the rule maximum.
For example, an available destination balance of $85,000, a minimum of $100,000,
a target of $250,000, and sufficient source liquidity produces $165,000 to target
or $15,000 to minimum. Preview does not reserve or transfer funds.

Proposed funding authority still requires separate issuer and source approvals.
The scheduler rechecks agreements and current liquidity in a serializable transaction,
uses unique rule/scheduled-time runs, and posts internal funding atomically. Concurrent
executions cannot replay a completed run. Missed dates coalesce; weekends are skipped.
There is no asynchronous funding wire in this internal funding path.

## Remaining roadmap

The existing application supplies statements/camt exports, fees, wire investigations,
repair and operations workflows. These additions connect account liquidity, routing,
and accounting controls to that foundation. Further work is needed for bank-specific
holiday/cutoff calendars, external automatic funding with in-flight reservations and
approval thresholds, available-credit facilities, automatic service pricing/monthly
billing, and comprehensive orphan or out-of-order network-event reconciliation.
The current dashboard reads complete account journal history; production-scale
operation would require indexed period aggregates and pagination.

## Verification

Run the repository build and tests. To exercise real SQL transactions, automatic
internal routing, funding concurrency, wire settlement workers, statement delivery,
and reconciliation against the configured local lab services:

```bash
BANKING_RUN_EXTERNAL_INTEGRATION=1 dotnet test tests/Banking.Domain.Tests/Banking.Domain.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~StatementIntegrationTests
```

These integration tests use isolated synthetic databases, not the main lab database.
Without the environment flag, their bodies return without exercising external services.
