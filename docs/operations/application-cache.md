# Shared application cache

Gateway and Admin share the application cache composition. Discovery, Functions, Mappings,
Costs and PricingRules use FusionCache directly; Admin's discovery service remains optional.
See [the design and validation record](../decisions/0007-fusion-cache.md).
Authentication, tasks, spending, provider credentials, Data Protection and other Redis
stores retain their existing registrations and namespaces.

## Configuration and upgrade

Both hosts resolve Redis through the existing RedisUrlParser: REDIS_URL, then
CONDUIT_REDIS_CONNECTION_STRING. Without Redis, FusionCache uses its dedicated L1.
This cache behavior supports Admin and isolated cache tests; the normal Gateway runtime requires
Redis for its rate limits and spending services, including in single-node deployments.
Each warmed Redis cache owns three multiplexers: payload storage, backplane and generation
metadata. These do not reuse the host multiplexer or replace its IDistributedCache.
DI disposes the storage, backplane, metadata connection and dedicated L1.

Data Protection reuses Gateway's pooled host multiplexer. Admin registers its host multiplexer
lazily when Redis is configured. Both hosts configure key persistence through their final DI
provider; registration creates no Redis connections or temporary service providers. A configured
Redis key store does not silently fall back to local files when Redis is unavailable. Admin without
Redis continues to use the default local key store.

~~~json
{
  "ApplicationCache": {
    "Environment": "production",
    "LocalDuration": "00:00:05",
    "DistributedReadTimeout": "00:00:00.250",
    "MaximumDuration": "7.00:00:00",
    "Domains": {
      "Discovery": { "Enabled": true },
      "Functions": { "Enabled": true, "MaximumDuration": "06:00:00" },
      "Mappings": { "Enabled": true, "Duration": "00:10:00" },
      "Costs": { "Enabled": true, "Duration": "12:00:00" },
      "PricingRules": { "Enabled": true, "Duration": "00:15:00" }
    }
  }
}
~~~

Remove ApplicationCache:Implementations and CacheManager settings before upgrading.
Startup rejects either retired section, including an old disabled-region setting, rather
than silently dropping its policy. Translate old region Enabled and MaxTTL settings
to domain Enabled and MaximumDuration respectively:

| Former region | Domain |
| --- | --- |
| ModelDiscovery | Discovery |
| FunctionDiscovery | Functions |
| ModelMetadata | Mappings |
| ModelCosts | Costs |
| PricingRules | PricingRules |

Preserve effective cost/rule DefaultTTL overrides using Duration. Discovery retains
Discovery:CacheDurationMinutes (360 by default) unless its domain Duration is explicit.
Mapping duration defaults to ten minutes. Function duration still comes from the minimum
participating CacheTtlMinutes or an explicit request override; its domain accepts only
a maximum cap, not a fixed Duration. No configured function TTL means no cache write.
Functions.DiscoveryCacheEnabled is checked on every access: absent, blank, failed lookup
and false bypass caching; true/1/yes/enabled permit it. Cancellation propagates.
Discovery:EnableCaching=false also bypasses discovery. Domain Enabled=false bypasses
both reads and writes while required invalidation remains available.

Old Priority/EvictionPolicy settings have no domain equivalent; the dedicated L1 uses
FusionCache policies. Ignored manager/statistics options and cache tier switches are retired.
Migration preserves effective business TTL/enable contracts, not ignored configuration.

Use the same Environment in both hosts of a deployment and isolate environments. It
defaults to the lowercase host environment and accepts 1–64 ASCII letters, digits,
underscores and hyphens. Payloads and backplane use conduit:app-cache:{environment}:v1:.
The old host-specific application prefixes are not read by this version.

LocalDuration must be positive and at most five seconds; mappings, costs and rules further
cap payload L1 at 100ms. DistributedReadTimeout is positive and at most one second and
bounds storage/metadata reads. MaximumDuration is 12 hours–30 days (seven days by default).
Domain durations/caps must be positive and within that global maximum. L1 uses the smaller
local/domain duration; L2 promotion respects remaining lifetime. Tag markers last global
MaximumDuration plus one day, with a one-second L1. Five persistent generation keys have
no TTL. Do not expire their metadata manually as an operational invalidation procedure.

## Freshness and failure behavior

Fail-safe, eager refresh, timed-out factory completion and background storage/backplane
operations are disabled. Writes and invalidations are awaited; required errors are rethrown
as ApplicationCacheInvalidationException and scheduled through persisted Wolverine
retries at 1/5/30 seconds indefinitely. Monitor the retry backlog before declaring recovery.
Cancellation and business-loader errors retain their own contracts. A storage failure falls
back to the current business loader; a successful load is not repeated after a failed write.

