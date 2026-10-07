# Webhook delivery

Implementation and verification for epic #1419 are delivered in separate commits.
Deploy the complete epic together: HTTP policy removal relies on the durable
receiver policy, claims, and recovery added by the remaining increments.

## Receiver HTTP contract (WR-1)

Each dispatch performs one POST through HttpClientFactory. Automatic redirects,
HTTP retries, the second HTTP circuit breaker, and cookie persistence are disabled.
Treat a redirect as the receiver's actual non-success response. Configure the final
callback URL directly. Receiver cookies are never shared between callback owners.
Custom request headers and existing JSON, X-Webhook-Type, and X-Webhook-Timestamp
behavior are preserved for image/video terminal callbacks and video progress.

`Webhooks:Delivery:AttemptTimeoutSeconds` defaults to 10 (allowed 1–300).
`Webhooks:Delivery:ConnectTimeoutSeconds` defaults to 5 and must be between 1 and
the attempt timeout. Invalid options fail startup. Custom sender timeouts may be
between zero (exclusive) and 300 seconds. Caller cancellation propagates to the
worker; receiver timeouts return a distinct retryable outcome. Response-header
completion and immediate disposal avoid buffering arbitrary receiver bodies.
The response header limit is 64 KiB. Actual 2xx/other response codes are retained.
Retry-After delta-seconds and HTTP dates are returned to the delivery policy;
malformed values are ignored. Diagnostics exclude receiver reason phrases, raw
exception messages, callback URLs, and credentials.

## Validation record

Initial checkout: `7b3a471d`, .NET SDK 10.0.401, Windows x64, Docker Desktop Linux
engine. Existing webhook-focused baseline: 50 passing tests. WR-1 regression suite:
111 passing tests including cookie isolation.

The same production-registration loopback test sent 20 sequential healthy 202
callbacks followed by one unavailable 503 callback. Before WR-1: 20 healthy POSTs
in 351.59 ms, then **4 POSTs in 14,018.07 ms** for the unavailable callback. After
WR-1: 20 healthy POSTs in 5.21 ms, then **1 POST in 0.19 ms** for the unavailable
callback. These are small diagnostic samples with different warmup effects, not a
healthy-path throughput comparison. They establish removal of hidden retry
amplification. The WR-7 gate below measures database-backed delivery, restart,
one/two instances, mixed destinations, resource use, and latency distributions.

Reproduce the HTTP regression suite with:

```powershell
dotnet test Tests/ConduitLLM.Tests/ConduitLLM.Tests.csproj -m:2 --filter 'FullyQualifiedName~WebhookHttpTests'
```

## Durable identity and claim foundation (WR-3)

The additive `AddDurableWebhookDeliveries` migration creates indexed PostgreSQL
receipts. The existing domain `EventId` is the logical identity; it survives retry
and operator replay, while different progress occurrences have different IDs.
Receipt scope hashes that ID, the owner, and the immutable callback URL. Wolverine
envelope IDs are excluded. `X-Webhook-Id` is reserved and replaces any custom header
of the same name, case insensitively. Legacy queued messages retain their original
EventId, Timestamp, RetryCount, and NextRetryAt; new cycle fields default to zero/null.

The adapter atomically claims a record and persists a Wolverine lease-recovery
message. Concurrent workers cannot acquire an unexpired claim. Starting an actual
attempt persists its count before sending; success and scheduling are fenced by
the claim token and expiry. The saved request snapshot remains authoritative for
retries. Scheduling updates delivery state and Wolverine's scheduled inbox in one
transaction. A failed scheduling write leaves the previous claim/recovery message
intact. Receipts and payloads initially retain until 30 days after the delivery
deadline; the recovery/retention increment supplies bounded operator purge.

Three real PostgreSQL tests pass for two-instance exclusion with no Redis,
expired-owner fencing, and scheduling-write rollback. Four identity/legacy-format
unit tests pass. The checked-in native EF model is regenerated. Consumer adoption
and receiver-visible identity are integrated with WR-2 before deployment.

## Durable receiver policy (WR-2)

