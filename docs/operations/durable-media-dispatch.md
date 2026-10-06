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
