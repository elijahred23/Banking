# Wire operations implementation sequence

This repository is a learning simulator. The following is a delivery plan, not a claim of rail certification.

## Delivered foundation

- Active SWIFT correspondent routing now searches arbitrary configured paths, avoids cycles, prefers fewer legs, and breaks ties using relationship priority and ID. Route-selected events explain this policy. Existing explicit rail selection remains authoritative.
- Approved customer wires with insufficient available funds remain Created and record an AwaitingLiquidity event. SQL holds this waiting evidence; RabbitMQ is not the queue of record. The worker scans every 15 seconds in creation order and permits smaller fundable wires to proceed. This is best-effort creation ordering, not strict FIFO across competing worker instances.
- Successful validation atomically persists the customer hold, ISO message, route and events, ReadyForFed status, and three bank-scoped outbox messages. Account balance/hold and wire-status concurrency tokens prevent competing successful reservations. Settlement accounting remains deferred to final acceptance.
- Operations users can inspect the active bank's customer liquidity queue at `/Wires/Liquidity`. Waiting evidence also appears in the existing wire table and lifecycle events.
- Institution/master-account shortfalls retain existing rejection behavior. No automatic rail switching or correspondent book settlement has been added.

## Phase 1: intelligent routing and liquidity management

1. Add bank-scoped route policies with permitted rails, destination eligibility, amount bounds, currencies, effective dates, priority, and fallback behavior. Keep route evaluation pure in Domain and load candidate data in Infrastructure.
2. Add a business calendar with explicit time zones, holidays, cutoff exceptions, and a next-eligible-release calculation. Persist the policy version and evaluated time with each decision.
3. Evaluate book settlement only when both accounts share an issuer, service agreements are active, currency matches, and both parties have authorized the operation. Reuse correspondent posting services and balanced journals; never masquerade book settlement as Fedwire.
4. Add durable queue entries and reservations for customer, master-account, and correspondent positions. Synchronize EF configuration, startup upgrades, baseline SQL, forward migration, and database documentation. Use a unique payment/reservation constraint and transactional claim/release.
5. Introduce operator priorities with creation-time tie breaking and explicit starvation policy. Reevaluate on incoming settlement through an outbox event, retaining a periodic recovery scan. Cancellation, rejection, and expiry release only the originating reservation.
6. Send no-route and policy failures to a bank-scoped repair case, preserving the evaluated candidates and reasons. Require authorized operators to propose changes; rerun approval/compliance when material instructions change.

Acceptance: competing workers cannot overreserve; restart resumes waiting instructions; calendar boundaries and timezone changes are deterministic; no route produces a repair case; book postings balance and settle once; approval/compliance cannot be bypassed by release.

## Phase 2: tracking and exception recovery

7. Extend lifecycle evidence with separate transport delivery, business acceptance, settlement, and beneficiary-credit events. Correlate message ID, original message ID, UETR, route step, and settlement identifier across pacs.008/009, pacs.002, pacs.004, and investigation messages. Replay remains read-only.
8. Model pre-release cancellation independently from post-submission recall. Recalls do not reverse money until the receiving institution accepts and a return settles. Amendment requires versioning and fresh authorization where financial instructions change.
9. Extend returns with original-wire correlation, partial-return limits, unique settlement references, fees, and balanced reversal journals. Reject mismatched ownership, amount, currency, or duplicate returns.
10. Classify repair failures (directory, instructions, liquidity, compliance, connectivity) and assign bank-scoped operator queues. Preserve original instructions and audit every repair/resubmission.
11. Expand simulator scenarios for outage windows, delayed acknowledgments, late final outcomes, rejection, and duplicate delivery. Keep transport acknowledgment distinct from settlement; test duplicate and out-of-order messages against the durable outbox and consumer idempotency.

Acceptance: full end-to-end traces explain all messages; retries cannot repost; recalls cannot refund early; partial returns cannot exceed settled principal; repaired wires retain their audit history.

## Phase 3: scheduling, beneficiaries, batches, authorization

12. Create versioned beneficiary and template records with bank ownership, routing/account validation, directory evidence, approval state, and effective dates. A template cannot silently change an approved instruction.
13. Add future-date and recurrence instructions with business-date adjustment, cancellation windows, timezone, end date/count, and a unique occurrence key. Each occurrence creates a normal wire and follows current approvals, compliance, routing, and reservation controls. Missed-run handling must be explicit.
14. Add bank-specific authorization policies for amount, actor authority, beneficiary status, and risk. Persist required approvals and policy version; enforce maker separation, independent approvers, escalation, expiry, and reapproval after material edits at the mutation boundary.
15. Add staged CSV and supported ISO batch import with file size/count limits, safe XML parsing, per-row errors, normalized instruction previews, and bank-scoped idempotency keys. Batch approval must not bypass individual wire controls.
16. Add duplicate scoring by bank, amount/currency, normalized beneficiary, value date, reference, and instruction fingerprint. Explain matches and record authorized overrides. Distinguish suspected duplicate business instructions from transport redelivery.

Acceptance: two scheduler instances create one occurrence; holiday/cutoff execution is predictable; unapproved beneficiaries cannot execute; duplicate uploads do not duplicate payments; approval expiry and independence are enforced server-side.

## Phase 4: correspondent integration and analytics

17. Version bank/affiliate fee schedules for outgoing, incoming, investigation, and return services. Snapshot quoted fees and charging basis; post balanced, independently correlated fee journals only at the configured earned event.
18. Build bank-scoped KPIs from durable events and final postings: stage durations, settled volume, exceptions, rejection/return rates, waiting demand, reservation utilization, and fee income. Show reporting time zone and date window. Do not count acknowledgment as settled volume.
19. Forecast liquidity from approved scheduled demand and separately identified expected incoming funds. Clearly distinguish committed balances from forecasts. Reconcile correspondent statements with immutable posting IDs and expose breaks for authorized operations review.

Acceptance: dashboard totals reconcile to journals and statements; fee retries post once; cross-bank data cannot leak through filters, exports, or drilldowns; forecasts never increase spendable balances.

## Verification strategy

Run the repository build, tests, and diff whitespace check for every delivery. Add focused policy and worker tests for rejection, retry, concurrency, cancellation, and duplicate scenarios. For durable model changes, also exercise idempotent startup against the configured SQL Server and an empty bootstrap database. Run opt-in SQL/RabbitMQ integration scenarios when those local services are available. Add bank-scoping and authorization tests for each new mutation and operations endpoint.