The consumer now uses PostgreSQL claims and receipts. It returns immediately for
delivered, actively claimed, future-due, and obsolete-cycle work. It sends the
reserved X-Webhook-Id alongside the saved custom headers. A committed receipt
suppresses sends even if subsequent SignalR or circuit reporting fails.

Options under `Webhooks:Delivery`:

| Option | Default | Meaning |
| --- | ---: | --- |
| MaxAttempts | 100 | Maximum reserved actual send attempts per cycle |
| TerminalWindowSeconds | 86400 | Terminal notification delivery window |
| ProgressWindowSeconds | 300 | Started/progress notification freshness |
| InitialDelaySeconds | 2 | First retry backoff |
| MaxDelaySeconds | 3600 | Capped exponential backoff |
| JitterRatio | 0.2 | Symmetric backoff jitter |
| MaxRetryAfterSeconds | 3600 | Maximum receiver-requested wait |
| DeferralSeconds | 30 | Circuit/admission deferral fallback |

2xx succeeds. Network failures, receiver timeouts, 408, 429, 500, 502, 503, and 504
retry. Other statuses (including redirects, authentication failures, 501, and 505)
exhaust deliberately. Invalid URLs/JSON and oversized payloads exhaust without a
send. The legacy null-payload fallback remains compatible. Retry-After cannot
shorten the normal backoff, and all retry/deferral times are capped at the deadline.
Open circuits durably defer without consuming a send attempt. No receiver backoff
sleeps in a worker. Terminal exhaustion retains the NonRetryableMessageException
path to Wolverine error storage and the endpoint's `Retry: null` protection.

Counts are reserved immediately before the send. A crash between that write and
HTTP can consume a slot without a POST; this conservatively prevents exceeding the
budget after restart. A crash after remote acceptance and before the receipt can
redeliver. Delivery is at least once: receivers should deduplicate X-Webhook-Id.
The lease recovery envelope also protects work when a schedule/receipt write or
shutdown interrupts handling. Optional notifications never control scheduling.

Validation: 103 webhook/policy/endpoint tests pass. Five real PostgreSQL tests pass,
including a 15-second outage with a pending retry across host restart and two hosts
processing duplicate events through the production static bridge, queue policy,
and HTTP registration. The receiver observes one active POST for duplicates and
stable IDs/custom headers on retry. A SignalR failure after success causes no resend.

## Atomic terminal media callbacks (WR-4)

Gateway terminal writers lock the authoritative task, check its version and worker
lease, and commit the terminal result and Wolverine callback envelope together.
Identity derives from the task and committed execution version. Concurrent or
repeated terminal writes cannot replace an outcome or create another callback.
Callback recovery sends the saved intent without invoking a provider or spending.
Cache, SignalR, and provider-error reporting run after the authoritative commit;
their failures cannot manufacture a failed callback after completion.

