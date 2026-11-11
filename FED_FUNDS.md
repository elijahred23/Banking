# Treasury and federal funds lab

This subsystem simulates unsecured bilateral USD overnight loans between eligible US lab banks. It is not a production trading platform, an official rate calculation, or the Federal Reserve discount window. The five network participants seeded by Web can trade without additional databases or applications.

## Run the complete workflow

Start SQL Server and RabbitMQ using the existing lab configuration. Run Web first to apply the idempotent upgrade, then run the existing WireService, MessageManager, and FedwireSimulator hosts plus the new Treasury worker:

```sh
dotnet run --project src/Banking.Web
dotnet run --project src/Banking.WireService
dotnet run --project src/Banking.MessageManager
dotnet run --project src/Banking.FedwireSimulator
dotnet run --project src/Banking.FedFundsWorker
```

Each command runs in its own terminal. Web's existing outbox publisher must remain running. The worker scans durable agreements every five seconds and reads the shared simulation clock. The Operations **Process this bank's Treasury agreements** action can also run the same recovery/accrual/maturity service without a worker host.

1. Sign in as an **Approver** at First Prairie (lender). Open **Treasury & federal funds** and establish an active $1,000,000 lending limit to Central Oklahoma (borrower).
2. Sign in as Central Oklahoma's **Maker**. Propose a $750,000 borrowing from First Prairie at an explicitly entered 4.50% annual rate. Pick an eligible simulated value date; the overnight maturity skips weekends and configured holidays.
3. Switch to First Prairie's **Maker**, open the proposed trade, and accept its terms.
4. Use each bank's **Approver** to approve the agreement. Each bank's approver must differ from its maker, including the all-permissions persona. The second approval atomically reserves the lender's credit exposure and creates the funding obligation.
5. On the agreed value date the worker creates one durable institution-transfer instruction through the existing payment pipeline. Lender Operations can request funding explicitly. Future-dated funding waits; missed value dates become funding exceptions rather than silently changing the contract.
6. Observe the `pacs.009`, `pacs.002`, IMAD/OMAD, incoming delivery, and reserve journals through each bank's own wire link. The trade becomes **Active** only after final network acceptance and both banks' wire accounting are present.
7. Advance the shared simulation clock with Operations. The dashboard's one-day action explicitly advances time for all banks. For a one-day $750,000 loan at 4.50%, the worker accrues $93.7500 without moving reserves. A Friday-to-Monday loan accrues three calendar days.
8. At maturity the worker creates separate principal and interest obligations, each with its own `pacs.009`. After both payments settle and both banks post their journals, the agreement becomes **Repaid** and releases the credit reservation.
9. Open Treasury reconciliation independently at both banks. Principal and interest balances should clear; network cash receipts should match each bank's reserve journals. Interest income and expense remain in the journal history.

The indicative trade desk publishes one-simulated-day borrowing/lending quotes and can start a negotiated proposal from a counterparty quote. Quotes do not reserve reserves or credit, and do not bypass bilateral approval. A new separately approved trade can refinance an existing maturity by providing reserves; no automatic rollover is implied.

## Rules and recovery

- Trade status and payment status are separate. An overdue loan retains its original settled funding history and its unpaid liabilities.
- Principal and rates use decimal arithmetic. Monetary storage and accruals retain four decimal places; the lab's institution-transfer XML preserves that precision. The synthetic profile does not assert production Fedwire currency rounding rules.
- ACT/360 uses elapsed calendar dates `[ValueDate, MaturityDate)`. Daily amounts are differences of rounded cumulative totals, avoiding drift across repeated jobs. No automatic default penalty or interest beyond contractual maturity is added.
- Accepted funding is assigned the agreed simulated value date. If processing recovers after a delayed response, accrual catches up from that date rather than the recovery time. New funding submissions after the value date fail and require cancellation and a new agreement.
- Credit is reserved under a serializable transaction with SQL rowversion protection. It includes approved pending funding, active loans, and overdue loans. Limits cannot be reduced below committed exposure. Suspending a line blocks new funding but does not erase debt.
- Funding failures create **FundingException**, not an active loan. Reserved exposure remains until explicit retry or unfunded cancellation. Repayment liquidity failures create **Overdue** with failed obligations retained. Borrower Operations can retry those obligations once liquidity is available.
- A submitted payment is never retried by inventing another live instruction. Only an explicitly failed obligation can be retried. Its stable obligation ID and attempt number deterministically identify the new wire and correlation; old rejected payment records remain immutable.
- Funding and repayment wires reuse WireService, MessageManager, FedwireSimulator, durable outbox/inbox recovery, and the existing serializable settlement engine. Treasury never directly changes master balances.
- The existing FI wire journals post reserve/clearing movements. Treasury then posts separate balanced reclassification journals into those bank-owned wire ledgers: `FF_SOLD`, `FF_PURCHASED`, interest receivable/payable, and interest income/expense. `UnifiedJournalService` includes them automatically. Accruals and obligations retain both journal references; user views disclose only the active bank's entries.
- Treasury reconciliation checks outstanding principal, accrued unpaid interest, balanced/two-line journals, known Treasury journal identities, and reserve receipts versus reserve postings. The existing network wire report excludes recognized Treasury reclassification/accrual journals when comparing cash settlement journals.
- The dashboard's liquidity projection covers approved pending Treasury funding and unpaid contractual maturities. It excludes already-settled cash and unrelated future payment flows. Projected receipts are not guaranteed reserves.

## Configuration

Add a `FedFunds` section consistently to the Web and Treasury worker configuration, for example:

```json
{
  "FedFunds": {
    "ReferenceRate": 4.50,
    "Holidays": ["2026-11-26", "2026-12-25"]
  }
}
```

The default calendar skips weekends and has no configured holidays. It is a synthetic lab calendar, not a maintained official federal holiday schedule. The reference rate is displayed independently and never supplies a missing negotiated rate. No EFFR benchmark is calculated from quotes.

The lab retains the existing shared password and switchable bank/persona identities. It does not introduce production identity administration, collateral, automatic market matching, automatic rollovers, penalty pricing, or cross-facility credit. These are extensions beyond the bilateral overnight workflow.

## Persistence and verification

`050_FedFunds.sql` creates the seven subsystem tables with named constraints, ownership/scan indexes, unique credit lines, one approval per bank/trade, one accrual per trade/day, one obligation per trade/type, and unique current obligation correlations. The same schema is an embedded startup resource and forward migration `202611112000_fed_funds_treasury.sql`; bootstrap includes it. Financial history is retained.

```sh
dotnet build Banking.slnx --no-restore
dotnet test Banking.slnx --no-build --no-restore
git diff --check
BANKING_RUN_EXTERNAL_INTEGRATION=1 dotnet test tests/Banking.Domain.Tests --no-build --no-restore --filter 'FullyQualifiedName~FedFunds'
```

External scenarios use disposable named SQL databases and isolated RabbitMQ queue namespaces on the configured local lab containers (`localhost:11433` / `localhost:5672`). They run the real WireService, MessageManager, and FedwireSimulator consumers, and remove their databases/queues afterward. They cover the full overnight lifecycle, duplicate status delivery, settlement-before-publish crash recovery, repeated accrual jobs, concurrent credit approval, insufficient funding, retained overdue liability and repayment retry, third-bank access denial, journal reconciliation, and conservation of reserves. Default runs skip external work unless the environment variable is set.
