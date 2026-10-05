# Application cache contract probe

This executable exercises the production FusionCache services for
[epic #1396](https://github.com/nickna/Conduit/issues/1396), with JSON reflection disabled.
See [the design record](../../docs/decisions/0007-fusion-cache.md) for policies,
historical comparison and performance tradeoffs, and
[operations](../../docs/operations/application-cache.md) for upgrade/rollback.

## Local and Redis execution

Run all five domains and shared composition from the repository root:

~~~powershell
dotnet run --project tools/ConduitLLM.CacheProbe -c Release
~~~

For Redis and PostgreSQL, start dedicated fixtures. All ports bind only to loopback:

~~~powershell
docker run -d --name conduit-1396-redis -p 127.0.0.1:16396:6379 redis:7.4.2-alpine
docker run -d --name conduit-1396-postgres -p 127.0.0.1:15396:5432 -e POSTGRES_DB=cacheprobe -e POSTGRES_USER=cacheprobe -e POSTGRES_PASSWORD=cacheprobe postgres:16
$env:CONDUIT_CACHE_PROBE_REDIS = '127.0.0.1:16396'
$env:CONDUIT_CACHE_PROBE_POSTGRES = 'Host=127.0.0.1;Port=15396;Database=cacheprobe;Username=cacheprobe;Password=cacheprobe'
dotnet run --project tools/ConduitLLM.CacheProbe -c Release
~~~

Use a dedicated empty DB: the JIT probe creates its schema and seeds one mapping if absent.
It never deletes a database or flushes Redis. Payloads use unique environments and expire
naturally; generation metadata is persistent. Remove the dedicated fixture afterward.
Connection strings are never printed. Omit PostgreSQL for cache-only checks.

Default mode is all. Individual modes are compose, discovery, functions, mappings and pricing.
Composition verifies generated serialization, ownership, shared Admin/Gateway namespace,
backplane tag propagation and bounded telemetry without replacing the host distributed store.
Domain modes validate 32 simultaneous misses/one factory, owned payloads, invalidation and
complete independent L2 reads. Functions use an enabled-setting fixture; service tests cover
repository/global-toggle/MCP behavior. Pricing covers positive, missing, lists, expiry and rules.

With PostgreSQL configured, JIT all also verifies actual repository cold/L1/restarted-L2
query counts: discovery 1/0/0, mappings 1/0/0, model-identifier billing 2/0/0. The existing
billing loader executes a count and data query on a cold load. Native builds exclude EF
schema creation and this database counter; they exercise actual cache DTO/serializer paths.

## Native and independent processes

~~~powershell
dotnet publish tools/ConduitLLM.CacheProbe -c Release -r win-x64 -p:PublishAot=true -p:ConduitAotAudit=true -m:1 -nr:false -o artifacts/cache-probe/native
./artifacts/cache-probe/native/ConduitLLM.CacheProbe.exe
$env:CONDUIT_CACHE_PROBE_REDIS = '127.0.0.1:16396'
$env:CONDUIT_CACHE_PROBE_ENVIRONMENT = 'cache-restart-fixture'
./artifacts/cache-probe/native/ConduitLLM.CacheProbe.exe discovery-write
./artifacts/cache-probe/native/ConduitLLM.CacheProbe.exe discovery-read
~~~

Repeat the write/read pair for functions-write/functions-read, mappings-write/mappings-read,
and pricing-write/pricing-read, with the same isolated environment in each pair. These
processes verify complete generated Redis round trips without a business-loader repair.
Use a fresh environment when rerunning a pair. Native execution evidence is Windows x64;
other platforms can publish their own RID. Existing full-link EF warnings remain outside
the focused cache slice; the production analyzer audit must pass without baseline relaxation.

## Durable fault and recovery gates

~~~powershell
$env:CONDUIT_CACHE_TEST_REDIS = '127.0.0.1:16396'
$env:CONDUIT_CACHE_TEST_POSTGRES = 'Host=127.0.0.1;Port=15396;Database=cacheprobe;Username=cacheprobe;Password=cacheprobe'
dotnet test Tests/ConduitLLM.Tests --filter 'FullyQualifiedName~Core.Caching'
~~~

The distributed fixture starts independent Admin/two Gateway hosts, updates through the
actual Admin service, disconnects only its proxy sockets, verifies a persisted scheduled
retry, restarts both Gateways and verifies the original message ID. It also covers missed
backplane delivery, duplicate events, current discovery/billing fallback and strict recovery.
It creates/removes only its uniquely named database; the fixture login must permit creation.
Other cache suites cover late loaders, metadata loss, recovery publication races, mutable
ownership, effective/expiry transitions, dependency routing and request cancellation.

## Archived rollout comparison

Commit e7057ce5 preserves the temporary RolloutBenchmark, stages 0–5 host-selection tests
and complete two-legacy-process discovery rollback reproducer before compatibility removal.
To reproduce that historical gate, use an isolated checkout of that commit and dedicated
fixtures; its README and benchmark modes belong to that version. The current branch has
retired the legacy engine, temporary selectors, raw entity metadata and comparison harness.
Retained tests assert domain behavior and real failure contracts.

The design record reports matched warmed latency, allocation, Redis commands, resident
memory and socket overhead. All latency/L2 targets passed; L1 allocation exceeded its
original target and remains an explicit correctness/performance tradeoff for PR review.
These fixture results do not claim a production rollout or deployment capacity.

Remove only task-owned fixtures when finished:

~~~powershell
docker rm -f conduit-1396-redis conduit-1396-postgres
~~~
