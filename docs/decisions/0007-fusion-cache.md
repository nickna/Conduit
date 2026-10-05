# FusionCache application caching (epic #1396)

Status: FC-1 through FC-6 validated; migrated domains remain opt-in until deployment review.
Baseline: `e8355b606a6b0885db8642d6bc41c5cd6b83760f`, refreshed against the checkout on 2026-10-04.
Implementation branch: `codex/epic-1396-fusioncache`. FC-1 issue: #1397.

## Sequence and gates

Complete and commit FC-1, FC-2, FC-3, FC-4, FC-5, FC-6, FC-7, then FC-8.
FC-4's distributed failure and rollback gate precedes every migration beyond discovery.
FC-8 requires a demonstrated rollback and comparison with the thresholds below before
deleting compatibility code. None of the probe's passing assertions constitutes a
production rollout, a durable messaging test, or evidence of linearizable invalidation.

## Inventory

Five production storage consumers use `ICacheManager`: `DiscoveryCacheService`,
`FunctionDiscoveryCacheService`, `CachedModelProviderMappingService`,
`CachedModelCostService`, and `CachedPricingRulesService`. A sixth dependency,
`ModelMappingCacheInvalidationHandler`, writes mapping invalidations directly.
The billing decorators are composed in Gateway `BillingServicesExtensions` and Admin
`ServiceCollectionExtensions`. The shared mapping decorator is registered by
`AddSharedApplicationServices`. Admin's own mapping writes publish change events.

Gateway calls `AddCacheInfrastructure` from `ConfigureCoreServices`, then `AddCacheManager`
from `ConfigureCachingServices`. The latter singleton registration wins normal resolution;
the registry-integrated registration remains enumerable. `CacheRegistry` can resolve the
winning singleton without feeding its policies into it. Admin calls `AddCacheManager`.
`AddCacheInfrastructure` is not a reliable alternative composition: its asynchronous registry
policy update overrides options, and registry discovery/pricing defaults differ from manager
defaults. FC-2 must make composition deterministic and remove redundant registration.

The production operations used are `GetAsync`, `SetAsync`, `GetOrCreateAsync`,
`RemoveAsync`, `RemoveManyAsync`, `RemoveByPatternAsync`, and `ClearRegionAsync`.
`GetEntryAsync`, `SetEntryAsync`, `ExistsAsync`, `ClearAllAsync`, key enumeration,
manager health/statistics APIs, and manager events have no external production callers.
Registry methods/events have only their registration/registry callers and tests.
`CacheRegionAttribute`/descriptors are unused outside this infrastructure.
Do not recreate these APIs around FusionCache.

Domain `CacheStats` is live: Admin `/v1/admin/system-metadata/cache/function-discovery/stats`
and its manual invalidation endpoint, global-settings statistics, and unrelated Redis
cache services use it. Retain its response contract, including `EntryCount = 0` where
the implementation has no exact count. Logical tag expiration must not be reported as a
physical deleted-entry count. Configuration's cache DTOs must be audited separately
against generated OpenAPI/client contracts before deleting them.

## Effective policy matrix

Logical keys below gain `{CacheRegion}:` and the host's distributed prefix today.
The default manager uses memory and, if supplied, distributed caching in all five domains.
`FunctionDiscovery` is absent from the manager's default table: its lazy fallback is 15 minutes.

