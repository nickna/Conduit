# Admin NativeAOT feature matrix

This page defines the narrow Admin contract for the first NativeAOT candidate.
Production remains JIT under [ADR 0006](../decisions/0006-native-aot-persistence.md).
Native publish success and a broad OpenAPI document do not qualify the remaining
EF-backed management APIs. The [Gateway matrix](gateway-native-aot-feature-matrix.md)
defines the separate request-time contract.

`GET /health/runtime-capabilities` reports `runtime_mode: native-aot`, an empty
`signal_r_protocols` array and
`persistence_status: typed-npgsql-selected-runtime-slices`. Its included/excluded
feature names below describe this selected profile.
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
authorization middleware. Exactly 23 operations are selected: the 12 global
settings operations, 10 IP-filter operations and the cache-only
`Auth_GenerateEphemeralMasterKey` operation. The boundary uses exact metadata
triples; a supported name on a different route or method is rejected.

Other matched `/v1/admin` management endpoints return generated
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
required; capability feature arrays below retain nine included and nine excluded
features.

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

The exact native `included_features` values are:

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

The exact native `excluded_features` values are:

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
