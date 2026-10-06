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