| Domain | Key / payload | Positive / negative duration | Options and L1/L2 behavior | Ownership and load behavior | Invalidation dependency |
| --- | --- | --- | --- | --- | --- |
| Discovery | `all`, `capability:{value}`, `virtualkey:{id}[:capability:{value}]`, each optionally `:with_pricing`; `DiscoveryModelsResult` of `JsonElement` projections. Function catalog/schema endpoints also store wire JSON inside this payload under their own keys. | Explicit `Discovery:CacheDurationMinutes`, default 360 minutes; no negative marker. | `Discovery:EnableCaching` defaults true. `ExposePricing` defaults true and is part of the model key. Writes use explicit duration; L2-to-L1 promotion uses **region** default (6 hours), losing a shorter explicit TTL. `CacheManager:RegionConfigs:ModelDiscovery` can disable writes/clamp MaxTTL. | Get/load/Set, so concurrent misses are independent. Mutable result and CachedAt are shared in L1; caller sets CachedAt. Endpoint authenticates before cache access and filters by allowed models; warming uses `DiscoveryModelProjector`. | Mapping, canonical model/association, provider, pricing, virtual-key changes and manual requests. Current virtual-key patterns broadly clear the region when ending in `*`; preserve broad invalidation until narrower dependencies are proved. |
| Function tools | `configs:{sorted,comma-separated IDs}`; `List<Tool>` with `JsonObject` schemas. Current sorting does **not** deduplicate. | Explicit method override, otherwise minimum `CacheTtlMinutes` among participating configurations with a TTL; **no write** if none has one. No negative marker. | `Functions.DiscoveryCacheEnabled`: missing/blank/failed lookup is disabled; true/1/yes/enabled enable. Checked before reads and writes. Lazy region default 15 minutes on L2 promotion; explicit writes use calculated TTL. | Scoped repositories; Get/load/Set, mutable tool list returned directly. MCP discovery remains provider-specific. | FunctionConfigurationChanged and manual invalidation currently clear every combination. Enable setting is read on each operation; re-enable must not resurrect entries from before a change. |
| Mappings | `CacheKeys.ModelMapping.ById`, `ByAlias`, `ByAlias + :all`, `AllMappings`; entity or list. | Explicit 10 minutes. Missing single mappings are written null but treated as a miss by legacy GetOrCreate; lists can be empty. | ModelMetadata region default **24 hours**, used by L2 promotion; default writes explicitly override to 10 minutes. MaxTTL can clamp. | Factory access coalesces per local striped lock; L1 returns mutable EF result. L2 loses Association.Model/Model.Series, requiring repair queries. Factory errors can be caught and cause a second DB call. | Add/update/delete and mapping events remove known ID/alias/list keys; alias-list and old alias gaps exist. Model/association/provider changes require routing invalidation too. New projection must contain the complete graph, and every returned result must be independent. |
| Costs | `CacheKeys.ModelCost.ByModelId`, `ById`, `All`; `ModelCost`/list. Missing result uses sibling `:negative` string `missing`. | **12 hours** positive default (the decorator's 15-minute comment is wrong); **1 minute** negative. | ModelCosts region overrides DefaultTTL/MaxTTL apply. L2 promotion uses the region default, including for the negative marker, so a one-minute marker can incorrectly live 12 hours in L1. | Get/load/Set, no coalescing. Validate IsActive, EffectiveDate <= now, ExpiryDate > now on each single read; list preserves administrative listing contract. Missing is null, never a fabricated zero-cost entity. | Decorator writes clear region. ModelCostChanged clears actual billing `IModelCostService`, parsed rules, and priced discovery. `IModelCostCache`/`RedisModelCostCache` and batch invalidation are a separate cache path; names do not establish identity. |
| Parsed pricing rules | `CacheKeys.PricingRules.ById(costId)`; `PricingRulesConfig`. | **15 minutes** default, no negative caching. | PricingRules region overrides. Empty configuration or invalid JSON returns null; source-generated parse uses case-insensitive/camel-case options. | Mutable object conditions become JsonElement on JSON round trip. Get/parse/Set; the key omits configuration content, relying on cost invalidation. | ModelCostChanged targeted cost ID; broad reset for unknown dependencies. Configuration fingerprint should prevent a late parse from restoring a superseded config. |

Manager options actually applied: per-region Enabled, DefaultTTL, MaxTTL, Priority,
EvictionPolicy. Per-region UseMemoryCache/UseDistributedCache/EnableDetailedStats,
and manager UseDistributedCache/GlobalDefaults/StatisticsReportingInterval/
EnableDetailedStatistics are ignored by the ordinary manager constructor. Its timer is always
one minute. Enabled affects **writes**, not reads. FC-2 must define explicit domain options,
honor disable on both paths, and document options that become obsolete.

Cancellation: repository loaders accept request tokens except mapping's existing interface,
which has none. Legacy manager/domain broad catches often swallow canceled reads/writes.
Required invalidation must throw (including cancellation) through real domain dependencies;
best-effort cache failures may bypass to the DB, while business-loader exceptions must propagate
without a duplicate load. No singleton may capture a scoped repository/DbContext.

Intentional correctness changes to test: retain the entry's remaining L2 lifetime when promoting
to L1; preserve one-minute negative expiration; disabled domains bypass reads; propagate caller
cancellation; clone mutable results; fix required invalidation failure propagation and mapping
graph/alias gaps. Do not silently copy the legacy bugs into new policy.

## Packages, serialization, and connections

Pin `ZiggyCreatures.FusionCache` and `.Backplane.StackExchangeRedis` to **2.9.0** in
central package management. Both are MIT; the upstream
[license](https://github.com/ZiggyCreatures/FusionCache/blob/v2.9.0/LICENSE.md) was reviewed.
StackExchange.Redis stays at the repository's 2.13.17 pin. Restore/build and the real Redis
probe establish compatibility with .NET SDK 10.0.401 / runtime 10.0.12.

The candidate `.Serialization.SystemTextJson` 2.9.0 works at runtime with a generated resolver
but calls reflection-capable generic JsonSerializer overloads, introducing native linker warnings.
Use the small `IFusionCacheSerializer` implementation that takes generated JsonTypeInfo instead;
it has no reflection fallback. Register each concrete **FusionCacheDistributedEntry<T>**, including
`FusionCacheDistributedEntry<long>` for tag markers, not only the value contracts. Unknown
types fail closed. Polymorphic pricing conditions use registered primitive/JsonElement values;
tool parameters use JsonObject. Native checks must include Redis, envelopes, and tags.
The final metadata-only native publish produced no FusionCache/serializer diagnostics; existing
EF/compiled-model linker diagnostics remain visible in the probe log, without adding suppressions
to production or changing the service warning baselines.

FusionCache's Redis backplane and Microsoft's RedisCache **both dispose factory-supplied
multiplexers**. Give each its own dedicated owned connection rather than handing them the
host auth/spending/task connection. FC-4 adds a third, lazily owned metadata connection for
clock-independent generation tokens; all three are isolated from other host stores.
Use the common RedisUrlParser result. Register the application RedisCache only inside the
FusionCache composition, not as the host-wide IDistributedCache. Existing Gateway
`conduit-tasks:` and Admin `conduit:` consumers retain their composition/prefixes.
Legacy manager currently uses these host-specific prefixes too; they are not mutually shared.

Target application prefix: `conduit:app-cache:{environment}:v1:` and compatible backplane
channel/cache name in both hosts; environment must be explicit, bounded, validated, and shared.
Production/test namespaces must not overlap. No wildcard Redis scans or shared-store flush.

The supported cache-local composition is plain FusionCache without Redis/backplane.
It does not make unrelated Redis-dependent host services resolvable (see #1366).

## Freshness and error requirements for subsequent gates

Fail-safe, eager refresh, timed-out factory completion, background distributed operations, and
background backplane operations start disabled. This includes **tag options**, whose upstream
defaults otherwise enable fail-safe. Await invalidation with rethrow flags and zero distributed/
backplane circuit-breaker duration; verify failures below domain services. A completed method
alone is not proof that a library short circuit propagated an invalidation.

Bound tags to five domain tags and necessary entity/configuration groups. Marker L2 lifetime
must exceed the longest tagged payload lifetime, including configuration overrides; otherwise an
obsolete payload can become current again after marker expiration. L1/tag-marker lifetimes and
reconciliation/bypass during disconnection must be decided and exercised in FC-4. Billing/routing
need stricter freshness than discovery. Existing durable Wolverine events remain authoritative.
Backplane messages and local factory locks are neither business locks nor exactly-once delivery.

FC-4 found a reproducible tag-only failure: a payload written by a node whose clock is 30 seconds
ahead survived invalidation and restart. Payload keys now include one persistent random generation
per domain. Capture that generation before running a factory; invalidation atomically replaces it
before expiring the tag. An old factory can finish for its original caller but cannot publish into
the current namespace. Atomic SET NX initialization preserves concurrent invalidations. Lost generation
metadata creates a new namespace instead of resurrecting old payloads. There are at most five
persistent metadata keys, without payload-key tracking or Redis scans. Discovery/functions cache
generation reads locally for one second; routing/billing/rules will use 100 milliseconds and require
their own outage-reconciliation tests before selection.

The registered domain tests use a dedicated PostgreSQL database, actual Admin cost mutation,
Admin plus two independent Gateway IHost/cache graphs, and the existing persisted Wolverine
transport. A TCP proxy interrupts only fixture Redis connections. A required invalidation failure
is observed in Wolverine's scheduled-message table; both Gateway hosts are disposed and restarted,
and the same message ID retries successfully after reconnection. ApplicationCacheInvalidationException
uses indefinite persisted retries with 1/5/30-second delays; this is idempotent domain expiration,
not a retry policy for business mutations. Default transport polling adds delivery latency.

After successful event processing, all five pilot variants converge within the tested two-second
bound, including a disconnected backplane. Detected Redis disconnection bypasses discovery cache;
reads are bounded by DistributedReadTimeout (250ms default, <=1s), including initialization.
During recovery, failed invalidations must be retried before freshness is claimed. A connection
recovery alone is not reconciliation. In-flight requests may return their earlier snapshot, and
no linearizability or exactly-once delivery is claimed. Cache-local mode has process-local generations.

Mixed-version rollback was exercised: old-prefix legacy payloads remain stale after Fusion-only
invalidation. Disable discovery reads on **every** legacy process, serve current DB results,
clear each legacy process's complete application domain, then re-enable only after queued changes
are processed. Switching only the selector is unsafe; never flush shared Redis. FC-8 must exercise
and record this procedure before compatibility retirement.

FC-4 evidence: **79 focused tests passed, zero skipped**, including deterministic late-factory
barriers, future-dated payloads, atomic generation initialization, generation-metadata loss,
durable retry across host restarts, duplicate delivery, missed backplane, actual Redis network
interruption and mixed-version rollback. The analyzer ratchet passed with **0 first-party diagnostics**.
The published win-x64 NativeAOT domain probe also passed local/Redis factory checks and separate
write/read processes with generation metadata and persistent invalidation, without new cache linker diagnostics.

## Compatibility and baseline evidence

The focused executable `tools/ConduitLLM.CacheProbe` requires JSON reflection disabled and fails
with a nonzero exit on any violated assertion. Run with `CONDUIT_CACHE_PROBE_REDIS` for real
Redis; absent that variable, the executable explicitly reports that Redis checks were not requested.
`CONDUIT_CACHE_PROBE_PREFIX` enables separate `write` / `read` processes. Optional
`CONDUIT_CACHE_PROBE_POSTGRES` runs JIT-only DB baseline fixtures; use a dedicated empty database.

Evidence captured with isolated Redis 7.4.2 / PostgreSQL 16 containers on loopback, 2026-10-04:

- Reflection-disabled actual discovery/capability/pricing, null/negative, ModelCost/list,
  pricing conditions and tool JsonObject schemas survived Redis L2.
- Independent warmed cache nodes converged after tag invalidation within the five-second
  probe bound; a restarted cache rejected the obsolete L2 payload using the persisted marker.
- 32 simultaneous healthy misses performed exactly one local factory load; cancellation propagated.
- Dedicated-node disposal left another node able to serve its cached cost.
- Published win-x64 NativeAOT executable passed real Redis scenarios and separate write/read
  process discovery round trip. This does not execute native EF queries (#1368).
- Existing focused legacy contract suites: **78 passed, 0 failed, 0 skipped**.

Representative cache read measurements (200 samples after 20 warmups; microseconds):

| Path | Median | p95 | Allocated bytes/call | IDistributedCache calls |
| --- | ---: | ---: | ---: | ---: |
| Legacy discovery payload L1 | 1.10 | 1.40 | 320 | 0 |
| FusionCache discovery payload L1 | 0.70 | 0.90 | 232 | 0 |
| Legacy discovery payload L2 | 471.50 | 583.40 | 16,195 | 200 |
| FusionCache discovery payload L2 | 409.50 | 539.20 | 3,708 | 200 |

These are local infrastructure samples, not endpoint throughput claims. Distributed calls
count adapter methods, not physical Redis commands (RedisCache may issue more than one).
Native samples were also successful; runtime scheduling/noise makes small differences inconclusive.
Read-only Redis INFO instrumentation on the final run observed 201 commands for 200 L2
reads in both implementations (one is the observer INFO command), and one observer command
for 200 L1 reads. Redis MEMORY USAGE for the representative discovery entry was 528 bytes
legacy / 704 bytes FusionCache (1.33x). One active cache node used two multiplexers/four
Redis sockets; instrumentation added one multiplexer/two sockets, yielding six connected
clients. These counts exclude additional independent host stores.

Real shared discovery query and full repository-backed mapping decorator on the one-model
PostgreSQL fixture (single cold sample includes EF first-use work):

| Legacy service path | DB reader commands | Elapsed microseconds | Allocated bytes |
| --- | ---: | ---: | ---: |
| Discovery cold | 1 | 102,126 | 2,507,480 |
| Discovery L1 | 0 | 47 | 1,032 |
| Discovery L2 | 0 | 904 | 15,872 |
| Mapping cold | 1 | 93,337 | 1,437,512 |
| Mapping L1 | 0 | 32 | 1,256 |
| Mapping L2 including repair | **1** | 9,180 | 273,936 |

The discovery measurement uses the production shared DB projection and a minimal cache payload;
the read microbenchmark above covers a priced wire-shaped payload. Mapping capability assertions
guard the full cold/L1/L2 outputs. FC-6 must reduce the healthy complete L2 repair count to zero.

Before migration, set these rollout comparison thresholds: no extra DB loads on healthy hits;
at most one load per instance for 32 same-key concurrent misses; cold paths retain one query per
representative loader; median/p95 cache-operation latency <= baseline * 1.25 plus 5 microseconds
(L1) / 250 microseconds (L2); allocations <= baseline * 1.25 + 512 bytes/call. Tagged/clone costs
must be measured separately. Redis operations and resident entry memory <= 2x the matched
legacy workload; explain fixed tag/backplane overhead and connection count explicitly. Failure
freshness is a hard gate independent of performance. Re-measure on matching hardware at FC-8.

## Retirement inventory and effort

FC-6 uses a versioned explicit mapping snapshot containing all routing/provider/association/model
and series-default fields plus detached cost scalars. Credentials and cyclic EF navigation collections
are excluded. It reconstructs independent mutable domain graphs on every read, so a complete L2
hit avoids the legacy navigation-repair query. ID, alias, alias-list and aggregate keys share a
generation; mapping writes/events expire them all, including old aliases on rename. Provider,
model/association, cost, series-default and catalog changes also expire routing dependencies. The series
endpoint now publishes the existing discovery invalidation event after persistence; its handler
expires mapping dependencies as well. Handler cancellation and required failure propagation remain
visible to the persisted retry policy. The mapping business interface still has no cancellation token.

Routing payload/generation L1 durations are at most 100ms. Observed storage failures quarantine
strict-domain generation reads; successful recovery rotates the namespace before accepting cache
hits, forcing a current business load even before a pending invalidation retries. No stale serving
or background completion is enabled. The tested mapping convergence bound is one second after
successful invalidation. FC-7 must exercise this stricter policy through the actual billing path too.

FC-6 evidence: **81 focused tests passed, zero skipped**. Redis/PostgreSQL regressions cover ownership and 32 misses/one factory,
complete independent L2, renamed aliases/lists/deletion, provider/model/association dependencies,
late mapping factories, outage fallback and reconnection before retry, actual host selections and
series endpoint publication. The analyzer ratchet passed with **0 first-party diagnostics**;
published win-x64 NativeAOT local/Redis/separate-process mapping checks passed. Real repository
query counts are **1/0/0** for cold/L1/restarted L2, versus legacy **1/0/1**. The first complete L2
read allocates generated metadata during first use; these single samples are not steady-state
performance claims. FC-8 compares warmed matching workloads against the predeclared thresholds.

FC-5 migrates scoped function discovery policy, loader and invalidation callers. Normalized
configuration sets use one factory/configuration query, retaining minimum configured TTL,
explicit overrides, global toggle interpretations and no-write behavior without a TTL.
Configuration changes expire every combination and discovery catalog/schema payloads;
toggle events expire combinations while disabled. Endpoint catalog/schema loads also use
generation-captured factories, preserving authentication and missing-configuration errors.
Mutable tool/schema state is detached and cloned; MCP expansion and names are unchanged.

FC-5 evidence: **62 focused tests passed, zero skipped**, including real Redis two-node
overlapping combinations, configured/override TTL, MCP cold/L1/L2 parity, one factory for 32
normalized concurrent requests, late schemas, cancellation, toggle recovery, scope validation,
and service-level storage failure through the handler. The analyzer ratchet passed with
**0 first-party diagnostics**. Published win-x64 NativeAOT function modes passed local/Redis
coalescing and independent-process schema/invalidation round trips with reflection disabled.

FC-3 routes discovery requests and warming through one wire projection and a domain factory
method. Discovery's selector is reversible and remains Legacy by default. The FusionCache
service preserves variant keys and TTL caps, detaches incoming JsonElements and copies mutable
containers on reads, bypasses disabled caches, and rethrows required invalidation failures.
Storage fallback never retries a failed business load or repeats a successful load after a
failed write. Broad discovery dependencies replace region/pattern scans. FC-4 remains the
mandatory distributed race/outage/mixed-version gate before further domain migration.

FC-3 evidence: **52 focused tests passed, zero skipped**, with real Redis L2/restart,
32 same-key concurrent misses/one factory, mutable ownership, disabled reads, TTL caps,
business/cache failure separation, cancellation, denied-key access, wire equality and pricing
visibility. The production discovery probe passed as published win-x64 NativeAOT in local
and Redis modes, including separate-process write/read and persistent invalidation recovery.
The real PostgreSQL probe reported discovery cold/L1/restarted-L2 query counts **1/0/0**.
The analyzer ratchet again passed with **0 first-party diagnostics**, without baseline changes.

FC-2 evidence: 34 focused tests passed with Redis host composition enabled (zero skips),
including scope-validated Gateway/Admin local and Redis graphs, legacy regressions,
invalid policy rejection and observable Redis failures. The registered `compose` probe
passed in JIT and published win-x64 NativeAOT, with both Redis and cache-local graphs;
it checked shared namespaces, actual generated metadata, cloning, tag propagation and
bounded telemetry. The repository analyzer ratchet passed with **0 first-party diagnostics**,
without updating its baseline. Gateway's duplicate registry/manager registration was removed;
the ordinary legacy manager remains the sole production selection at this gate.

The four manager partials are **806 production lines** (303/167/149/187). Additional candidates:
manager interface and embedded stats/events (259), registry (255), registry interface/contracts
(192), registration (195), attributes/descriptors (175), entry/configuration models (214), and region
enum (120): **2,216 lines** to audit, not a promised deletion count. Retain any enum/contracts
still used by independent Redis/security/task consumers. Report net production additions/deletions
with `git diff --numstat` against the pinned baseline at closure.

Tests to retarget: discovery service/endpoint pricing and shape, Admin virtual-key discovery,
functions MCP/tool schemas, mapping decorator/cold start/invalidation handlers, costs/pricing JSON,
model-cost invalidation and real failure contracts. Remove manager/registry mechanism tests only
after their useful contracts have been preserved in the domain/Redis suites. Batch invalidation
tests remain relevant to the independent Redis stores.

Revised effort remains eight serial correctness gates, with FC-4/6/7 largest. The compatibility
spike validates the chosen stack but does not make a full migration a package-only change.
Reserve separate commits for composition, pilot, distributed failure/rollback proof, functions,
complete mappings, billing/pricing, and retirement; allow further commits for gate failures.
Planning estimate after FC-1: FC-2 1–2 engineering days, FC-3 2–3, FC-4 3–5, FC-5 1–2,
FC-6 2–4, FC-7 2–4, FC-8 1–2 plus the operator's rollout/rollback observation window.
This is a complexity estimate, not a delivery promise; failed freshness gates may extend it.
Excluded follow-ups: RedisCacheServiceBase, BufferedStatsRedisCacheBase, DistributedCachePopulator,
HybridCacheAccessor, global settings, auth, provider credentials, IP/counters, tasks/dedup,
Data Protection, provider health/response/embedding caches.
