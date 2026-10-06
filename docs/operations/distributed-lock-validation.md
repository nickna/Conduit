# Distributed lock validation (#1399)

## Scope and reproduction

The isolated fixtures use PostgreSQL 16.15 (Compose major) and 17.11, Redis 7.4,
.NET SDK 10.0.401/runtime 10.0.12, Npgsql 10.0.3, DistributedLock.Postgres 1.3.1
and DistributedLock.Core 1.0.9. No production sessions, storage or notifications
are involved. Caller tests mock storage/SignalR while using real coordination.

Set `CONDUIT_LOCK_TEST_POSTGRES` and `CONDUIT_LOCK_PROBE_POSTGRES` to the isolated
Npgsql connection string; set `CONDUIT_LOCK_TEST_REDIS` and
`CONDUIT_CACHE_TEST_REDIS` to the isolated Redis endpoint. Without overrides the
integration collection owns disposable PostgreSQL 17 and Redis containers.

```powershell
dotnet test Tests/ConduitLLM.IntegrationTests -c Release --filter 'Component=DistributedLock'
dotnet test Tests/ConduitLLM.Tests -c Release --filter 'FullyQualifiedName~DistributedLock|FullyQualifiedName~PostgresLockIdentity|FullyQualifiedName~MediaCleanup|FullyQualifiedName~MediaDeletion|FullyQualifiedName~AdminMediaServiceSoftDelete|FullyQualifiedName~AdminVirtualKeyService|FullyQualifiedName~MetadataSyncLock|FullyQualifiedName~OpenRouterDriftDetection|FullyQualifiedName~BudgetAlertManager|FullyQualifiedName~DistributedCachePopulator|FullyQualifiedName~CoordinatedConnectionPoolWarmer|FullyQualifiedName~Architecture|FullyQualifiedName~HostApplicationCacheComposition|FullyQualifiedName~MediaLifecycleExtensions'
dotnet run --project tools/ConduitLLM.LockProbe -c Release
dotnet publish tools/ConduitLLM.LockProbe -c Release -r win-x64 -p:PublishAot=true -o artifacts/lock-probe/native
./artifacts/lock-probe/native/ConduitLLM.LockProbe.exe
./scripts/aot/aot-audit.ps1 -OutputDirectory artifacts/lock-probe/aot-audit
```

Linux CI uses `linux-x64` and the executable without `.exe`; its complete commands
are in `.github/workflows/distributed-lock.yml`. Both PostgreSQL majors run separate
jobs. Published probes compile the exact production adapter, interfaces, identity,
metrics and optional helper source with reflection disabled. This proves the lock
slice, rather than claiming a full-host native publish or unrelated infrastructure
parity. The host analyzer ratchet separately rebuilds Admin and Gateway.

## Executed local evidence before selection

- PostgreSQL 16 and 17: **18/18 integration tests each**. This includes actual
  legacy/new exclusion in both directions before legacy retirement; independent
  providers; EF-context churn; scaled healthy lifetime; immediate/waiting busy;
  waiting cancellation; release after exceptions; idempotent disposal; backend
  errors distinct from busy; and optional skip/fallback policies.
- **226 unit tests passed**, two Redis composition cases initially skipped;
  rerunning the expanded matrix with real Redis passed **233/233**, including all four
  host composition cases and drift cancellation tests. These cover
  media fail-closed/contention/cancellation, manual sync HTTP 409, scheduled loss,
  alert cancellation, cache coalescing and single execution, startup cleanup, FNV
  golden values (including UTF-8 and collision regression), architecture, and
  singleton resolution with scope validation. Final cutover repeats this matrix.
- Real session termination cancels media and metadata work, prevents another batch,
  records `Cancelled`, preserves cancellation through async disposal, and permits
  a successor. A slow alert send continues excluding competitors past its former
  five-second TTL even when it ignores cancellation.
- Real Redis: 32 concurrent alert checks produce one isolated SignalR send; the
  24-hour idempotency marker and four-hour cooldown remain. Leader/follower warming
  opens real PostgreSQL connections, emits one signal, unsubscribes on completion;
  terminated leader work emits no success signal.
- A fixture TCP proxy drops traffic in both directions while PostgreSQL remains
  connected. Loss is observed within the predeclared **15-second** bound on both
  versions after a barrier observes real Npgsql keepalive (test including disposal ~6s).
  Teardown closes proxy sessions and a
  successor acquires. Loss notification is asynchronous and does not fence work.
- Exhausting the **32-session** pool cancels a waiting connection acquisition
  rather than returning null. Releasing a holder permits reuse. Teardown leaves
  zero held advisory locks.
- Native publish: zero new warnings, no reflection fallback or suppression. Both
  host analyzer builds pass with **zero first-party IL2xxx/IL3xxx diagnostics**.

## Latency and resource measurements

Each measurement warms 20 iterations then records 100 acquisition/release samples.
The baseline is dedicated-session legacy SQL, excluding EF factory startup and old
timer allocation. Production uses a singleton as the hosts do. All elapsed values
include awaited release; the probe prints the five slowest acquisition/release
splits and returns nonzero on an unmet gate.

