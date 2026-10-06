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
amplification. The final WR-7 gate must measure database-backed delivery, restart,
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

`GlobalConcurrency` defaults to 32 and `DestinationConcurrency` to 4. With Redis,
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
other receivers. Started/progress work retains its five-minute freshness deadline.
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

