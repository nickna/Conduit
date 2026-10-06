# Durable async media dispatch

Async image/video acceptance uses `IMediaTaskSubmission`. `MediaTaskSubmission`
opens a connection from Wolverine's PostgreSQL message database, begins one ADO.NET
transaction, inserts `AsyncTasks`, and enlists a fresh `MessageContext` with
`DatabaseEnvelopeTransaction` from the pinned Wolverine 6.14.0 package. Publishing
the full generation command writes its outgoing envelope through that exact
connection/transaction. The task ID is assigned before serialization. Both writes
commit before the endpoint can return its existing 202 response.

The separately created repository DbContexts and automatic handler transactions
do not participate in this operation. No EF Core outbox package, Wolverine upgrade,
application queue, recurring scheduler, saga, or schema migration is required.
Typed Npgsql parameters and existing source-generated JSON contexts keep this new
path free of runtime query compilation and reflection-based JSON serialization.

After commit, immediate envelope delivery and task-created notifications are
best-effort. Delivery failure leaves Wolverine's durable envelope for its existing
durability agents. Task status is read through the existing cache/database service;
submission does not install a stale Pending cache entry over a worker's newer state.
Cache or notification availability cannot invalidate durable acceptance.

`ConduitLLM.Media.Dispatch` reports `media_task_acceptances` with `task_type` and
`outcome` (`accepted`, `commit_unconfirmed`, `dispatch_deferred`, `notification_failed`).
Inspect Wolverine's existing message-store health counts and dead-letter diagnostics
when delivery is delayed. Logs identify tasks without logging keys or request payloads.

An unconfirmed commit produces an error, never a fabricated 202 or an automatic
submission retry. The task ID is included in the server's commit-outcome diagnostic.
PostgreSQL may have committed despite a lost confirmation/HTTP response, in which
case the durable command will execute. These endpoints did not support a client
idempotency key before this change; repeating an HTTP request can create another
task. Task-ID claims and billing idempotency protect duplicate **command delivery**,
not independent submissions. Operators should investigate the recorded task ID
before resubmitting after an unknown outcome.

Production async acceptance requires PostgreSQL-backed Wolverine storage. The
development-only InMemory transport fails closed for this operation because it
cannot provide the requested durability guarantee.

Deploy using the existing database migrator and Wolverine message-store schema
provisioning. The application and message schemas must be in the same database and
the message-store connection must have access to `AsyncTasks`. Regenerate/verify
the committed static adapters with `scripts/generate-wolverine-code.ps1`; submission
uses existing image/video message contracts and changes no handler signatures.
Run `scripts/aot/aot-audit.ps1` for the existing native analyzer profile. Full native
runtime parity remains under #1374; this epic does not broaden that profile.

The required `Durable media dispatch` CI job runs:

```powershell
dotnet test Tests/ConduitLLM.IntegrationTests/ConduitLLM.IntegrationTests.csproj --configuration Release --filter 'Component=MediaDispatch'
```

The suite migrates a real PostgreSQL 17 container and uses the pinned Wolverine
runtime, committed Gateway bridge adapters, actual task repositories and image/video
orchestrators, a fake external provider, and the actual idempotent billing repository.
It terminates the publisher subprocess without shutdown, restarts message handling,
forces outgoing-envelope rollback and transport publication failure, and verifies
full payload preservation, resumed generation, one invocation/debit, and optional
cache/notification failure. No live provider credentials or charges are needed.

## Automatic recovery

The existing one-minute `MediaTaskLeaseRecoveryService` now calls `IMediaTaskRecovery`.
Each pass selects at most 100 non-archived image/video rows with PostgreSQL
`FOR UPDATE SKIP LOCKED`: expired Processing leases and Pending rows untouched for
two minutes whose scheduled retry is due. Row locks serialize recovery with provider
markers, claims, cancellation, and another Gateway's sweep. Resetting ownership/state
and inserting replacement envelopes use the same PostgreSQL transaction as acceptance.
The two-minute Pending grace period limits duplicate dispatch while an original command
is queued. `UpdatedAt` records reconciliation and bounds repeat redispatch; task-ID
claims remain the execution authority. Safe lease recovery does not consume a provider
retry attempt. There is no new timer or independent scheduling system.

Only tasks with no provider-start **or** completion marker are automatically replayed.
Marked outcomes become Indeterminate with automatic retry disabled. Terminal and
archived tasks remain excluded. Provider-start requires the current owner and an
unexpired lease and can succeed only once. An old worker that loses this check exits
without invoking the provider, overwriting the new owner's result, or releasing the
task-ID reservation now used by its replacement. Claim cache invalidation is best-effort.
Local cancellation registration also follows the durable claim: replacement executions
install their own source, and stale executions can unregister only their own source.
Failure/cancellation finalization first renews the current owner's lease atomically;
an execution that lost ownership leaves the replacement's state and reservation alone.
Persisted cancellation classifies an unfinished provider invocation as Indeterminate
before clearing its lease, even if the worker has not unwound yet. A pre-provider
cancellation remains Cancelled and releases its unspent reservation; neither state is
automatically replayed.

Recovery and operator-approved retries share `MediaGenerationCommandReconstruction`,
including legacy image commands with blank task IDs and legacy video request metadata.
No model/prompt or authorization data is guessed. Missing/malformed request or key
metadata leaves the original state and records `Dispatch recovery blocked:` in `Error`.
Updating its timestamp moves the blocked row to the back of the bounded scan so it does
not permanently exclude later eligible work. The next eligible scan rechecks repaired
metadata. Unsupported non-media task types are outside this recovery policy.

Monitor `media_task_dispatch_recoveries` in `ConduitLLM.Media.Dispatch`, tagged by
`outcome` (`redispatched`, `indeterminate`, `blocked`), the existing safe-recovery and
Indeterminate counters, and Wolverine outgoing/dead-letter counts. The periodic worker
logs aggregate redispatch/repair counts. Task IDs and types are logged; keys and request
payloads are not.

To verify a rollout, inspect old Pending rows and expired leases in `AsyncTasks`, then
confirm an eligible task reaches Processing/Completed and has one billing ledger entry.
Check blocked tasks through the operator task listing or the persisted `Error` field;
repair the complete stored request/authorization metadata using the original source,
or cancel the task if that source is unavailable. Indeterminate tasks require #1328's
operator/provider reconciliation. Never clear provider markers merely because a task
is old. Existing PostgreSQL indexes bound the scan; no new application or messaging
schema migration is needed, and old eligible Pending rows are reconciled on startup.

The integration suite also kills a claimant, consumes its real Wolverine redelivery
before lease expiry, kills a recovery subprocess after reset while envelope insertion
is blocked, and verifies execution after restart. Concurrent sweeps/two worker hosts,
stale owners, malformed historical rows, cancellation/terminal exclusion, and crashes
after the provider marker are checked against PostgreSQL and the real billing ledger.