Every factory captures a domain generation before loading. Invalidation changes that
persistent, clock-independent token, so a late old factory cannot repopulate the current
namespace. Missing metadata initializes a new token atomically. Discovery/functions refresh
local generation metadata within one second; mappings/costs/rules within 100ms. Tests verify
two-second discovery/function and one-second strict-domain convergence after successful
invalidation, including missed backplane delivery. Queue latency, outages and requests already
in flight are outside those bounds; this is not linearizable coordination.

Detected Redis disconnection bypasses cached generation metadata. After a strict-domain
storage failure, reads remain fenced until successful metadata recovery publishes a newly
rotated token. This forces a current business load even before a pending invalidation retries.
A later failure cannot be acknowledged by an earlier recovery. Failed recovery continues
database fallback. No detached writes or timestamp-based namespace resurrection are used.

Caller results are owned copies: discovery detaches JsonElements and copies containers;
functions copy tool/schema graphs; mappings/costs reconstruct complete versioned snapshots;
rules copy mutable collections. Redis mapping hits need no navigation-repair query. Provider
credentials and tracked/cyclic EF graphs are excluded from payloads. JSON uses generated
payload and FusionCache-envelope metadata with no reflection fallback. Add generated metadata
and real Redis/native coverage when introducing a new payload type.

Usable costs default to 12-hour L2 storage, shortened by expiry. Missing/unusable costs have
a one-minute negative contract, shortened by a known future effective date. Reads validate
active/effective/expiry even on a hit; missing costs preserve billing reconciliation errors
and configured zero rates remain valid. Administrative cost lists retain all rows. Rules
default to 15 minutes and use cost ID plus SHA-256 of configuration content; invalid JSON
returns null and is not written. Priced discovery has an internal transition deadline that
refreshes at effective/expiry boundaries without exposing metadata in HTTP responses.

Repricing expires costs, mappings, rules and discovery. The billing decorator is
Configuration.IModelCostService; IModelCostCache/RedisModelCostCache is an independent
auxiliary store. Batch cost requests await actual billing/dependent expiration before queue
acceptance; only that auxiliary store's expiration stays batched.

## Rollout and rollback

The serial isolated gates and the two-legacy-process bypass/clear/re-enable reproducer are
preserved at commit e7057ce5. They used real Redis, PostgreSQL durable transport, Admin
repricing, Gateway restarts and missed backplane delivery. They establish fixture evidence;
no production deployment or operator observation window was performed by this change.

This final version has no implementation selector. To bypass a domain, set its Enabled
to false on every serving process and restart. To return to the old engine, deploy the old
binary with its old configuration. Drain or suspend affected requests while clearing caches:
a binary switch alone can expose stale legacy prefixes left behind before the upgrade.

Before legacy reads resume, inventory every serving legacy process and its complete old
application-domain keyspace. Bypass legacy reads where supported (for discovery,
Discovery:EnableCaching=false); an old region write-disable setting alone does not prove
read bypass. Keep traffic drained where a read bypass is unavailable. Clear the entire
affected legacy application domain on every instance and its verified distributed keys,
restart those processes to discard old L1, and only then resume reads. Cover all variants,
not sampled keys. Never flush shared Redis or remove a generic host prefix containing auth,
tasks or other stores. Drain/replay pending durable invalidations against the selected binary.
The archived fixture proves discovery rollback on its complete known legacy keyspace;
operators must validate their actual topology and all domains being rolled back.

## Monitoring and reproduction

conduit_application_cache_operations_total uses bounded domain/operation/result labels.
Watch hits/misses, factory business errors, Redis/serialization errors, invalidation failures
and durable retries. These count cache operations, not HTTP requests or exact resident entries;
the statistics contract does not enumerate L1 or scan Redis.

A warmed node adds three multiplexers/six sockets. The matched five-payload fixture occupied
6768 Redis bytes including five generation keys versus 4128 legacy bytes (1.64×). Old
generation payloads and tag markers expire naturally; repeated invalidation can temporarily
increase resident memory across the maximum payload TTL. Monitor memory and connection
counts under deployment load. L1 allocation remains 2.6–4.2KB per fixture call and exceeds
the original allocation target; the design record documents this explicit review tradeoff.

Follow [the probe README](../../tools/ConduitLLM.CacheProbe/README.md) for local, Redis,
NativeAOT and independent-process probes. Set CONDUIT_CACHE_TEST_REDIS and
CONDUIT_CACHE_TEST_POSTGRES to isolated fixtures for distributed tests. The durable
test creates/removes only its uniquely named database and interrupts only its own proxy
connections; its PostgreSQL login must permit database creation. Production must never be
used as a fault-injection fixture.
