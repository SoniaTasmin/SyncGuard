# SyncGuard — three-minute walkthrough

**Before recording:** `cp .env.example .env` (set a password; install `jq`), `docker compose down -v && docker compose up -d --build`,
and wait until `curl -s localhost:5001/health` prints `Healthy`. Open two terminals. Terminal B runs
`docker compose logs -f worker destination-api | grep -v Microsoft` throughout. Paste the helpers into terminal A:

```bash
F=localhost:5002/fault-injection/rules; J='content-type: application/json'
order() { curl -s -X POST localhost:5001/api/orders -H "$J" -d "{\"orderNumber\":\"$1\",\"customerCode\":\"CUST-007\",\"lines\":[{\"sku\":\"${2:-ABC-1234}\",\"quantity\":1,\"unitPrice\":9.99}]}"; echo; }
show()  { curl -s localhost:5001/api/orders/$1 | jq '{orderNumber, outbox: .outbox | {status, attemptCount, lastStatusCode, lastError, destinationId}}'; }
```

---

### 0:00–0:20 — The problem

> "A wholesaler's orders live in SQL Server and must reach a fulfilment API that sometimes fails: 500s, 503s,
> rate limits, timeouts, and worst of all, accepting an order and losing the response. A naive integration
> either loses orders or books them twice. This demo shows the fix."

Show the Mermaid diagram in the README for a few seconds: outbox → worker → idempotent destination → reconciliation.

### 0:20–0:45 — Happy path and the transactional outbox

```bash
order DEMO-1; sleep 3; show DEMO-1   # the first request after startup can take ~2 s (warm-up)
```

> "The order and its outbox event are committed in one SQL transaction, so there is never an order without a
> pending delivery. The worker sends it with the event id as the `Idempotency-Key`. Delivered on the first attempt."

### 0:45–1:25 — Failures, one at a time

```bash
curl -s -X POST $F -H "$J" -d '{"fault":"Http503","count":3,"orderNumber":"F-503"}' >/dev/null; order F-503
curl -s -X POST $F -H "$J" -d '{"fault":"Http429","retryAfterSeconds":10,"orderNumber":"F-429"}' >/dev/null; order F-429
curl -s -X POST $F -H "$J" -d '{"fault":"LostResponse","orderNumber":"F-LOST"}' >/dev/null; order F-LOST
order F-BAD 'bad sku'
sleep 15; for o in F-503 F-429 F-LOST F-BAD; do show $o; done
```

Point at each result:
* **F-503:** delivered after 4 attempts using exponential backoff with jitter (terminal B shows the delays).
* **F-429:** the second attempt came 10 s later, exactly as `Retry-After` asked.
* **F-LOST:** the destination saved it, then the connection was dropped. The retry returned `200` (an idempotent replay) with
  the same `destinationId`, so there is no duplicate.
* **F-BAD:** a `422` is permanent, so it went to `ManualReview` after one attempt instead of being retried forever.

### 1:25–2:20 — Scale, chaos and a crashed worker

```bash
for r in '{"fault":"Http503","count":60,"everyNth":7}' '{"fault":"Http429","count":30,"everyNth":11,"retryAfterSeconds":3}' \
         '{"fault":"LostResponse","count":40,"everyNth":13}' '{"fault":"Http500","count":20,"everyNth":17}'; do curl -s -o /dev/null -X POST $F -H "$J" -d "$r"; done
curl -s -X POST "localhost:5001/api/orders/seed?count=1000&seed=2026"; echo
sleep 2; docker compose kill -s SIGKILL worker; docker compose start worker
watch -n2 "curl -s localhost:5001/ops/status | jq '{worker: .worker.status, outbox, delivery}'"
```

> "A thousand orders under 150 injected faults, and I've just killed the worker with SIGKILL mid-run. Nothing was in
> memory; every attempt is recorded in SQL before it is sent. The new worker waits for the dead one's lease to expire
> (≤ 30 s), takes over, and the pending count drains to zero. Retry attempts are counted on the right."

Exit `watch` once `pending` is 0.

### 2:20–2:50 — Reconciliation: prove it, then break it

```bash
curl -s localhost:5001/ops/reconciliation | jq '{isConsistent, isSettled, counts}'
docker compose exec -T sqlserver bash -c 'SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" /opt/mssql-tools18/bin/sqlcmd -C -b -S localhost -U sa -d SyncGuardDestination' <<'SQL'
DELETE ReceivedOrders WHERE OrderNumber = 'SEED2026-00001';
UPDATE ReceivedOrders SET TotalAmount = TotalAmount + 1 WHERE OrderNumber = 'SEED2026-00002';
GO
SQL
curl -s localhost:5001/ops/reconciliation | jq '.counts, [.discrepancies[] | {kind, orderNumber, detail}]'
```

> "Every source order is matched at the destination: no missing orders and zero duplicates. The only gap is F-BAD,
> which is flagged for a human. Now I delete one order and alter another downstream, and reconciliation names
> both. Orders still inside the settle window are reported as in flight, not as false alarms."

### 2:50–3:00 — Close

> "At-least-once delivery, made safe by durable idempotency, with honest limits: one active worker in v1 and no auth.
> The README and CASE_STUDY have the details, and `dotnet test` runs all nine scenarios against a real SQL Server."
