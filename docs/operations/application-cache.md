# Shared application cache

Implementation: [epic #1396 design record](../decisions/0007-fusion-cache.md).
Discovery and Functions selectors are available; defaults still select the legacy implementation.
Other per-domain selectors take effect as each gated migration lands. Authentication, tasks,
spending, provider credentials, ephemeral keys, Data Protection and other Redis stores
keep their existing registration and namespaces.

## Configuration

Both Gateway and Admin use the existing `RedisUrlParser` resolver (`REDIS_URL`, then
`CONDUIT_REDIS_CONNECTION_STRING`). They independently own a dedicated application
RedisCache connection, Redis backplane connection and lazy generation-metadata connection. None is the host's shared
IConnectionMultiplexer. DI disposes the RedisCache; FusionCache unsubscribes and disposes
its backplane and dedicated L1. The application store is a keyed IDistributedCache and
does not replace the normal host-wide IDistributedCache.

```json
{
  "ApplicationCache": {
    "Environment": "production",
    "LocalDuration": "00:00:05",
    "DistributedReadTimeout": "00:00:00.250",
    "MaximumDuration": "7.00:00:00",
    "Implementations": {
      "Discovery": "Legacy",
      "Functions": "Legacy",
      "Mappings": "Legacy",
      "Costs": "Legacy",
      "PricingRules": "Legacy"
    }
  }
}
```

Environment defaults to the host environment name in lowercase. Set it identically
in both hosts in a deployment; isolate every environment. It accepts 1–64 ASCII
letters, numbers, underscores and hyphens. Keys use
`conduit:app-cache:{environment}:v1:`; the backplane uses the same versioned namespace.
The hosted services' existing distributed-cache prefixes remain unchanged.

LocalDuration must be positive and at most five seconds. A domain's configured TTL
remains its L2 lifetime; L1 uses the smaller TTL/LocalDuration, and L2 promotion respects
remaining logical lifetime. MaximumDuration must be between 12 hours and 30 days.
Domain writes with a longer duration fail validation; tag markers live MaximumDuration
plus one day so obsolete payloads cannot outlive their invalidation markers. Tag markers
use a one-second L1 duration. DistributedReadTimeout must be positive and at most one second;
it bounds storage/metadata reads, never detaches a write or required invalidation.

Legacy/FusionCache selections are per-process startup settings; they are temporary rollout
controls. Change them only at the domain's documented gate, and exercise the FC-4
mixed-instance/rollback procedure. A flag switch alone is not a cache freshness guarantee.
The final retirement removes selectors after the rollback window closes.

## Failure and freshness contracts

Fail-safe stale reads, eager refresh, timed-out factory completion and background distributed
and backplane operations are disabled for both payloads and tag markers. Invalidation is
awaited, serialization/distributed/backplane exceptions are rethrown, and the distributed and
backplane circuit breakers have zero duration. Domain services decide which reads can fall
back to the database. Required invalidation failures must reach durable Wolverine retry.

Auto-cloning prevents callers from mutating cached results on read. Discovery instead
detaches immutable JsonElements once when publishing and copies their mutable container
on every read, avoiding full JSON round-trips on L1 hits. Domain loaders must
also detach mutable values before publishing them. Singleton cache composition receives
loaders as per-call delegates; it never captures repositories or DbContexts.

JSON uses generated metadata for the payload **and** FusionCacheDistributedEntry envelope,
including the long tag-marker contract. Unknown types fail closed. Never enable JSON
reflection to make a new cache payload work; add its concrete generated metadata and test
it through Redis. Mappings need a complete explicit projection at FC-6, rather than the
legacy tracked entity graph.

## Monitoring and validation

`conduit_application_cache_operations_total` joins the existing Prometheus registry.
Labels are bounded domain, operation and result values, never raw keys, model names,
configuration IDs, virtual keys or per-run environment names. High-level events report
hits/misses, factory successes/business errors, serialization errors and tag invalidations.
Backplane receive/publish events use the shared domain. Redis adapter failures report
`result="redis_error"`; required domain invalidation failures report `operation="invalidate"`
with `result="error"`. Tag invalidation counts operations, not removed physical entries.

Run the registered composition probe from the repository root:

```powershell
$env:CONDUIT_CACHE_PROBE_REDIS = '127.0.0.1:16396'
dotnet run --project tools/ConduitLLM.CacheProbe -c Release -- compose
$env:CONDUIT_CACHE_TEST_REDIS = '127.0.0.1:16396'
dotnet test Tests/ConduitLLM.Tests --filter 'FullyQualifiedName~ApplicationCacheCompositionTests'
```

Without a Redis probe connection, `compose` explicitly tests cache-local composition.
Redis host tests report skipped unless CONDUIT_CACHE_TEST_REDIS is set. Both host roots
are exercised with scope validation. Cache-local composition does not repair unrelated
host dependencies described in #1366.

The probe's `compose` mode demonstrates compatible independent Admin/Gateway cache
graphs, source-generated L2 reads, caller ownership, backplane invalidation and bounded
telemetry. It does not claim FC-4's full PostgreSQL/Wolverine process topology.
Never flush shared Redis to recover this cache; only this application namespace is eligible
for an operational cleanup, and logical tag expiration normally needs no physical cleanup.

## Discovery pilot

`ApplicationCache:Implementations:Discovery=FusionCache` selects the factory-oriented
discovery service. The endpoint authenticates before cache access. Requests and the warmer
use the same projection, keys, pricing visibility and factory path. Capability, virtual-key,
and priced/unpriced variants remain separate; the shared discovery tag deliberately covers
all variants and the discovery endpoint's function catalog/parameter payloads. Existing
pattern invalidations invalidate this dependency broadly, without enumerating Redis keys.

`Discovery:EnableCaching` and the existing
`CacheManager:RegionConfigs:ModelDiscovery:Enabled` disable both reads and writes.
`Discovery:CacheDurationMinutes` sets the positive L2 TTL (default 360); the region MaxTTL
caps it. The explicit discovery TTL takes precedence over region DefaultTTL, as before.
Invalidation remains active even while caching is disabled. Cache storage/serialization
failures allow current database results to serve; business loader failures and cancellation
propagate. A successful load followed by a failed cache write runs the loader only once.
Required invalidations propagate failures for durable retry.

## Distributed recovery and rollback

Each domain has one persistent random generation key under the application prefix. Payload
factories capture it before loading; invalidation replaces it before expiring tags. Clock skew
and late factories therefore cannot place an old result in the current generation. Missing
metadata initializes a new generation atomically. Do not assign a TTL to generation keys.
Discovery/functions refresh their local generation within one second; mappings/costs/rules
use 100 milliseconds and require the stricter domain tests before rollout.

Discovery convergence was tested within two seconds **after successful event processing**,
including lost backplane delivery. This excludes database/transport outage duration and queue
latency. Detected Redis disconnection serves current DB results; a factory already running
may return its earlier snapshot to that request. Recovery alone does not certify freshness:
wait for pending invalidations to succeed. Required invalidation failures throw
ApplicationCacheInvalidationException and use persisted Wolverine retries at 1/5/30 seconds
indefinitely. Monitor retry backlog and invalidate-domain errors during recovery.

Before a rollback, disable discovery caching on every serving legacy process, including both
Gateway and Admin graphs that serve the domain. Drain pending durable invalidations, clear
the complete legacy ModelDiscovery region on every such instance (or replace those processes
while reads remain disabled and remove only their verified old application domain keys), then
enable the legacy selection. Cover every legacy instance and the full old domain namespace;
partial sampled-key cleanup or a selector switch alone can resurrect stale old-prefix values.
Other shared Redis stores must remain intact. The fixture proves this procedure on its complete
known keyspace; operators must inventory the actual deployment's processes/prefixes.

Reproduce FC-4 against isolated fixtures by setting CONDUIT_CACHE_TEST_REDIS and
CONDUIT_CACHE_TEST_POSTGRES, then running DistributedDiscoveryCacheTests and
FusionDiscoveryCacheTests. The former creates and removes its own uniquely named database;
the PostgreSQL fixture login must permit database creation. It starts Admin and two independent
Gateway hosts using the real persisted transport, interrupts only their proxy connections,
restarts both gateways with a scheduled retry pending, and verifies the original message ID.
This is integration evidence, not a production rollout or a claim of linearizable coordination.

## Function discovery

`ApplicationCache:Implementations:Functions=FusionCache` selects scoped function policy and
factory loading. The singleton cache never captures its scoped repositories. ID sets are sorted
and deduplicated; tools retain the existing MCP expansion/names and JsonObject schemas. The
loader validates requested configurations and computes the minimum configured CacheTtlMinutes
from its single configuration query. Explicit TTL overrides take precedence, with the existing
FunctionDiscovery region MaxTTL cap. No configured TTL means no cache write.

Functions.DiscoveryCacheEnabled is checked on every access: absent, blank, failed lookup or
false disables reads/writes; true/1/yes/enabled enables them. Cancellation propagates. Configuration
events invalidate all combinations, plus the discovery endpoint's function catalog/schema domain.
Enable-setting events invalidate combinations even while disabled, so re-enable cannot recover a
pre-change result. Required errors propagate through the same durable retry policy as discovery.
An old schema factory stays in its captured generation. Tools and nested schemas are detached
before publication and cloned on every cache read. Authentication remains before endpoint cache
access; the function loader receives the caller's already selected configuration IDs.
