# Distributed lock ownership (#1399)

Status: Accepted; both hosts select the new adapter after the DL-6 evidence gate.
Baseline: merged FusionCache PR #1413 on `dev`. All seven packages are implemented
serially, with validation and a commit before advancing.

DL-2 evidence: both host graphs build; 8 PostgreSQL 17 lock integration tests passed
(six adapter scenarios plus the two retained legacy regressions). Adapter delegates
advisory acquisition/disposal to upstream, with no advisory SQL, expiry timer or retry
implementation. DL-6 refined keepalive ownership (below).
Bounded metrics report outcome, wait/hold, loss and release failures, never key IDs.
Legacy/new exclusion is tested using the actual legacy implementation in both directions.
The integration fixture accepts `CONDUIT_LOCK_TEST_POSTGRES` for PG16/17 matrix runs;
without it, its owned PostgreSQL 17 container remains the default.

DL-3 evidence: 97 focused media unit tests passed, including two-instance exclusion,
backend outage fail-closed, detected loss before work, and cancellation after storage
ignores its token. Actual PostgreSQL 17 session termination canceled the deletion
engine within the 10s bound, started only one batch, and recorded `Cancelled` with a
fresh three-second completion token. Manual/scheduled/hard-delete/restore/key-cleanup
share the numeric identity and propagate request/shutdown, 30-minute deadline and loss.
Storage calls already running at loss can complete; canceled post-storage bookkeeping
may leave a record for a subsequent idempotent cleanup. No fencing/exactly-once claim.

DL-4 evidence: 10 sync/alert unit tests and two actual PostgreSQL 17 caller tests passed.
Manual sync still returns HTTP 409 against a scheduled holder. Scheduled ownership is
released before the schedule interval (the old using block encompassed that sleep).
Cancellation propagates while a fresh context records only terminal run status with a
three-second budget. A terminated scheduled session cancels protected work and async
release preserves that cancellation. An isolated alert send held ownership for >5s;
even canceled uncooperative send work continued excluding competitors until awaited.
Redis repository methods have no token API: check before/after each awaited operation;
do not abandon those calls or redesign mark-before-send/cooldown. Known-loss release
errors are logged/counted without masking cancellation; healthy release errors propagate.

DL-5 evidence: 35 helper/populator/warmer tests and two actual PostgreSQL optional-policy
tests passed. Busy waiting returns null: discovery skips, cache population falls back.
Infrastructure failure (including backend timeout) remains distinct and preserves optional
fallback. Missing service falls back; caller cancellation never does. Operation failure
is never executed twice. Callbacks receive protected tokens; the populator keeps its old
uncancellable overload, awaiting it under healthy ownership, and offers a token overload.
Striped local coalescing and all cache rechecks remain. Connection warming awaits its
subscription listener on cleanup and StopAsync cancels/awaits work. Loss/failure cannot
publish a warming success signal; Redis pub/sub operations already in flight remain
uncancellable. Discovery also avoids logging a canceled warm as completed.

## Contract and intentional lifetime change

`IDistributedLockProvider.TryAcquireAsync(key, acquisitionTimeout, cancellationToken)`
returns `IDistributedLockOwnership` or null **only for contention**. Zero wait means
an immediate attempt; bounded wait uses the provider library. Backend/pool/network
errors and cancellation propagate. A handle exposes `HandleLostToken` and async
disposal. It owns its locking session independently of EF contexts/transactions.
There is no validity/expiry/owner-value inspection, extension or status-check API.

Healthy ownership lasts until **all protected work has completed or stopped**.
Request/shutdown, operation deadline and loss tokens are linked by the caller;
cancellation never disposes a healthy handle while uncooperative work continues.
Former media/sync leases become 30/15-minute operation deadlines. Alerts and optional
warming have no new operation deadline: their existing request/shutdown token applies.
Loss notification is asynchronous, does not fence storage or Redis mutations and
does not provide exactly-once deletion/delivery. Check cancellation before each new
batch, retry and side effect; await operations that cannot accept cancellation.

## Refreshed policy and timeout inventory

