# Messaging throughput and headroom

Conduit's event bus runs on Wolverine over the PostgreSQL transport. Queues are tables and
listeners **poll** them; there is no broker pushing work. That one fact determines the
system's throughput ceiling, how it degrades, and which alarm actually gives warning — all of
which behave differently from a push-based broker, and none of which are obvious from the
event rates alone.

This page explains where the ceiling comes from, which queue binds first, and what to do
about it. It does not restate the tuning values — those live in
`ConduitEndpointPolicies` and are the source of truth.

The measured rates below, including roughly 39/s at batch 10 and 200/s at batch 50,
are preserved historical local-fixture observations from the earlier handler-entry
collector ([baseline source](https://github.com/nickna/Conduit/blob/f68cb5fae1130bbe1ef4fc30aca7a3266094dbaa/docs/operations/messaging-throughput.md)).
They are not new .NET 11 results or production capacity forecasts. The current
schema 2 harness separately records handler completion and SQL-observed durable
inbox settlement; its rates are not interchangeable with those historical figures.

## What sets the ceiling

Two independent limits apply to every listener, and the lower one wins:

```
ceiling  =  min(  receive batch ÷ poll interval  ,  handler concurrency ÷ handler latency  )
             \_______ transport-bound _______/     \_______ handler-bound _______/
```

**The transport-bound term is the one people miss.** A listener claims at most *receive batch*
rows per poll and polls on a fixed interval, so a queue with a small batch has a hard rate cap
no matter how fast its handler is or how many nodes are running. The batch comes from each
endpoint's `PrefetchCount` in
[`ConduitEndpointPolicies`](../../Shared/ConduitLLM.Configuration/Messaging/ConduitEndpointPolicies.cs);
the interval is set alongside it in
[`WolverineEndpointPolicy.ListenWithPolicy`](../../Shared/ConduitLLM.Configuration/Messaging/Wolverine/WolverineEndpointPolicy.cs).

Measurement matches the model closely — a queue delivers about 95% of `batch ÷ interval` when
its handler is not the constraint. Reproduce with the harness (see [below](#reproducing-the-measurements)).

> **`PrefetchCount` does not mean on this transport what it meant on the previous one.** On
> RabbitMQ it bounded *unacknowledged messages held locally* — back-pressure, with no effect on
> sustained rate. Translated to the Postgres transport it became the per-poll batch, which is a
> direct throughput divisor. An endpoint whose prefetch was deliberately set *low* for safety
> therefore inherited a low rate cap that was never intended.

## Which queue binds first

`spend-update-events` — by a wide margin, and it is also the least able to grow.

Its policy is `ConcurrentMessageLimit = 1`, which
[translates](../../Shared/ConduitLLM.Configuration/Messaging/Wolverine/WolverineEndpointPolicy.cs)
to `ListenWithStrictOrdering`: exactly **one node** listens, handling messages **sequentially**.
That is the correct semantics for ordered financial processing, but it has a consequence worth
stating plainly:

**Adding Gateway instances does not add spend throughput.** Every other queue absorbs load by
scaling out or by running its handlers in parallel; this one cannot do either. Whatever ceiling
it has is the ceiling, fleet-wide.

Compounding that, it carries the smallest receive batch of any tuned endpoint, so it is
transport-bound long before its handler becomes the limit. Measured on a single node against a
local database, it sustains roughly **39 msg/s** — an order of magnitude below every other
queue, and *unchanged* whether the handler takes 0 ms or 15 ms, because the handler is not what
is limiting it.

The crossover to handler-bound sits near **25 ms** of handler latency. `SpendUpdateProcessor`
makes four sequential round trips per message (key read, group read, idempotent balance
adjustment, follow-on publish), so a deployment whose database round trips are slower than
about 6 ms each — plausible with a networked or cross-AZ database, unlike the local one these
figures come from — will be handler-bound instead, at a *lower* rate than 39/s. **Re-measure
against production-like database latency before trusting any specific number here.**

## Latency will not warn you

The natural alarm — "page when publish→handle latency crosses a threshold" — does not work on a
poll-based transport. Latency is dominated by the poll interval (a message waits half an
interval on average) and stays essentially **flat right up to the ceiling**, then goes vertical
once arrivals exceed drain rate and the backlog grows without bound.

Measured on `spend-update-events`: p99 held between roughly 265 and 275 ms across offered loads
from 10/s to 35/s — that is, from 25% to 90% of capacity, with no meaningful upward drift. A
latency alarm on this queue moves from "quiet" to "unbounded backlog" with almost no warning
band in between.

**Alarm on utilization and queue depth instead**, both of which move early and proportionally:

| Signal | Where it comes from | Threshold |
|---|---|---|
| Sustained delivery rate ÷ measured ceiling | Wolverine metrics ([observability](../monitoring.md)) | **warn 60%**, **page 80%** |
| Queue depth | Row count in `wolverine_queues.wolverine_queue_<name>` | **page** if above 10 s of drain (`ceiling × 10`) for 5 min |

Use queue depth as the authoritative signal. Rate tells you how close you are; depth tells you
whether you have already lost.

## What to do before changing transports

In increasing order of cost and risk. **Exhaust these before treating the transport as the
problem** — a transport swap is a bootstrap change plus a full re-validation of ordering and
financial parity, and none of the levers below require either.

1. **Raise the endpoint's `PrefetchCount`.** One line in `ConduitEndpointPolicies`. The ceiling
   moves linearly and — critically — **strict ordering is unaffected**, because the batch only
   controls how many rows a poll claims, not how they are handed to the handler; sequential
   dispatch on a single node is preserved. Measured on `spend-update-events`: batch 10 → ~39/s,
   25 → ~99/s, 50 → ~200/s, 100 → ~406/s, with handler concurrency pinned at 1 throughout.
   This is by far the cheapest lever and it is the right first response to spend-queue pressure.
2. **Shorten the poll interval.** Also linear, but it multiplies the standing database load
   described below, and it helps every queue whether or not they need it. Prefer lever 1.
3. **Partition the ordered queue.** `SpendUpdateRequested` exposes a `PartitionKey` (the virtual
   key), so the domain only requires ordering *per key* — global serialization across all keys
   is stricter than necessary and is what caps the queue. Splitting into partition-ordered
   listeners lifts the cap without weakening the guarantee the domain actually depends on.
   This needs a correctness review first: balance is held per *group*, not per key, so
   per-key partitioning must be shown safe against the group-level balance adjustment and the
   depletion-crossing check in `SpendUpdateProcessor`.
4. **Change transports.** Only once the above are exhausted — realistically when a queue needs
   sustained throughput in the thousands per second, or when poll load itself has become the
   binding constraint on the database.

## Standing cost of polling

Listeners poll whether or not there is traffic, so the transport has a **non-zero floor that
scales with hosts × queues, not with load**. On an idle two-host development environment (one
Gateway, one Admin; six queues plus their scheduled-message tables) this measures at roughly
**216 table scans/s** and **175 transactions/s**, entirely served from Postgres's buffer cache.

That is negligible at this size, and it is not negligible in shape: a Gateway scaled to ten
instances pays roughly ten times the Gateway share of it while completely idle. Include this
floor when sizing the database, and treat a rising idle transaction rate after a scale-out as
expected rather than as a leak.

Sample it directly:

```sql
SELECT schemaname || '.' || relname AS table, seq_scan + idx_scan AS scans
FROM pg_stat_all_tables
WHERE schemaname LIKE 'wolverine%'
ORDER BY scans DESC;
```

Take the difference between two samples over a known interval; the absolute counters are since
statistics reset.

## Reproducing the measurements

The harness lives in `Tests/ConduitLLM.Benchmarks` and drives a real Wolverine host through the
**production** configuration path — the same `ConduitEndpointPolicies` descriptors and the same
`WolverineEndpointPolicy` translation the services use — so retuning an endpoint changes what
the harness reports, and no figure here can silently drift from the code that produces it.

```bash
# Set this to an explicitly owned private PostgreSQL fixture, targeting its postgres
# maintenance database. Never use an application database or implicit localhost:5432.
: "${PHASE3_QUEUE_ADMIN_CONNECTION:?Set the private fixture maintenance connection first}"

# Ceiling for one queue, sweeping simulated handler cost
dotnet run -c Release --project Tests/ConduitLLM.Benchmarks -- transport-throughput \
    --queue spend --messages 600 --handler-cost-ms 0,5,15,30,60 \
    --admin-connection "$PHASE3_QUEUE_ADMIN_CONNECTION" \
    --database "conduit_msgbench_$(date +%s%N)" \
    --output "artifacts/queue-$(date +%s%N).json"

# Latency at a fixed offered load (the only way to get a meaningful latency figure)
dotnet run -c Release --project Tests/ConduitLLM.Benchmarks -- transport-throughput \
    --queue spend --messages 400 --handler-cost-ms 15 --rate 20 \
    --admin-connection "$PHASE3_QUEUE_ADMIN_CONNECTION" \
    --database "conduit_msgbench_$(date +%s%N)" \
    --output "artifacts/queue-$(date +%s%N).json"

# Effect of a proposed retune, before committing it to ConduitEndpointPolicies
dotnet run -c Release --project Tests/ConduitLLM.Benchmarks -- transport-throughput \
    --queue spend --messages 800 --receive-batch 50 \
    --admin-connection "$PHASE3_QUEUE_ADMIN_CONNECTION" \
    --database "conduit_msgbench_$(date +%s%N)" \
    --output "artifacts/queue-$(date +%s%N).json"
```

Queues: `spend`, `webhook`, `image`, `video`, `gateway-default`.

Each invocation creates fresh databases with an owned name prefix and a fresh JSON report;
existing databases or report files are rejected. The schema 2 completion gate requires a successful
warmup, exact unique message IDs, preserved sequential spend handling, finished handlers,
SQL-observed `Handled` inbox rows and empty active transport/inbox/outbox/dead-letter state.
Settlement timestamps include the polling observer's delay and database work. These
synthetic handlers do not debit real group balances or prove financial closure. See the
[phase 3 queue driver](../../scripts/performance/phase3/README-queue.md) for matched sweeps,
runtime/GC identity and report-policy checks. No production batch/concurrency change is
adopted by running the experiment.
The default publisher parallelism is1; preserve it for the strict spend-order check.

The harness creates and uses its own databases. This is deliberate — the
queue names are the production ones, so pointing it at the application database would inject
synthetic spend and webhook events into queues a running Gateway is listening on.
