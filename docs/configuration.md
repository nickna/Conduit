# Configuration

*Audience: operators deploying and tuning Conduit. This doc explains **where** configuration lives
and the shape of each area; it is not an exhaustive list of every setting. For the objects being
configured, see [Core concepts](./concepts.md).*

Conduit's configuration lives in two places, and knowing which is which is most of the job:

| | Deploy-time configuration | Runtime configuration |
|---|---|---|
| Lives in | The **environment** (env vars / secrets) | The **database**, edited through the Admin UI |
| Set by | Your deployment / container platform | Operators, live, in WebAdmin |
| Changes take effect | On restart | Immediately, no redeploy |
| Examples | Database URL, keys, storage, migration readiness mode | Providers, virtual keys, model mappings, costs, routing policies |

The rule of thumb: **infrastructure and secrets are deploy-time; everything about how Conduit routes,
prices, and gates requests is runtime.**

## Deploy-time configuration (the environment)

Conduit ships **no `appsettings.json`** — every process setting is read from environment variables.
Hierarchical settings use a **double underscore** as the section separator, so a setting documented
as `Section:Sub:Value` is set as `Section__Sub__Value` in the environment.

The authoritative, current list of variables is **`.env.example`** in the repo root — it is kept in
step with the code, and copying it is the intended way to start a deployment. What follows is a map
of the areas, not a copy of that file:

- **Authentication and secrets** — the single backend/master key that lets WebAdmin and internal
  services reach the Admin and Gateway APIs, and a separate key that guards the health endpoints
  (see [Monitoring](./monitoring.md#securing-the-health-endpoints)). Human admins sign in to WebAdmin
  through Clerk, configured with its own keys.
- **Database** — a single **PostgreSQL** connection URL (`DATABASE_URL`) is required; Conduit is
  Postgres-only. Gateway and Admin startup never changes the schema. Run the Admin image's explicit
  `migrate` command before rolling out either service; the default `CONDUIT_MIGRATION_MODE=Wait`
  keeps readiness down until that command has brought the schema current. See the
  [migration deployment strategy](./operations/deployment/migration-deployment-strategy.md).
- **Customer mode** — `CONDUIT_CUSTOMER_MODE` decides how much provider error detail customers see
  in every customer-facing emission (HTTP error responses, SSE error events, async task status,
  webhook payloads, SignalR failure events). `External` (the default) returns classified generic
  messages — a rate limit reads as "the model provider rate-limited this request" with no provider
  name, raw text, or billing/key state — and is the right mode for public or paid deployments.
  `Internal` adds the provider name, upstream HTTP status, and the redacted raw provider message
  (plus a structured `error.metadata.provider_error` object) for teams running Conduit as their own
  router. HTTP status codes are identical in both modes. Set it on **both** the Gateway and Admin
  services — the Admin only uses it to report the active mode on WebAdmin's System Information page.
- **Redis** — **required for the normal Gateway runtime, including single-node deployments**.
  Set `REDIS_URL` or `CONDUIT_REDIS_CONNECTION_STRING`; missing configuration fails startup before
  the runtime service graph is constructed. Redis backs distributed caching, rate limiting,
  spending coordination, the real-time backplane, and ephemeral keys. Admin can run without Redis,
  with Redis-backed provider-error monitoring unavailable. Infrastructure-free OpenAPI export and
  isolated application-cache composition do not require Redis. Redis 7.4 or newer enables
  per-connection field TTLs for SignalR monitoring; older supported servers use periodic stale-connection
  cleanup. The bundled Compose deployment pins Redis 7.4.2. Set
  `SignalR__ConnectionMonitor__EnableHashFieldExpiration=false` to force the compatibility path.
- **Messaging** — Conduit's internal events run on **Wolverine over PostgreSQL** (they reuse the
  database; there is no separate message broker to run). This replaced an earlier RabbitMQ-based
  transport — if you see RabbitMQ referenced in older material, it no longer applies.
- **Media storage** — generated media (images, audio, video) is stored in memory for local dev or in
  an **S3-compatible bucket** (AWS S3 or Cloudflare R2, auto-detected) for real deployments. Two
  cautions worth calling out: automatic **media cleanup runs only on the Admin service**, and it
  ships in **dry-run mode** — nothing is actually deleted until you turn dry-run off, so storage will
  grow unbounded until you do.
- **Security** — IP filtering, rate limiting, failed-auth banning, and security headers are each
  configurable per service (the Admin and Gateway APIs have their own prefixes). One setting is easy
  to miss: correct client-IP handling behind a load balancer or CDN requires enabling **trusted-proxy
  / forwarded-headers** support — it is **off by default**, and without it IP-based rules act on the
  proxy's address, not the caller's.
- **Rate limiting** — per-key and per-group ceilings on requests, tokens and concurrency are set at
  runtime through the Admin API, not by environment variable. Two deploy-time knobs matter:
  `CONDUIT_RATE_LIMIT_FAILURE_MODE` decides whether a Redis outage admits or rejects limited traffic
  (it admits by default), and the token-reservation defaults bound what a single uncapped request
  can hold. See [Rate limiting](./operations/rate-limiting.md).
- **Observability** — metrics are exported by prometheus-net on each service's `/metrics` endpoint
  and scraped by Prometheus; dashboards and alert routing live in the Grafana stack. Distributed
  tracing is **opt-in**: it starts only when both `Telemetry:TracingEnabled=true` and an explicit
  `Telemetry:OtlpEndpoint` are set, since there is no collector in the default deployment. Covered
  in [Monitoring](./monitoring.md).

> A number of older variables (legacy Redis, cache toggles, database-recreate escape hatches) are
> **deprecated or ignored** and log a warning on boot if set. Trust `.env.example` and the startup
> warnings over any list — including this one — for what is still live.

### Scheduled media lifecycle cleanup

The Admin service is the single owner of scheduled media cleanup. Each cycle acquires a PostgreSQL
distributed lock before running soft-delete purge, explicit expiration, storage reconciliation,
storage-quota eviction, and retention-policy cleanup, so a multi-instance deployment does not run the same cycle
concurrently. The Gateway only records media lifecycle metadata; it does not schedule cleanup.

Set `MediaLifecycle__Enabled=true` to start the scheduler and configure its polling interval with
`MediaLifecycle__ScheduleIntervalMinutes`. The configurable phases can be controlled independently with
`MediaLifecycle__EnableExpirationCleanup`, `MediaLifecycle__EnableReconciliation`,
`MediaLifecycle__EnableQuotaCleanup`, and `MediaLifecycle__EnableRetentionCleanup`. Cleanup
defaults to `MediaLifecycle__DryRunMode=true`;
set it to `false` only after reviewing the status endpoint and logs. All phases share
`MediaLifecycle__MonthlyDeleteBudget`, batch-size, rate-limit, and dry-run safeguards.

Permanent storage deletes reserve monthly budget before each sub-chunk. Configure the maximum
reservation with `MediaLifecycle__BudgetReservationStride` (10 by default). A Redis failure uses
`MediaLifecycle__BudgetFailureMode=FailClosed` by default; `FailOpen` is available when continuing
cleanup is more important than enforcing the provider allowance. Failures increment
`conduit_admin_media_cleanup_budget_store_failures_total`. The status page identifies Redis versus
the development-only in-memory counter; the latter resets on restart and is not shared.

Storage deletion uses S3 `DeleteObjects` requests and records a database deletion only for keys the
provider confirms. `MediaLifecycle__MaxBatchSize` groups up to 1,000 candidates before applying
`MediaLifecycle__DelayBetweenBatchesMs`; `BudgetReservationStride` remains the maximum size of each
storage request so the crash exposure never exceeds the reserved budget stride. HTTP 429/503 and
S3 `SlowDown` outcomes retry with exponential backoff controlled by
`MediaLifecycle__DeleteThrottleMaxRetries` and
`MediaLifecycle__DeleteThrottleInitialBackoffMs`. Each provider call is bounded by
`MediaLifecycle__R2OperationTimeoutSeconds`.

Cleanup candidate queries use `(CreatedAt, Id)` keyset pages and project only the fields required
for deletion. `MediaLifecycle__CleanupPageSize` controls the page size (1,000 by default), while
`MediaLifecycle__MaxRecordsPerRun` applies one shared 10,000-record ceiling across purge,
expiration, quota, and retention phases. A truncated run is reported as
`Partial: record cap reached` in the overall and phase status so the next scheduled run can resume.
Large-batch approval counts and bytes are computed with database aggregates before the first page.

The overall media-storage statistics endpoint performs totals and groupings in the database.
Its per-virtual-key breakdown returns only the 100 largest consumers, preventing the response and
query materialization from growing with every virtual key.

Retention policies may cap group storage with `MaxStorageSizeBytes`, `MaxFileCount`, or both.
Before uploading generated media, Gateway resolves the owning group and runs an indexed SQL
aggregate over that group's media records. A policy may reject a write that would exceed quota
with HTTP 429, or allow it for eviction by the next Admin cleanup cycle. Quota cleanup counts
active and recoverable tombstoned objects because both still occupy storage, then permanently
evicts the oldest eligible objects until both limits are satisfied. Recent-access protection is
honored. The media-assets statistics endpoint and page show current usage, effective limits, and
over-quota state for every group.

Set `MediaLifecycle__RequireManualApprovalForLargeBatches=true` to pause scheduled scopes above
`MediaLifecycle__LargeBatchThreshold`. The Admin cleanup status page and
`/v1/admin/media-cleanup-jobs/approvals` expose the durable request with its candidate count,
bytes, group, and cutoff snapshot. Approving re-queries eligible media under the cleanup lock and
limits execution to objects that existed at that cutoff; it never persists or replays a stale ID
list. Unused approvals expire after `MediaLifecycle__LargeBatchApprovalExpirationHours` (24 by
default). The `conduit_admin_media_cleanup_pending_approvals` gauge reports outstanding requests.

With `MediaLifecycle__EnableSoftDelete=true` (the default), expiration and retention cleanup mark
tracked rows with a deletion timestamp. Tombstoned media is immediately hidden from Gateway
serving, normal Admin lists, searches, and storage statistics, but its storage object remains
billable until purge. Admins can include deleted items in the media list and restore them while the
recovery window remains open. Purge permanently deletes the storage object and row after the
assigned retention policy's `SoftDeleteGracePeriodDays`; an active default policy is used when no
policy is assigned, then `MediaLifecycle__SoftDeleteGracePeriodDays` is the final fallback. A
tombstone does not consume the monthly delete budget; the later permanent purge does. Deleting a
virtual key is the deliberate exception: all of that key's active and tombstoned media is
permanently removed before the database cascade, because no owner remains for later recovery.

> **Recent-access protection and CDNs:** `RespectRecentAccess` uses `MediaRecord.LastAccessedAt`,
> which is refreshed only when media is served through the Conduit media API. If
> `CONDUIT_S3_PUBLIC_BASE_URL` points clients at a CDN or public bucket URL, those direct requests
> bypass Conduit and do not protect an asset from retention or quota cleanup. The retention-policy
> editor detects this configuration and displays a warning. Manual prune applies each group's
> `RecentAccessWindowDays`; groups with recent-access protection disabled use age alone.
>
> The current decision is **document and warn**, without exempting every record that has a
> `PublicUrl` and without ingesting provider analytics. `PublicUrl` indicates that an asset can be
> reached publicly, not that it is still used, so treating it as an exemption could retain abandoned
> media forever. CDN analytics ingestion is provider-specific, needs additional credentials and
> reconciliation semantics, and is deferred until there is a supported cross-provider design.

Storage reconciliation enumerates the configured S3-compatible bucket (or in-memory development
store), compares object keys with `MediaRecord` rows, and reports the count and bytes that are
untracked. In a non-dry run it deletes untracked objects only after they are older than
`MediaLifecycle__ReconciliationMinimumAgeHours` (48 hours by default); newer objects are never
deleted. The `VirtualKey` → `MediaRecord` foreign key deliberately retains `ON DELETE CASCADE`.
That keeps key deletion transactional, while the independent storage sweep recovers objects left
behind by a failed pre-cascade storage deletion or another partial write.

The Admin media cleanup status endpoint reports whether soft delete is enabled, its fallback grace
period, the aggregate cycle, and the last outcome of each phase. Prometheus metrics use a
`cleanup_type` label with `purge`, `expiration`, `reconciliation`, `quota`, or `retention`, expose
`conduit_admin_media_cleanup_records_tombstoned_total`, and report untracked drift through
`conduit_admin_media_cleanup_untracked_objects` and
`conduit_admin_media_cleanup_untracked_bytes`. When `MediaLifecycle__TestVirtualKeyGroups` is set,
purge, expiration, quota, and retention are limited to those groups and reconciliation is skipped because
an untracked object no longer has group ownership that can be scoped safely.

## Runtime configuration (in the Admin UI)

Everything about *how Conduit behaves per request* is data in Postgres, created and edited live
through the Admin API and the WebAdmin UI — no restart, no redeploy. This is where you manage:

- **Providers** and their **API keys** — the upstream connections and credentials.
- **Model mappings** — which alias points at which provider and provider model.
- **Model costs** — how each model is priced for billing.
- **Virtual keys** and **key groups** — access control and prepaid balances.
- **Routing policies** — global defaults and per-alias overrides, plus the routing kill switch
  (see [Model routing](./routing.md#turning-routing-off-and-configuring-it)).
- **IP filters, prompt-cache policy, and other global settings.**

The clearest illustration of the deploy-vs-runtime split is the **routing kill switch**: it reads
like an infrastructure toggle, but it is a *runtime* setting stored in the database, so you can flip
routing off during an incident without touching a deployment.

## Where to go next

- **[Core concepts](./concepts.md)** — what providers, keys, mappings, and virtual keys are.
- **[Model routing](./routing.md)** — the routing policies and switch you configure at runtime.
- **[Monitoring](./monitoring.md)** — the health-endpoint and observability settings referenced above.