| Caller | Previous lease / acquisition | Busy | Backend failure / absent optional service | Cancellation, loss, shutdown | Release failure |
| --- | --- | --- | --- | --- | --- |
| Scheduled media cleanup | 30 min / immediate | Skip this run | Fail closed, record/log failed run | Stop new batches; cancel storage/repository work; terminal status must not be success | Log and propagate where possible |
| Manual cleanup, hard delete, restore, virtual-key media deletion | Shared `media:cleanup:leader`, 30 min / immediate | Existing conflict/error contract | Fail closed (no media mutation without ownership) | Request + 30 min deadline + loss; hold through completion | Observable failure |
| Scheduled/manual OpenRouter sync | Shared `openrouter:metadata-sync:leader`, 15 min / immediate | Scheduler skips; HTTP 409 manual | No sync; log/propagate error | Request/shutdown + 15 min + loss; persist canceled terminal state with bounded cleanup | Observable failure |
| Budget alert thresholds | `RedisKeys.Lock.AlertThreshold`, 5 sec / immediate | Skip threshold | Log; do not send unguarded | Check between Redis operations; pass token to SignalR; preserve mark-before-send and cooldown | Observable/logged |
| DistributedCachePopulator | 30 sec / 10 sec wait, 50ms polling | Factory fallback | Factory fallback | Propagate cancellation, retain striped local lock and cache rechecks; uncancellable callback finishes under healthy ownership | Log; never rerun operation |
| Discovery warming | 5 min / configured timeout (1 sec polling) | Skip on timeout | Warm without distributed coordination, including missing service | Protected token through cache loader; no false success after loss | Log; never rerun operation |
| Connection warming | Configured `LockExpiry` / immediate | Follower waits for Redis signal then fallback/stagger | Existing optional startup policy; no success signal on failure | Shutdown/loss through warming; unsubscribe and await listener cleanup | Log; never rerun leader work |

Indirect callers: `DistributedSpendNotificationService` constructs BudgetAlertManager;
`LeaderElectionServiceExtensions` resolves the warmer's optional lock dependency.
Gateway has two cache composition branches; Admin registers locks in MediaLifecycleExtensions.
FusionCache did **not** retire DistributedCachePopulator. Its independent local policy remains.
All legacy status/extension/value/validity members are only used by implementations/tests.
Tests requiring retargeting: lock unit/integration, cleanup service/soft-delete/key service,
optional helper/populator, pool warmer; add sync/alert cancellation and contention tests.

## Dependency and session ownership

