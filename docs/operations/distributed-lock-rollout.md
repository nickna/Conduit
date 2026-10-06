# Distributed lock cutover and rollback

## Selection and configuration

Gateway `Program.Caching` and Admin `MediaLifecycleExtensions` both call
`AddConduitDistributedLocks`. It selects one singleton `PostgresDistributedLockProvider`
with no runtime backend flag or compatibility bridge. The configuration-owned
`DATABASE_URL` is parsed by `ConnectionStringManager`; no scoped EF context or
transaction owns a locking session. Pins: DistributedLock.Postgres 1.3.1 (MIT),
DistributedLock.Core 1.0.9, Npgsql 10.0.3, .NET 10.

Each ownership retains one independently opened Npgsql session until all protected
work completes/stops. Npgsql built-in keepalive is one second; connection/command
timeouts are five seconds. The separate pool has application name
`conduit-distributed-lock`, minimum 0, maximum **32 per process/connection string**.
Budget database capacity for both hosts and every replica, in addition to EF pools.
Library session advisory locks retain the UTF-8 FNV-1a signed-bigint namespace.
Provider/library multiplexing and transaction enlistment are disabled. Configured
TLS/GSS settings are preserved. A zero acquisition wait can still spend time opening
or waiting for a pooled connection; infrastructure timeouts propagate as errors.

Media and sync's former 30/15-minute leases are now operation cancellation deadlines.
Alerts/cache/warming use their existing request/shutdown cancellation. Deadline,
request, shutdown and detected loss cancel protected work; they do **not** release
a healthy session while uncancellable work continues. Loss is asynchronous, with
tested bounds of 10 seconds for isolated termination and 15 seconds for a blackhole
after active keepalive; it is not a fencing or exactly-once guarantee.

## Required drain for either direction

Retain the previous release artifact for rollback. The old lease implementation can
release while its operation continues; stable numeric identities do not make a rolling
mix safe. Do not run destructive shadow work or rely on lease expiry as proof of drain.

1. Quiesce **every instance**: block manual cleanup, hard delete, restore, virtual-key
   deletion and metadata-sync triggers at ingress; disable media/sync schedules;
   pause producers that invoke guarded budget alerts. Prevent new replicas/startup
   warming and pause optional discovery/cache population if shared work can overlap.
   Record which triggers were paused and their previous settings.
2. Drain requests, guarded jobs and startup warmers on both hosts. Cancel cooperatively
   if needed, then await their actual completion. A canceled request or empty
   `pg_locks` result alone is insufficient for an old worker whose lease expired.
   Check job/run terminal states and actual task/process completion. If uncooperative
   work cannot finish within the shutdown budget, keep triggers quiesced and stop
   the owning process; confirm it has exited before continuing.
3. With all old/new protected work stopped, replace **both** host versions/selection
   across the cluster. Keep triggers paused while verifying startup, pool bounds and
   session ownership. For rollback, restart the retained old artifact only after the
   same drain; its former TTL limitations return.
4. Resume a single staging/canary trigger. Verify guarded completion and contention
   policy, then restore the recorded producer/scheduler/ingress settings. Resume
   other instances only after the evidence below passes.

Do not kill a database connection as a production drain technique: external work may
continue after session loss. PostgreSQL diagnostic queries help count sessions and
holders; the operator must also establish that protected work has stopped.

## Staging/canary evidence and rollback triggers

Use disposable databases and mocked/sandbox storage/notification destinations for
fault injection. The [validation report](distributed-lock-validation.md) supplies
commands, measured bounds and the automated PostgreSQL 16/17 JIT/native matrix.
`DistributedLockCutoverTests` uses real legacy SQL and modern ownership to demonstrate
that an expired legacy session permits an unsafe successor until work is drained,
and that canceled uncooperative modern work excludes rollback holders until awaited.
It validates the policy barriers, not deployment-system ingress/scheduler controls.

Before resuming production, repeat in the deployment environment:

| Evidence | Pass condition / rollback trigger |
| --- | --- |
| Same-key exclusion and identity | Two instances cannot enter the same guarded work; distinct keys proceed. Any overlap stops rollout. |
| Completion and cancellation | Media dry-run/approval/budget/tombstone/audit behavior remains; canceled runs never report successful completion or start another batch. |
| Busy policy | Media fails closed; manual sync returns 409; scheduled sync/discovery skip; cache population follows its optional fallback; alerts skip the threshold. |
| Backend outage and loss | No unguarded critical side effect; loss cancels further work within measured bounds. Optional fallback matches policy. Unexpected continued work stops rollout. |
| Alerts and warming | Real Redis marker/cooldown still deduplicate; failed/lost warming emits no success signal; subscriptions clean up. |
| Resource and latency | Lock pool <=32/process, one session/holder, no held locks after drain; matched warmed median/p95 <= legacy *2 +1ms. Sustained exhaustion or unmet limits stops rollout. |
| Shutdown | Cooperative 16-holder release <=2s in probe; host exits/drains within its configured budget. No early release while healthy work continues. |

Observe `conduit_distributed_lock_acquisitions_total{operation,outcome}` (acquired,
busy, canceled, error), `conduit_distributed_lock_wait_seconds`,
`conduit_distributed_lock_hold_seconds`, `conduit_distributed_lock_losses_total` and
`conduit_distributed_lock_release_failures_total`. Labels are bounded operation groups,
never virtual-key IDs. Compare busy/error rates and hold tails with workload volume.
Inspect `pg_stat_activity` filtered by application name and `pg_locks` for advisory
holders; pooled idle connections can remain after release until Npgsql prunes them.
Release failure is logged/counted. Known-loss release preserves cancellation; a
healthy-release failure propagates and requires investigation.

A failed canary keeps triggers quiesced and follows the complete drain/rollback steps.
This implementation and its isolated verification do not authorize deployment.
Redis leader election, fencing, identity and hosted-service orchestration remain
separate from these operation locks.