Running image/video Completed, Failed, and Cancelled callbacks retain their existing
payload contracts, original headers, destination, owner, and correlation. API/event
cancellation reconstructs the running task's callback from accepted metadata if it
wins the orchestrator race. Pending cancellation, timeout, and Indeterminate paths
did not promise callbacks and retain that behavior. Unknown provider outcomes remain
Indeterminate with automatic generation retry disabled. Late progress cannot reopen
a terminal task or cache a rejected optimistic update (issue #1446).

No historical terminal rows are automatically notified: their missing intent cannot
be distinguished safely from an already delivered legacy callback. Roll out this
writer for new transitions after the receipt migration. Any legacy reconciliation
requires independently identified missing intents and an explicit bounded window.

Verification covers concurrent terminal writers for all six image/video outcomes,
outbox-write rollback, dispatch after host restart, cache/notification failure,
cancellation races, stale worker fencing, and delayed progress. Existing durable
acceptance/recovery tests also verify provider markers and spend safeguards.

## Async admission and circuit isolation (WR-5)

`GlobalConcurrency` defaults to 32 and `DestinationConcurrency` to 8. With Redis,
these are aggregate limits across instances sharing the admission namespace; a
process-local cap also applies. Without Redis (or during an outage), limits and
circuits are local: N instances can admit up to N times each configured limit.
PostgreSQL claims, receipts, and scheduled retries remain authoritative in both
modes. Optional Redis failures never remove durable work.

Destination scope is SHA-256 of the exact accepted URL, including path/query; no
URL or authentication data appears in Redis keys. Five retryable failures open the
destination circuit for 60 seconds (`CircuitFailureThreshold`, `CircuitOpenSeconds`).
Afterward `RecoveryProbes` (default 1) bounds simultaneous probes across instances.
Leases expire after attempt timeout plus 30 seconds, including after process exit.
Atomic async Redis scripts use Redis server time and generation fencing. Late or
expired results cannot close/reopen a newer circuit. Success of an ordinary older
request does not erase the open circuit. Unused Redis state expires in 30 minutes;
local destination state is pruned after 30 minutes and capped at 4096 entries.

Admission never waits for capacity. Denial durably schedules the event and returns
the worker slot, with no HTTP attempt. Per-destination capacity leaves slots for
other receivers. Capacity denial uses `CapacityDeferralMilliseconds` (default 250,
10–60000); an open circuit uses its remaining open interval. The longer
`DeferralSeconds` (default 30) applies when the bounded local destination registry
is full. Separating capacity from circuit delays keeps healthy bursts from waiting
30 seconds for a short-lived HTTP slot. Gateway scans durable scheduled work every
250 ms so short capacity deferrals are not stretched by a multi-second scheduler
cadence. This adds up to four idle scheduled-work polls per second per Gateway.
Started/progress work retains its five-minute freshness deadline.
The receiver exception-count listener circuit is removed because exhausted receiver
events could otherwise pause the whole queue. Wolverine's infrastructure persistence
and recovery still handle transport/database faults. Historical `RateLimit` metadata
is not enforced by Wolverine and is not a throughput guarantee. Final concurrency
recommendations are checked against the WR-7 workload measurements below.

## Restricted recovery and retention (WR-6)

All routes below require Admin's `MasterKeyPolicy`. They expose safe receipt
metadata, never request JSON, callback URLs, custom headers, or payloads:

* `GET /v1/admin/webhook-deliveries/?owner=1&taskId=...&eventId=...&limit=50`
  filters by owner/task/event (maximum 100 rows).
* `GET /v1/admin/webhook-deliveries/backlog` returns retained state counts,
  reserved attempts, replay cycles, and oldest pending age.
* `GET /v1/admin/webhook-deliveries/dead-letters?limit=50` lists supported Wolverine
  error-envelope references linked to delivery records. Admin uses the configured
  Gateway durability schema (`Webhooks:GatewayDurabilitySchema`, default
  `wolverine_conduit_gateway`) through Wolverine 6.14's public persistence adapter.
  This query-only adapter never starts a Gateway listener or joins its node cluster.
* `POST /v1/admin/webhook-deliveries/{id}/replay` accepts JSON
  `{"operationId":"<new UUID>","virtualKeyId":1,"expectedCycle":0,"deadLetterId":null}`.
  Repair the receiver's configuration first; the saved URL/payload/headers remain
  immutable. Exhausted, retained records alone qualify. A fresh internal cycle and
  delivery deadline commit with the canonical outbox intent and authenticated actor
  audit. The receiver EventId stays unchanged. Repeat the same operation ID after an
  uncertain response. Concurrent calls or stale expected cycles cannot start extra
  cycles. Delivered events cannot be replayed. An optional matching dead-letter ID
  is discarded through the supported API after commit; cleanup failure is harmless.
* `POST /v1/admin/webhook-deliveries/purge?limit=100` deletes expired terminal
  receipts and replay audit in bounded batches (maximum 1000 each), plus at most 100
  expired webhook error envelopes using Wolverine's filtered discard API. Pending
  work is never purged. Run this operator command regularly; retention is eligibility
  for explicit purge, not an automatic background deletion promise.

`RetentionDays` defaults to 30 (1–365). Payloads and credentials in canonical receipt
snapshots remain until the delivery deadline plus retention; replay extends that
deadline. Audit stores actor, operation ID, cycle, time, prior attempts, and optional
error-envelope reference, with the same bounded retention. Protect database backups
and access accordingly. A master key shared by operators has actor `master-key`;
identity claims, when available, provide individual attribution. No bulk replay is
provided. Error cleanup and application replay have separate transactions; the
canonical cycle guard makes old error-envelope redelivery harmless.

HTTP telemetry is sourced only from `WebhookMetricsHandler`:
`conduit_webhook_requests_total{status}`, `conduit_webhook_duration_ms{status}`, and
`conduit_webhook_active_requests`. Status labels contain numeric HTTP codes or
`error`/`cancelled`, never URL/owner/task labels. `conduit_webhook_scheduled_total`
distinguishes `admission` deferrals from `receiver_retry`. The notification service
refreshes receipt state, oldest age, replay, and reservation gauges each minute;
use max across replicas for shared-state gauges and sum rates for process HTTP
counters. Alert on growing pending age/count, exhaustion, and sustained receiver
failure rates. Reserved attempts conservatively include a crash-before-send slot;
they are explicitly distinct from measured HTTP invocations.

SignalR event names, payload shapes, and actual receiver statuses remain. Statistics
now report the authoritative retained delivery snapshot (`period: retained`), not
overlapping Redis attempt counters or fabricated no-Redis zeros. Response latency
comes from the HTTP histogram; legacy statistics response-time fields remain zero
and URL statistics empty because the receipt snapshot does not measure them.
Metrics and SignalR cannot control delivery scheduling or resend a committed success.

The no-caller audit removed the batch timer/queue, Redis/cached/no-op receipt trackers,
logging-only delivery service, old synchronous circuits, and duplicate Redis metrics
writers. Legacy expiring circuit JSON remains readable for serialization compatibility.

## Deployment and rollback

1. Back up the database and use the release migrator to apply the additive
   `20261006230550_AddDurableWebhookDeliveries` and
   `20261006232832_AddWebhookReplayAudit` migrations. Keep Wolverine's shared
   transport schema and separate Admin/Gateway durability schemas. Regenerated
   static handlers and the native EF model ship with this release.
2. Stop or drain the previous Gateway workers before enabling the new workers.
   Deploy the complete implementation together. Previous workers do not consult
   these receipts and must not compete with new workers for callbacks. Deploy Admin
   recovery endpoints with the matching Gateway durability schema setting.
3. Resume workers and inspect retained backlog/oldest age, actual HTTP statuses,
   POST rates, exhaustion, and admission deferrals. Verify one authenticated callback
   against a controlled receiver before expanding a canary. Native images retain
   the repository's canary-only promotion status.

Legacy scheduled messages keep their original logical EventId and timestamp;
missing owner/cycle/start fields default to zero/zero/null. The original timestamp
starts their freshness window, and legacy RetryCount conservatively consumes the
attempt budget. Old progress beyond five minutes expires without POSTing. Old
terminal work outside the delivery window becomes inspectable exhaustion, rather
than silently receiving a fresh unlimited window. Historical terminal tasks are
never blindly backfilled: their receiver may already have accepted a callback.

For rollback, stop new workers, snapshot receipt/replay state and queued/scheduled
work, and retain the additive tables. Avoid running an older worker over active
replay cycles: it does not enforce the cycle fence or receipt. Resolve/drain the
active callback backlog with this version before a coordinated application
rollback. Restoring the previous application also restores its earlier HTTP retry
and terminal intent behavior; removing the new tables does not make that safe.

Use `X-API-Key: <master-key>` for Admin operator calls. The existing outer Admin
security middleware rejects Bearer-only requests despite authentication-handler
support; that separate issue is tracked in [#1448](https://github.com/nickna/Conduit/issues/1448).

## Combined reliability gate (WR-7)

The integration suite uses disposable PostgreSQL 17 databases, real Wolverine
outbox/inbox persistence and production static bridges, the production
`CoreMessagingJsonContext`, and a controllable Kestrel HTTP receiver. Redis cases
use Redis 7.4; other cases deliberately omit it. Process tests kill a separate
`MediaDispatchProbe` executable, rather than simulating a restart with mock calls.

Coverage includes image/video termination on both sides of the terminal
transaction commit; process death after retry commit and before acknowledgment;
claim-owner death before send and after receiver acceptance; expired leases and
late owners; duplicate events on two hosts; 25 expired progress events mixed with
a valid final event; unavailable admission and bounded Redis probes; optional
notification failure; actual status codes, Retry-After, connection reset, timeout,
redirects, cookies, and oversized/slow response bodies. Accepted image/video jobs
also traverse their production orchestrators through real HTTP callbacks, with
custom headers, exactly one provider call and one idempotent debit.

The acceptance fault test now injects transport failures into the actual
`wolverine_queues` schema (#1449). It ages the dead publisher's heartbeat only in
the isolated fixture to avoid a 60-second control-command timeout; Wolverine must
still recover the untouched persisted outgoing envelope. Source-generated direct
and legacy video messages are separately round-tripped (#1450).

Reproduce from a Windows checkout with Docker Desktop Linux containers:

```powershell
dotnet test Tests/ConduitLLM.Tests/ConduitLLM.Tests.csproj -m:2 --filter 'FullyQualifiedName~Webhook|FullyQualifiedName~GenerationOrchestrator|FullyQualifiedName~HybridAsyncTask|FullyQualifiedName~MediaGenerationEventContract|FullyQualifiedName~WolverineEndpointPolicy|FullyQualifiedName~MasterKeyAuthentication'
dotnet test Tests/ConduitLLM.SerializationTests/ConduitLLM.SerializationTests.csproj -m:2
dotnet test Tests/ConduitLLM.IntegrationTests/ConduitLLM.IntegrationTests.csproj -m:2 --filter 'FullyQualifiedName~Webhook|FullyQualifiedName~MediaDurable|FullyQualifiedName~MediaTerminal'
pwsh -File scripts/generate-wolverine-code.ps1 -Verify
pwsh -File scripts/aot/aot-audit.ps1
```

The native gate additionally starts the actual Linux x64 Gateway and Admin
executables against PostgreSQL/Redis. It verifies authenticated HTTP delivery,
retained inspection, Wolverine's public dead-letter adapter, restricted replay,
and the unchanged receiver event ID. Build current images before opting in:

```powershell
docker build -f Services/ConduitLLM.Gateway/Dockerfile.native -t conduit-http-native:epic-1419 .
docker build -f Services/ConduitLLM.Admin/Dockerfile.native -t conduit-admin-native:epic-1419 .
$env:CONDUIT_WEBHOOK_NATIVE_TEST = '1'
dotnet test Tests/ConduitLLM.IntegrationTests/ConduitLLM.IntegrationTests.csproj --filter 'FullyQualifiedName~WebhookNativeRuntimeTests' -m:2
```

The default integration run explicitly skips this image-dependent case. Analyzer
builds and real native publishes are separate gates: existing first-party linker
warnings remain subject to `scripts/aot/linker-warning-baseline.json`; successful
publication does not mean zero linker warnings or general native promotion.

## Matched HTTP workload

The runner compiles the same harness against archived baseline `7b3a471d` and this
implementation. Each runs one and two Wolverine hosts, 75 queue workers per host,
20 healthy warmup events, then a 200-event burst observed for ten seconds. Payloads
have 1024 bytes of JSON padding. Four healthy destination URLs return 202 after
10 ms. The outage case offers 160 events to one destination returning 503 after
200 ms, then 40 healthy events. Both implementations use identical queue and
scheduled polling (250 ms); current production Gateway uses that scheduled cadence.
The baseline also receives this faster cadence, rather than benefiting the new
implementation alone.
Auxiliary spend/statistics hosted services are excluded; the production webhook
consumer, HTTP sender, queue policy, and current receipt adapter remain in the path.

Final results (milliseconds, rounded; outage offers contain 40 healthy events):

| Hosts | Workload | Version | Delivered | Useful/s | POSTs | Healthy first p95 | Success p95 | Healthy drain |
|---:|---|---|---:|---:|---:|---:|---:|---:|
| 1 | healthy | before | 200 | 19.95 | 200 | 268 | 284 | 302 |
| 1 | outage | before | 0 | 0.00 | 225 | — | — | >10000 |
| 2 | healthy | before | 200 | 19.91 | 200 | 155 | 174 | 199 |
| 2 | outage | before | 24 | 2.40 | 435 | 61 | 74 | >10000 |
| 1 | healthy | new | 200 | 19.95 | 200 | 631 | 650 | 740 |
| 1 | outage | new | 40 | 3.99 | 48 | 279 | 296 | 314 |
| 2 | healthy | new | 200 | 19.95 | 200 | 460 | 476 | 561 |
| 2 | outage | new | 40 | 3.98 | 56 | 191 | 218 | 285 |

All four new cases pass the tolerances below. Healthy-only goodput is unchanged
at this offered rate; p95 increases by 365/301 ms for one/two hosts. Durable
claim/watchdog, attempt, and receipt writes plus destination admission explain
this measured cost. There is no healthy-path speedup claim. Under outage, all
healthy events now finish, rather than zero/24 of 40. The baseline two-host success
percentile includes only those 24 successes and excludes the 16 unfinished healthy
events; it cannot establish a better healthy-service latency bound.

Total outage POSTs fall from 225/435 to 48/56 (79%/87% lower). POSTs per offered
event are 1.125/2.175 before and 0.24/0.28 after. The 160 failed-destination events
remain durably pending behind the open circuit; these ratios reflect bounded
admission during the ten-second outage observation, not delivery or loss of those
events. Oldest pending work is approximately ten seconds in all outage cases.
Transport queue depth is zero at each final sample; raw pending counts are
200/176 before and 160/160 after, as scheduled and in-flight work lives elsewhere.

Resource observations for the same windows (MiB, aggregate managed process):

| Hosts | Workload | Version | CPU ms | Allocated MiB | Peak resident MiB | Peak thread-pool queue/threads | PostgreSQL calls |
|---:|---|---|---:|---:|---:|---:|---:|
| 1 | healthy | before | 3062 | 21.9 | 181.2 | 32/29 | 2532 |
| 1 | outage | before | 3219 | 12.7 | 187.6 | 61/29 | 2030 |
| 2 | healthy | before | 4000 | 22.2 | 199.7 | 33/34 | 3018 |
| 2 | outage | before | 1844 | 18.1 | 197.0 | 41/48 | 2735 |
| 1 | healthy | new | 4609 | 34.4 | 169.9 | 21/28 | 6439 |
| 1 | outage | new | 4828 | 30.1 | 182.4 | 15/40 | 5494 |
| 2 | healthy | new | 3688 | 32.9 | 193.9 | 20/43 | 6394 |
| 2 | outage | new | 2125 | 35.6 | 195.5 | 16/48 | 6488 |

PostgreSQL calls and allocations increase with authoritative receipts and atomic
scheduling. One-host CPU also increases; two-host CPU varies with background
durability activity. These are single matched samples, not confidence intervals.
Do not extrapolate their resource ratios or burst drain to fleet capacity.

Full p50/p95/p99 and resource observations are retained in
[baseline.json](webhook-validation/baseline.json),
[current.json](webhook-validation/current.json), and
[environment.json](webhook-validation/environment.json).

Hardware: Intel Core i7-14700T, 28 logical processors, Windows x64, .NET 10.0.401,
Docker Desktop Linux. PostgreSQL 17 Alpine has a two-CPU/two-GiB container limit
and `pg_stat_statements` enabled. Hosts and receiver share one managed process on
the host machine; two hosts exercise shared queue/receipt coordination, not two
independent machines. PostgreSQL calls count SQL statements for the full window;
CPU, allocation, resident memory and thread-pool samples cover the aggregate
managed process. Redis is deliberately absent (zero calls); shared Redis admission
and outage behavior are verified separately by the integration suite. Builds and
other fault tests do not run during measurement.

This is fixed-burst goodput, not a saturation or maximum-throughput claim. Reported
20 useful events/second for a healthy run is capped by 200 offers over ten seconds;
healthy drain time and latency reveal the meaningful burst behavior. First-attempt
latency starts before enqueueing and ends at receiver arrival. End-to-end latency
ends after the success receipt and notification callback; failed destinations have
no successful end-to-end observation. Raw reports include p50/p95/p99, healthy-only
first-attempt samples, transport depth, unfinished logical work/oldest age, POSTs,
resource samples, and database calls. Transport depth alone excludes scheduled and
in-flight work, so zero transport depth does not imply an empty logical backlog.

Acceptance tolerances for this asynchronous callback gate are: every healthy offer
completes within two seconds of enqueueing; healthy-only p95 is at most 1.5 seconds,
ten-second goodput at least 19.5/s (2.5% below offered rate), and exactly one POST per
healthy event; mixed-outage healthy work also drains within two seconds and total
POSTs stay below 100 for 200 offers. These absolute latency bounds allow the added
receipt/lease transactions and bounded destination admission while keeping callback
delay small relative to the five-minute freshness window. They do not permit lost
healthy work or retry amplification. The runner fails when these bounds are missed.

An initial four-slot destination configuration produced 1.47-second healthy p95
and a 2.04-second drain on one host. Eight destination slots use the existing
32-slot global cap more effectively across four healthy URLs, while a failing
destination can still occupy only a quarter of that cap. Capacity deferral is
250 ms rather than the original 30 seconds, with matching production scheduled
polling. The final comparison above validates those defaults; it does not assume
that durable receipts make the healthy path faster.

Reproduce with the same SDK and Docker resources:

```powershell
New-Item -ItemType Directory -Force artifacts/webhook-baseline | Out-Null
git archive -o artifacts/webhook-baseline.zip 7b3a471d
Expand-Archive artifacts/webhook-baseline.zip artifacts/webhook-baseline -Force
pwsh -File scripts/benchmarks/webhook-delivery.ps1
```

The runner accepts only the isolated `conduit_webhook_bench` database name, creates
and removes its own PostgreSQL container, and writes JSON to
`artifacts/webhook-benchmark`. Never substitute an application database. Repeat on
deployment hardware before increasing concurrency; instance scaling without Redis
also scales the fallback admission caps.

## Final verification and tracked follow-ups

The final gate passed 134 targeted unit/regression tests, 45 PostgreSQL/Redis/HTTP
and process-recovery cases, 14 source-generated serialization compatibility cases,
and the opt-in native Gateway/Admin case. The native case performs a real 503,
persisted retry to a permanent 400, restricted inspection/error lookup, and a 202
operator replay with the same receiver event ID. Both native images publish; both
export OpenAPI, including all five recovery routes and their request/response
schemas. Static handler generation has no drift. The analyzer audit reports zero
first-party diagnostics; real native linking retains 147 first-party warnings,
with no warning group above the existing linker baseline. All four new workload
acceptance cases pass.

The new Admin contract and WebAdmin-local types are regenerated. Contract
generation is byte-stable across two isolated runs, and the checked-in contracts
pass OpenAPI validation with the existing violation allowlist. Reproduce the
Admin contract update with `npm run generate:admin:offline` in `tools/openapi`,
then `npm run verify:offline` and `npm run validate:openapi`.

Separate existing issues found during verification remain tracked:

* [#1445](https://github.com/nickna/Conduit/issues/1445): Testcontainers pulls a
  vulnerable SSH.NET test dependency.
* [#1448](https://github.com/nickna/Conduit/issues/1448): Admin's outer middleware
  rejects Bearer master keys; use the verified X-API-Key operator path.
* [#1451](https://github.com/nickna/Conduit/issues/1451): the OpenAPI tooling
  lockfile has three high-severity vulnerable development packages.
* [#1452](https://github.com/nickna/Conduit/issues/1452): fresh Gateway audio
  multipart schema export loses model/language/prompt, also on the unmodified
  baseline and native export. Its existing accurate committed Gateway contract is
  retained. Full `generate:offline` currently exposes that unrelated drift;
  determinism verification compares fresh generations without replacing it.