| Executed lane | Legacy median / p95 (us) | Production median / p95 (us) | Production loss | Shutdown of 16 holders |
| --- | ---: | ---: | ---: | ---: |
| Windows JIT, PG16 | 763.4 / 927.3 | 1622.4 / 1873.0 | 1006ms | 3ms |
| Windows JIT, PG17 | 796.6 / 939.5 | 1605.9 / 1732.3 | 1013ms | 4ms |
| Windows native, PG16 | 612.5 / 813.0 | 1321.8 / 1545.7 | 1017ms | 2ms |
| Windows native, PG17 | 612.5 / 703.7 | 1286.3 / 1413.1 | 1003ms | 2ms |
| Linux JIT, PG16 | 668.3 / 767.2 | 1482.9 / 1703.9 | 1000ms | 6ms |
| Linux JIT, PG17 | 450.5 / 490.3 | 953.3 / 1020.0 | 1000ms | 4ms |
| Linux native, PG16 | 478.8 / 557.1 | 1084.7 / 1160.6 | 999ms | 3ms |
| Linux native, PG17 | 328.3 / 362.7 | 767.6 / 866.3 | 999ms | 3ms |

These executions pass the declared median/p95 <= baseline * 2 + 1ms limits.
Linux evidence: [successful DL-6 matrix](https://github.com/nickna/Conduit/actions/runs/37413696384),
233/233 unit and 18/18 integration tests per version, JIT and native probes both pass.
The later logging guard correction removed two duplicate cancellation logs; it changed
no lock/keepalive behavior. Final composition selection runs the matrix again.
The adapter has more acquisition round trips for the externally owned session; it
passes the original thresholds, rather than claiming improved latency.

The **rejected** initial library-owned connection configuration observed Windows
p95 **2.75–3.50ms**, exceeding limits on some repetitions. Instrumentation located
the delay in upstream release (monitoring-query cancellation); registration/metrics
added microseconds. More critically, blackholing an already active `pg_sleep` monitor
failed the 15-second bound on Windows and Linux. Initial startup-only fault tests
did not establish that bound. DL-6 therefore changed the session ownership glue:
the adapter opens/transfers/closes one independent Npgsql connection, Npgsql built-in
idle keepalive detects loss, and upstream observes state-change notification while
still owning advisory acquisition/release. This adds no custom SQL/retry/timer and
preserves configured encryption. All four final local JIT/native gates pass; the
Linux matrix verifies the same exact production source before selection.

Measured dedicated-session occupancy: 16 concurrent holders = 16 locking sessions;
after 100 immediate busy attempts, **17 pooled connections**, below the 32 limit.
Cooperative teardown is below the **2-second** limit and leaves **zero held locks**.
There is no claimed reduction in connection occupancy; multiplexing remains disabled.

## Limits and warning baseline

Caller cancellation is cooperative. An already running uncancellable storage/Redis
operation can finish after loss; ownership is retained through healthy canceled work.
No exactly-once/fencing guarantee is introduced. A killed-session upstream release
can throw while closing its connection; the adapter counts/logs this and preserves
known-loss cancellation. Healthy release errors remain observable.

Existing warnings remain: Gateway/Admin nullable warnings, test CA2014 in Bedrock,
and the integration Testcontainers transitive SSH.NET 2023.0.0 NU1903 advisories.
No warning baseline is weakened. Native lock-slice evidence is separate from #1368
full-host boundaries. Fixture teardown owns its connections/containers even on
assertion failure; the developer's three externally supplied fixtures are removed
after the final verification.

## Final selection and maintenance outcome

DL-7 selects the singleton adapter in both hosts and removes the old interface,
PostgreSQL SQL/lease/retry implementation, temporary bridge, Redis/in-memory backends
and warming expiry option. No live consumers of those surfaces remain. Golden identity
tests are retained; the former automatic-expiry test is retired in favor of healthy
full-operation lifetime/context churn. A 47-line test-only fixture preserves frozen
legacy FNV + single-bigint SQL interoperability without production lease code.

Final local checks: **3,699 passed / 6 existing skips** using the repository unit filter
`FullyQualifiedName!~IntegrationTests&Category!=TimingSensitive` with isolated Redis
and PostgreSQL cache fixtures. Final PG16 and PG17 each pass **18/18** integration
tests, including two drain/rollback tests replacing two legacy implementation tests.
All four real host composition cases select the adapter with scope validation.
Admin's RDG guard generates all nine affected media handlers; exception logging guard
passes. Host analyzer ratchet remains zero first-party diagnostics.

Measured diff from the merged FusionCache `dev` baseline (`91d37d24`):

| Area | Added lines | Deleted lines | Net |
| --- | ---: | ---: | ---: |
| Production (`Shared`, `Services`, central package pin) | 632 | 1,224 | **-592** |
| Included unused Redis/in-memory alternatives | 0 | 429 | -429 |
| Remaining production adapter/business policy changes | 632 | 795 | -163 |
| Tests (including infrastructure and project reference) | 1,279 | 239 | +1,040 |

The adapter/identity/contracts/metrics/composition/cancellation helper total 240 lines;
caller policies and local cache coalescing remain. The 377-line PostgreSQL backend and
106-line old contract were retired. Documentation, the 290-line native probe/project,
and CI workflow are reported separately from production reduction; no 2,200-line claim.
Redis leader election/fencing/identity implementations have no diff. The
[runbook](distributed-lock-rollout.md) records isolated policy-drill evidence and
the remaining deployment-environment ingress/scheduler/canary verification.
