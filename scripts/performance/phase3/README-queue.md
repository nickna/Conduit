# Spend transport measurement

Run the existing `transport-throughput` mode through `run-queue-matrix.ps1` using
an already published, sealed .NET 11 Benchmarks folder. The caller supplies an
explicit connection to an owned private PostgreSQL server's `postgres` maintenance
database, creates any Docker/netem fixtures, and removes them afterward. The driver
creates fresh `conduit_phase3_queue_*` databases and never drops or reuses a database.
Redis is not used by the synthetic transport cohort.

The benchmark host deliberately compiles synthetic handlers at runtime. Its private
compiler package explicitly participates in publishing; production service
dependency guards remain unchanged. Verify a published folder before connecting
to a fixture:

```powershell
dotnet /tmp/published-benchmarks/ConduitLLM.Benchmarks.dll transport-runtime-smoke
```

This database-free probe uses the queue host's compiler configuration to compile
and execute a constant C# method. A successful build alone cannot establish that
the published folder contains its complete runtime dependency closure.

```powershell
pwsh ./scripts/performance/phase3/run-queue-matrix.ps1 `
  -BenchmarkDll /tmp/published-benchmarks/ConduitLLM.Benchmarks.dll `
  -AdminConnection $env:PHASE3_QUEUE_ADMIN_CONNECTION `
  -OutputDirectory /tmp/queue-results/netem-0 `
  -LatencyLabel 'owned bridge, no added latency' `
  -Batches @(10,25,50,100) -HandlerCostMs @(0,15) -Repeats 5 -Messages 800
```

Invoke separately with a new output directory for each controlled PostgreSQL
network-latency setting. The driver does not change `tc`, start Docker, infer a
production endpoint, or treat synthetic handler delay as database latency. Every
case records 20 actual `SELECT 1` round trips. Stop and drain real application hosts
before this cohort to keep their polling out of the measurement.

Reports require exact released runtime 11.0.0 on the expected native architecture
and retain the actual process/runtime/core-library and GC mode/configuration
identities. The published-input manifest covers the full supplied binary folder.
The strict parser uses raw JSON decimals for synthetic amounts, without a binary
floating-point monetary conversion. Run `test-queue-report.ps1` for report-policy
acceptance/rejection fixtures without connecting to any datastore.

`matrix.json` retains declared order, every child exit/deadline outcome, report
hashes and the complete published-input file manifest before/after execution.
Each schema 2 child report retains exact logical/envelope IDs, ordered handler
start/finish and independently SQL-observed `Handled` ticks, plus transport queue,
scheduled, incoming, outgoing and dead-letter depths. A failed warmup, missing or
duplicate event, ordering/concurrency error, incomplete handler or unsettled store
fails the command. A failed case stops the matrix and preserves prior/raw evidence.
Before publishing warmup messages, an initial read-only `to_regclass` gate waits
for the five required store relations under the existing warmup deadline. Its
table names, poll count and elapsed time are retained in `setup`. This hardens
observer startup against asynchronous provisioning; it is not a reproduced
production defect. SQL/authentication errors fail immediately, and later missing
relations or observation failures remain failures.

Latency quantiles use publish-intent to SQL-observed settlement, including the
100 ms polling observer's delay and database work. They are upper-bound observations,
not exact acknowledgement timestamps. `saturationObserved` requires at least three
samples with a backlog of one receive batch; a complete shallow-queue run alone
does not establish consumer capacity. Do not compare it to historical entry-count
rates as if the collectors had identical semantics.

The handler is synthetic: it conserves exact decimal event amounts but does not run
`SpendUpdateProcessor`, debit a group, reconcile Redis reservations, or prove
financial closure. Pair any proposed policy adoption with the real Gateway
idempotency, group-balance, depletion-notification, follow-on durability and live
financial-drain gates. Production batching, concurrency and transport are unchanged.
