# Admin NativeAOT feature matrix

This page defines the default narrow Admin contract for the first NativeAOT candidate
and its separately enabled experimental webhook recovery profile.
Production remains JIT under [ADR 0006](../decisions/0006-native-aot-persistence.md).
Native publish success and a broad OpenAPI document do not qualify the remaining
EF-backed management APIs. The [Gateway matrix](gateway-native-aot-feature-matrix.md)
defines the separate request-time contract.

`GET /health/runtime-capabilities` reports `runtime_mode: native-aot`, an empty
`signal_r_protocols` array and
`persistence_status: typed-npgsql-selected-runtime-slices`. Its included/excluded
feature names below describe the default profile, with nine included and nine
excluded features. The experimental opt-in adds one capability label and the four
operations described under [webhook recovery qualification](#opt-in-webhook-recovery-gate-qualification).
[Linux run 38175668349](https://github.com/nickna/Conduit/actions/runs/38175668349)
at `e0f9f9904df187460ab338bd86619d912f653d60` passes all three jobs: 219 selected
persistence and integration cases without skips, native publishing, both OpenAPI
exporters, the complete host gate and both packaged entrypoints, with zero
first-party linker diagnostics. The gate checks Admin and both Gateways' complete
metadata contracts and owned cleanup. Its image packages record clean Git source,
but their classic-store OCI descriptor association remains HOLD. The later cache
convergence proof below is separate local evidence.

## Matched management route boundary

Native Admin checks the matched endpoint's operation name, complete route pattern
and single HTTP method after the existing authentication, IP/rate-limit and
authorization middleware. The default profile selects exactly 23 operations: the 12 global
settings operations, 10 IP-filter operations and the cache-only
`Auth_GenerateEphemeralMasterKey` operation. The boundary uses exact metadata
triples; a supported name on a different route or method is rejected.

Other matched `/v1/admin` management endpoints in the default profile return generated
`application/problem+json` with status 501 and code `not_implemented` before
request-body binding, endpoint filters or handler-service resolution. This makes
excluded EF management/reporting/manual-media operations observable without
invoking their persistence paths. Pricing routes retain their existing native
omission. Unmatched paths and wrong methods retain routing's 404/405 responses;
health, metrics and OpenAPI routes remain outside the management boundary. JIT
retains its endpoint behavior and the full OpenAPI contract.

The managed tests materialize the actual production endpoint map, pin all 23
triples and check that no alternate management namespace or alias escapes the
boundary. TestServer sentinels cover body/DI/filter rejection and security order,
with JIT controls and sequential ephemeral-key consumption. The published-native
probe requires authenticated rejections across six excluded families, anonymous
authentication failure, and real cache-only key issuance, consumption and replay
rejection. These assertions do not claim atomic concurrent key consumption or
native EF query execution. Final committed-source Main and Native CI remains
required; the default capability arrays below retain nine included and nine
excluded features. The experimental webhook profile uses the same exact metadata
checks and leaves all remaining exclusions in place.

## Selected native behavior and evidence

| Area | Selected implementation | Evidence and limit |
|---|---|---|
| Master-key authentication | Existing Admin security middleware and Bearer credential handling. | [Linux run 38147134135](https://github.com/nickna/Conduit/actions/runs/38147134135) uses the configured master key for settings HTTP and rejects missing/wrong credentials without changing SQL. The capability label retains the existing Bearer fallback behavior; this is not a claim about every Admin security configuration. |
| Global settings HTTP | `IGlobalSettingRepository` resolves to singleton `NpgsqlGlobalSettingRepository`. | The same published run passes all six SQL/DTO fields, generated IDs, GET by ID/key, pagination, duplicate rejection, merge-patch no-op/clear/value semantics, PUT upsert, delete, typed-setting validation and protected-key rejection. Two GUID-owned keys are removed with independent cleanup; unrelated setting scalars remain unchanged. |
| Global settings cache | The shared `GlobalSettingsCacheService` uses the selected typed repository. `GlobalSettingChanged` broadcasts a reload using the stable event ID, so instances sharing a service queue all refresh their process-local caches. | The unit 96 local Linux native proof first reproduces the original SQL value 7 with Gateway caches split 7/5. Fresh native Gateways then consume ordinary Admin creation 5→7 without manual reload, retain 7 after direct SQL changes the owned value to 9, consume 9 after explicit reload, and restore 5 after owned deletion/reload. The full process gate and cleanup pass with the original native Admin retained. The separate CRUD probe's 204/202 checks remain acknowledgements only. Failed-reload retry and completed-ID retention remain [#1623](https://github.com/nickna/Conduit/issues/1623) and [#1627](https://github.com/nickna/Conduit/issues/1627). |
| IP-filter HTTP and enforcement | Singleton `NpgsqlIpFilterRepository` supplies CRUD and `VersionedResourceEndpointFilter` reads before and after mutations. JIT retains EF. | The unit 76 local gate and successful public [Linux run 38152552344](https://github.com/nickna/Conduit/actions/runs/38152552344) pass all 12 SQL/11 DTO fields, global/enabled/per-key pagination, merge-patch/no-op semantics, strong byte/timestamp ETags, 428/412, deletion-tag fallback and missing resources. A real socket bound to `127.0.0.2` is denied, allowed and denied again as owned global rules change. Private-IP bypass is disabled only for the owned child; per-key rules do not become global Admin policy. All unrelated IP/settings rows remain unchanged and three owned names are cleaned after producer shutdown. The later complete run repeats this gate. |
| Billing-audit host | Singleton `NpgsqlBillingAuditRuntimeWriter` and one directly hosted `BillingAuditService` per native host; 100-event threshold, 10-second timer and cancellation-aware final drain. | [Linux run 38149774358](https://github.com/nickna/Conduit/actions/runs/38149774358) passes the standalone 17-scalar/default/129-row rollback/constraint/cancellation proof and independent timers plus graceful shutdown tails on two Gateways. Admin selects the same writer and hosted composition. No controlled Admin HTTP billing enqueue is claimed. Scheduled native retention is disabled; reads, manual retention and abrupt-termination durability remain unsupported. |
| Schema, database and Wolverine readiness | Release-owned standalone JIT migrator, read-only Wait barrier, typed schema/pool probes and schema-gated Wolverine startup. | Published native hosts recover on the same processes from empty/prior databases and live PostgreSQL outages, with liveness 200/readiness 503 until recovery. Future history is rejected. [Linux run 38121134709](https://github.com/nickna/Conduit/actions/runs/38121134709) passes these cases; the later complete gate repeats them. Admin does not require the Gateway's Redis readiness behavior. |
| Liveness, metrics and telemetry | Existing health endpoints, HTTP Prometheus exposition, OpenTelemetry and heartbeat publisher remain registered. | Published host/OpenAPI smokes and the native parity gate pass startup/liveness. This does not qualify the omitted database-count operations collector or broader Admin reporting APIs. |

The exact default native `included_features` values are:

- `postgresql-wolverine-persistence`
- `master-key-and-bearer-fallback-authentication`
- `typed-global-settings-http`
- `typed-ip-filter-http-and-enforcement`
- `typed-global-settings-cache`
- `typed-billing-audit-host`
- `release-owned-schema-wait`
- `readiness-and-database-health`
- `liveness-metrics-opentelemetry`

The selected repository override lives in
`Services/ConduitLLM.Admin/Extensions/ConfigurationExtensions.cs`; it preserves
the host's singleton `NpgsqlDataSource`. Public constructors and JIT repository
selection remain compatible. Other resource-version branches, provider/model/
function/key management and reporting remain on their legacy EF dependencies.

## Native omissions and explicit exclusions

Native builds omit seven EF-backed hosted workers. JIT retains the original
registrations, options and leadership/scheduling behavior:

| Worker | Native registration boundary |
|---|---|
| `ModelCostCanaryHostedService` | `Extensions/ServiceCollectionExtensions.cs` omits the leader-elected worker; options validation remains. |
| `PricingConfigurationAuditHostedService` | The same extension omits the startup hosted audit. |
| `PricingAuditService` | The same extension omits the optional audit service and leader-elected wrapper. Retaining an unstarted queue would still permit an EF flush at its 100-event threshold. Pricing evaluators and validators remain registered with the optional audit dependency absent. |
| `AdminOperationsMetricsService` | `Program.Monitoring.cs` omits the database-count collector. HTTP metrics and telemetry remain. |
| `OpenRouterMetadataSyncService` | `Extensions/OpenRouterSyncExtensions.cs` omits the scheduled sync worker. Manual drift/apply services remain registered but unsupported natively. |
| `MediaStorageConfigurationGuard` | `Extensions/MediaLifecycleExtensions.cs` omits hosted startup validation; the singleton and manual guard interface remain registered. |
| `MediaCleanupService` | The same extension omits hosted cleanup scheduling; its singleton remains registered for manual-service dependencies. |

Paths in this table are under `Services/ConduitLLM.Admin`. Native handler
registration and runtime routing also omit `PricingEndpoints`, including pricing
tools and audit routes, because its constructor requires the omitted audit
service. JIT retains those endpoints. Media deletion, cleanup approval/status,
reconciliation, storage guard and other management services remain registered for
dependency compatibility; that is not native execution support. Removing hosted
startup does not make their direct EF queries or manual mutations safe to invoke.

The exact native `excluded_features` values, unchanged by the webhook opt-in, are:

- `ef-backed-admin-management`
- `pricing-tools-and-audit`
- `model-cost-canary`
- `startup-pricing-configuration-audit`
- `media-cleanup-and-reconciliation`
- `media-storage-startup-guard`
- `openrouter-metadata-sync`
- `database-count-operations-metrics`
- `billing-audit-reads-and-retention`

Readiness has no dependency on the seven omitted workers. Typed settings/IP
services, the global settings cache, typed billing-audit host, schema/pool and
Wolverine startup, heartbeat publisher, HTTP metrics, telemetry and Redis data
protection remain registered. JIT reports `persistence_status: ef-core-supported`
and retains the full existing worker and endpoint profile with no native
exclusions.

## Verification and promotion limits

`HostCompositionTests` checks actual host service identities and native omissions
alongside JIT parity. The capability assertions and selected HTTP proofs run from
`Tests/ConduitLLM.GatewayNativeAotTests/Program.cs` through
`scripts/aot/gateway-native-parity.ps1`. They compare independent PostgreSQL
scalars and preserve ownership during ordinary, failed and repeated cleanup.
The unit 77 local published gate passes these checks, settings/IP HTTP, bounded
Gateway protocols/functions/accounting and billing shutdown, followed by confirmed
owned SQL, Redis, S3, process, container, network and private-config cleanup.

The unit 96 cache phase makes 12 authenticated Gateway validation requests under
its hard aggregate cap of 50, with no provider dispatch, executions, request logs,
spend or ledger changes. Unrelated setting fields stay unchanged, and independent
cleanup repeats after producer shutdown. Its fresh Gateway/probe publications and
retained Admin publication are explicitly separate generations with unavailable
Git provenance; this local proof does not qualify a registry digest.

These results do not establish full Admin API parity, production canary behavior
or promotion. Digest-pinned comparative
measurements, rollback and ordered actual soaks remain required by the
[canary runbook](native-aot-canary.md), and a new promotion decision is required
before changing ADR 0006. Unsupported management queries and future worker
extraction are tracked in the
[persistence inventory](persistence-aot-query-inventory.md).

## Opt-in webhook recovery gate qualification

Native Admin leaves webhook recovery disabled by default. Set
`NativeAdmin:EnableWebhookRecovery=true` in configuration, or
`NativeAdmin__EnableWebhookRecovery=true` in the Admin process environment, to
enable the experimental qualification profile. The middleware and capability
response capture this setting at startup; configuration reloads do not change
the active profile. JIT retains its normal endpoint behavior.

This opt-in adds exactly four typed Npgsql/Wolverine operations to the default 23:

| Operation | Method | Matched route pattern |
|---|---|---|
| `Webhooks_Inspect` | GET | `/v1/admin/webhook-deliveries/` |
| `Webhooks_Backlog` | GET | `/v1/admin/webhook-deliveries/backlog` |
| `Webhooks_DeadLetters` | GET | `/v1/admin/webhook-deliveries/dead-letters` |
| `Webhooks_Replay` | POST | `/v1/admin/webhook-deliveries/{id}/replay` |

The enabled boundary therefore allows 27 operations. `Webhooks_Purge` remains
excluded and returns 501 before binding or handler resolution, as do other
excluded management operations. Names on a different route or method, and
multiple-method metadata, remain rejected. Authentication, IP/rate limits and
authorization still precede this boundary.

The enabled native capability response appends
`experimental-webhook-recovery` to `included_features`, for ten included features
and the same nine excluded features. `runtime_mode`, `signal_r_protocols` and
`persistence_status` retain their native values. Leaving the setting unset or
false retains the exact default 23 operations, nine included features and nine
excluded features. Enabling this experimental profile does not qualify broader
Admin management or change the production JIT decision.

The authentication correction for [#1685](https://github.com/nickna/Conduit/issues/1685)
retains strict successful Bearer/X-API-Key and invalid-credential assertions in
`WebhookNativeRuntimeTests`. Image overrides
`CONDUIT_WEBHOOK_NATIVE_GATEWAY_IMAGE` and
`CONDUIT_WEBHOOK_NATIVE_ADMIN_IMAGE` allow the opt-in gate to select freshly built
images. The gate enables `NativeAdmin__EnableWebhookRecovery=true` only on its
Admin container. Before authentication or delivery assertions, it requires both
containers to report `runtime_mode: native-aot` and Admin to include
`experimental-webhook-recovery`; JIT image overrides cannot qualify this gate.
PostgreSQL, Redis and both native apps share a fixture-owned Docker network;
fixture setup applies migrations through managed EF before native startup.
The gate retains strict successful authentication, delivery exhaustion,
inspection, dead-letter and replay assertions. Managed tests separately pin the
default and enabled route inventories, startup snapshots, generated HTTP
contracts, security precedence and pre-binding Purge rejection. These controls
do not substitute for running the complete gate with fresh native images.

On 2026-10-17, the complete local gate passed one test with zero skips using
fresh native Admin and Gateway images. Both reported native mode and Admin
reported the experimental capability. Valid Bearer and X-API-Key requests
returned 200 for settings and backlog; absent/invalid backlog credentials returned
401. Receiver responses 503 then 400 exhausted the delivery; inspection and
dead-letter reads succeeded, and replay returned 200. The receiver accepted the
replayed delivery with 202 and its row reached `Delivered` cycle 1. Exactly three
posts retained the same event ID, authorization header and
payload. All 60 managed boundary tests and seven PostgreSQL webhook tests also
passed. Both native builds reported zero first-party warnings. Final container
and network sets matched the pre-run snapshot after a transient extra container
was independently confirmed absent.

The receipt's Docker image inspection identities are Admin
`sha256:8a9975b79da69fd0a3391befcdb1782cc7ce0a60677a1a31f8d5cdcdcbc146fe`
and Gateway
`sha256:dec50506e2e7cb5ea5479c88fc55bc068b12a7ae0c314b7ce764a276fdadb10b`.
The local receipt `artifacts/issues-1685-1711/native-webhook-receipt.json` has
SHA256 `107ee66e40245bae2de7423174bbad2d6cf5563977a7d603b19639d61e8200ec`.
These images use a local gitless source context with uncommitted qualification
changes; this establishes local opt-in recovery qualification, not image promotion.

### Historical failed qualifications

The first attempt with the new opt-in profile failed its experimental capability
assertion because the fixture discarded the immutable container builder returned
by `WithEnvironment`. Admin correctly retained its default profile. That failed
result and logs remain retained; correcting only the fixture's builder assignment
enabled the successful gate above without changing production code or assertions.

A prior local run using both unmodified native Dockerfiles at production source
`7a25d05e1aa9f5e7642a5876ceb4f9f08fa9d844` returned 200 for valid Bearer and
X-API-Key requests to the supported global-settings route. Webhook backlog then
returned 401 for absent and invalid Bearer credentials, followed by 501 for the
valid Bearer request that requires 200. At that revision, the native management
boundary excluded every `Webhooks_*` operation. The run stopped before publishing a delivery request;
it establishes no native delivery, inspection, dead-letter or replay result.

That gate/profile incompatibility is recorded in
[#1706](https://github.com/nickna/Conduit/issues/1706). Existing managed
durable-delivery tests passed all four cases, and 17
managed authentication cases passed; neither substitutes for the failed native
gate. Fresh local images used a gitless source archive policy and retained
third-party AOT warnings, so this is not clean-Git image promotion evidence.
Both failed native attempts and their resource snapshots remain retained; a
transient additional container ID was absent on a later exact read, without
changing either original failed capture result.

The #1685 authentication correction is already merged in commit `26a058ebe`.
On 2026-10-16 (America/Los_Angeles), a fresh Release build at `cdcf9cf57`
passed all 58 selected master-key authentication, ephemeral-key integration and
native-profile middleware tests, with no skips:

```powershell
dotnet test Tests/ConduitLLM.Tests/ConduitLLM.Tests.csproj -c Release -m:2 --filter 'FullyQualifiedName~MasterKeyAuthentication|FullyQualifiedName~EphemeralMasterKeyIntegration|FullyQualifiedName~NativeAdminRuntimeProfileMiddlewareTests'
```

That recheck alone did not rerun native images or establish webhook recovery
qualification. The successful fresh-image gate above supplies the separate
experimental recovery evidence while preserving the default native boundary and
the existing promotion limits.