Pin provider-only **DistributedLock.Postgres 1.3.1**, MIT (restored nuspec inspected;
source commit `429cc01e452bc7ddac2e1a02a68268ca9591e887`). Npgsql remains **10.0.3**.
The net8.0 package allows Npgsql >=8.0.6; DistributedLock.Core resolves 1.0.9.
[Upstream provider](https://github.com/madelson/DistributedLock/blob/master/docs/DistributedLock.Postgres.md)
and [loss API](https://github.com/madelson/DistributedLock/blob/master/docs/Other%20topics.md).

Use the database connection string owned by configuration, not a scoped DbContext or
EF transaction. Normalize a separate pool: application name `conduit-distributed-lock`,
maximum pool size **32**, minimum 0, open/command timeout **5 seconds**. Provider
Npgsql keepalive cadence **1 second**; the upstream loss token observes connection state
changes. Multiplexing disabled: one locking session per held/waiting acquisition,
bounded by that pool. A blocked pool/acquisition must cancel rather than report busy.
This retains the baseline occupancy; no reduction/performance claim. Session advisory
locks only (the library's connection overload), using `PostgresAdvisoryLockKey(long)` and the exact
UTF-8 FNV-1a signed 64-bit mapping. No table, Redis backend or two-int namespace.

DL-6 rejected the initial library-owned connection/keepalive configuration. Its active
loss monitor uses `pg_sleep` for one-minute intervals; an established-monitor blackhole
failed the 15-second gate, and monitoring cancellation caused intermittent release-tail
latency failures. The chosen path opens one independent pooled Npgsql connection and
transfers it to ownership. The library sees an externally owned connection, tracks its
state-change loss token, and owns advisory SQL/acquisition/release. Npgsql's built-in
idle protocol keepalive owns detection with the five-second command timeout. Ownership
awaits library release then closes the session in finally, including error paths.
There is no custom keepalive timer, SQL, polling, or premature cancellation release.
This small amount of connection lifetime glue is an accepted maintenance tradeoff for
bounded loss detection; it does not capture EF or override configured encryption.

The library may throw on release after ownership loss while still disposing its session.
The adapter must record that failure; loss cancellation must remain visible to the caller.
Healthy repeated disposal is idempotent. Do not hide acquisition failures as contention.

## Evidence and thresholds declared before adapter selection

Focused executable: `tools/ConduitLLM.LockProbe`, reflection disabled, net10.0; a
nonzero exit means an assertion failed. It exercises raw legacy single-bigint SQL
against library sessions in both directions, acquisition/hold/release, distinct keys,
150ms contention wait, cancellation and isolated `pg_terminate_backend` loss.
The baseline measurement is the **legacy dedicated-session SQL path**, excluding EF
context-factory startup and lease timer allocation; do not present it as full-host latency.

Predeclared regression gates for matched warmed 100-sample workloads: acquisition/release
median and p95 <= legacy * 2 + 1ms; 16 simultaneous holders occupy 16 sessions and
maximum lock-pool occupancy <=32; no held advisory sessions after shutdown/teardown;
cooperative shutdown <=2s. Detected terminated-session loss <=10s on loopback; network
failure bound <=15s with five-second commands, subject to OS/TCP scheduling. Failures
of correctness/lifetime gates cannot be offset by favorable performance measurements.
Measure multiplexing separately before changing this dedicated-session decision.

DL-1 runtime lane: SDK 10.0.401, runtime 10.0.12, Windows x64, isolated PostgreSQL
16.15 and 17.11 containers. JIT and published win-x64 NativeAOT probes must both pass.
No warning suppressions or host reflection fallback. Linux native evidence belongs
in CI; this package-only probe intentionally excludes unrelated EF/Wolverine graphs.

Executed DL-1 gate (2026-10-05): JIT and published native passed on **both** versions,
zero build/publish warnings. JIT loss 6/7ms, native 1/2ms (PG16/17). Median/p95 in
microseconds, legacy -> library: JIT PG16 959.5/1193.6 -> 1143.9/1338.7;
PG17 1062.0/1660.2 -> 1162.1/1591.8. Native PG16 758.4/900.0 -> 818.0/925.1;
PG17 738.9/1785.5 -> 823.0/1106.2. These pass the predeclared latency gate.
Commands: `dotnet build tools/ConduitLLM.LockProbe -c Release`,
`dotnet run --project tools/ConduitLLM.LockProbe -c Release --no-build`,
`dotnet publish tools/ConduitLLM.LockProbe -c Release -r win-x64 -p:PublishAot=true
-o artifacts/lock-probe/native`, then run the executable, setting
`CONDUIT_LOCK_PROBE_POSTGRES` to each dedicated fixture. The initial sandboxed native
publish failed to launch the MSBuild task host; rerunning with native toolchain access
succeeded. This was an execution permission constraint, not a package diagnostic.

## Migration gates and retirement

During DL-2, the new concrete adapter was registered while legacy remained selected. A temporary
`LegacyDistributedLockProvider` bridges migrated callers until DL-6 evidence passes;
it retained old 30-minute lease semantics and could not supply loss notification.
It was removed in DL-7 after local and Linux evidence passed.
No dual-handle acquisition or destructive shadow execution. Cutover/rollback require
quiescing all critical triggers and draining in-flight work; stable IDs alone do not
make old expiring holders safe. Production deployment is outside this implementation.

DL-7 retires the 377-line PostgreSQL SQL/timer implementation, old retry/interface metadata,
unregistered Redis/in-memory alternatives (429 lines), and obsolete lease options.
Replace cleanup test's in-memory dependency with a test-only ownership fake. Preserve
Redis alerts, warming signals, leader election/fencing/identity and hosted orchestration.
Report net production, dead alternatives and tests separately, without a 2,200-line claim.

## Achieved epic gates

- [x] DL-1 #1400: caller matrix, minimal ownership contract, numeric identity and pinned package spike.
- [x] DL-2 #1401: adapter, bounded diagnostics, independent sessions and actual legacy/new interoperability.
- [x] DL-3 #1402: media request/shutdown/deadline/loss propagation and healthy full-operation ownership.
- [x] DL-4 #1403: sync conflict/cancellation, bounded terminal-status persistence and unchanged Redis alert deduplication.
- [x] DL-5 #1404: optional fallback/skip policies, cancellable cache overload and warming listener/shutdown cleanup.
- [x] DL-6 #1405: real PG16/17 + Redis, fault/resource bounds, Windows and Linux JIT/native, host analyzer/composition and repository guards.
- [x] DL-7 #1406: composition selection, obsolete interface/backends/options retirement and isolated drain/rollback policy check.

The [validation report](../operations/distributed-lock-validation.md) records measured
latency/resources, the rejected monitoring configuration and final maintenance delta.
The [runbook](../operations/distributed-lock-rollout.md) requires every-instance
quiescence and actual work drain for cutover **and** rollback. A retained previous
release artifact provides rollback; no permanent lease bridge/selection flag remains.
Deployment follows normal authorization and deployment-environment canary evidence.
