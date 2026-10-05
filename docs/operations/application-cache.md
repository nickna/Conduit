# Shared application cache

Implementation: [epic #1396 design record](../decisions/0007-fusion-cache.md).
FC-2 adds composition; all production domains still select their legacy implementations.
Per-domain selectors take effect as each gated migration lands. Authentication, tasks,
spending, provider credentials, ephemeral keys, Data Protection and other Redis stores
keep their existing registration and namespaces.

## Configuration

Both Gateway and Admin use the existing `RedisUrlParser` resolver (`REDIS_URL`, then
`CONDUIT_REDIS_CONNECTION_STRING`). They independently own a dedicated application
RedisCache connection and Redis backplane connection. Neither is the host's shared
IConnectionMultiplexer. DI disposes the RedisCache; FusionCache unsubscribes and disposes
its backplane and dedicated L1. The application store is a keyed IDistributedCache and
does not replace the normal host-wide IDistributedCache.

```json
{
  "ApplicationCache": {
    "Environment": "production",
    "LocalDuration": "00:00:05",
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
use a one-second L1 duration. Short local lifetimes alone are not a complete outage/race
policy; FC-4 must prove bypass, fencing and rollback before broad cutover.

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

Auto-cloning prevents callers from mutating cached results on read. Domain loaders must
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
