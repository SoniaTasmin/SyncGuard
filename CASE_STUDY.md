# Case study: keeping SQL Server and an unreliable REST API consistent

*A portfolio demonstration with synthetic data. All code and data are original.*

## The business problem

A wholesale company records orders in SQL Server. Each order must reach an external fulfilment API. That API
behaves like many real third-party APIs: it intermittently returns 500/503, rate-limits with 429, times out, goes
offline, and occasionally **accepts an order but the response never arrives**.

Typical symptoms in companies with this problem:

* **Lost orders.** The order is saved locally, but the process crashes, or the call fails after the commit, and nobody retries.
* **Duplicate shipments.** A timeout is retried blindly, and the API had in fact accepted the first call.
* **Poison messages.** One malformed order is retried forever, clogs the queue, and floods the logs.
* **No way to prove consistency.** When finance asks whether every order reached fulfilment, nobody can answer with evidence.

## Why the naive approaches fail

| Approach | Failure |
|---|---|
| Call the API inside the request that saves the order | The API is slow or down, so order entry fails or hangs. If the API succeeds and the DB commit then fails, fulfilment has an order the source does not. |
| Save the order, then call the API | A crash between the two steps loses the order silently. |
| Retry on every error | Duplicates when the response was lost, and endless retries on invalid data. |
| "Exactly-once" messaging | Not achievable across an HTTP boundary you don't control. Claiming it hides the real failure modes. |

## The engineering approach

1. **Transactional outbox.** The order, its lines and an `OutboxEvents` row are written in **one SQL transaction**.
   Either all of them exist or none do, so an order without a pending delivery cannot exist.
2. **Durable, at-least-once delivery.** A .NET `BackgroundService` polls due events and posts them. Every attempt is
   recorded **before** the request is sent, so a crash mid-request still counts toward the attempt limit, and the
   event becomes due again after its backoff. No delivery state lives in memory.
3. **Stable idempotency key.** The outbox event id is sent as `Idempotency-Key`, and the payload is serialized once,
   so retries send identical bytes. The destination enforces `UNIQUE(IdempotencyKey)` and `UNIQUE(OrderNumber)` in SQL.
   A repeat returns the original result (`200`, same `destinationId`). The same key with a different payload is refused (`409`).
4. **Failure classification.**
   * 408/429/5xx, timeouts and transport errors are retryable.
   * Capped exponential backoff with jitter spreads retries; `Retry-After` is honoured exactly.
   * Other 4xx responses are permanent: the event goes to **manual review** after one attempt.
   * Events that exhaust `MaxAttempts` also go to manual review.
5. **Single active worker, enforced.** A lease row is acquired with an atomic compare-and-set. It doubles as the heartbeat
   for health reporting. A graceful shutdown releases the lease; after a crash a new worker takes over when it expires.
   `AttemptCount` is an optimistic-concurrency token, so overlapping dispatchers cannot both claim the same attempt.
6. **Reconciliation with a quiescence rule.** It compares every source order with the destination's records (customer,
   total, lines) and reports *missing*, *mismatched*, *undelivered*, *unexpected* and *duplicated* orders. Pending events
   younger than a settle window are reported as *in flight*, not as false alarms.
7. **Operational visibility.** Structured JSON logs carry the event id on both sides. `/ops/status` reports pending, due,
   failed and delivered counts, retry metrics, the last successful sync, and worker health, all derived from SQL.

The solution is deliberately small: three .NET 10 services, one class library, two SQL databases and Docker Compose.
There is no message broker, cache or orchestration platform. SQL Server already provides the durability and
uniqueness guarantees the design needs.

## Verifiable results

### Automated tests: 30 of 30 passing

The integration tests run against a real SQL Server 2022 (Testcontainers) and exercise every acceptance scenario. Retry
timing is asserted exactly with a fake clock, for example "a 429 with `Retry-After: 120` is not retried at 119 s and is
retried at 120 s".

| # | Scenario | Evidence |
|---|---|---|
| 1 | Normal order syncs | Delivered on attempt 1, destination row matches, reconciliation consistent |
| 2 | 503 then recovery; unreachable; timeout | Delivered on attempt 4 / 3; exactly one destination row |
| 3 | 429 honours Retry-After | Next attempt at exactly `now + 120 s` |
| 4 | Duplicates | Replay returns the same id; 20 concurrent identical requests produce 1 row; a conflicting payload gets `409` |
| 5 | Lost response | Destination committed, worker saw a transport error, retry replayed, still 1 row |
| 6 | Worker restart | Real worker stopped with 8 requests in flight; a fresh worker delivered 200/200 with no duplicates |
| 7 | Invalid data | `422` goes to manual review after 1 attempt and is never retried; exhausted retries also go to manual review |
| 8 | Reconciliation | Detects deleted, altered (two fields) and unexpected rows, plus undelivered orders; in-flight becomes undelivered after the window |
| 9 | 1,000 orders under faults | 1,000 delivered, 1,000 destination rows, 1,000 distinct keys, reconciliation consistent |

### Live run (Docker Compose, single laptop, 2026-10-21)

The demo stack ran 1,000 seeded orders plus 4 hand-made orders, against a deterministic fault mix of 60×503, 30×429,
40 lost responses and 20×500. **The worker was killed with SIGKILL two seconds into the run** and restarted.

| Measure | Result |
|---|---|
| Time to drain all events (including ~28 s waiting for the dead worker's lease) | 37 s |
| Delivery attempts / retry attempts | 1,163 / 158 |
| Orders delivered | 1,004 of 1,004 valid orders |
| Destination rows / distinct order numbers / distinct idempotency keys | 1,004 / 1,004 / 1,004, **zero duplicates** |
| Reconciliation | 1,004 matched, 0 missing, 0 mismatched; 1 undelivered (the intentionally invalid order, in manual review) |
| After deleting one destination row and altering another | Reported exactly: 1 Missing (`SEED2026-00001`), 1 Mismatched (`SEED2026-00002`, `totalAmount`) |

Every number in this table can be reproduced with the commands in [DEMO_SCRIPT.md](DEMO_SCRIPT.md).

## Trade-offs and honest limits

* **At-least-once, not exactly-once.** Duplicates are *sent* sometimes; they are never *booked* twice.
* **One active worker in v1.** It is simple and safe, but throughput is bounded by one process. Scaling out needs per-row
  claims (`UPDATE … OUTPUT` with `READPAST`) instead of a global lease.
* **No ordering between events.** That is fine for one event type per order. Updates and cancellations would need per-order sequencing.
* **In-memory reconciliation.** Good to roughly 10⁵ orders; larger volumes would reconcile in time windows.
* **Demo-grade security** (no auth, `sa` login, plain HTTP). See [SECURITY.md](SECURITY.md).

## What this would look like for a client

The same pattern applies to most "our system and their API drift apart" problems. The work would be:

1. **Diagnosis.** Measure current drift with a one-off reconciliation, and classify the API's real failure modes from logs.
2. **Fix.** Add an outbox in the existing SQL Server database (one table, written in the existing transaction),
   a small delivery worker, and idempotency keys (most commercial APIs already accept them).
3. **Proof.** Run a scheduled reconciliation, alert on undelivered or mismatched orders, and keep a manual-review queue with a clear owner.
