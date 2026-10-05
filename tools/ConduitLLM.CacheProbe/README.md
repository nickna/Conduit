# Cache compatibility probe

FC-1 of [epic #1396](https://github.com/nickna/Conduit/issues/1396). See
[the design record](../../docs/decisions/0007-fusion-cache.md) for contracts and evidence.

The executable uses actual Conduit payloads and fails on violated contracts. It does not
enable production FusionCache. JSON reflection is disabled in JIT and native builds.

Run local composition from the repository root:

```powershell
dotnet run --project tools/ConduitLLM.CacheProbe -c Release
```

For Redis and PostgreSQL, start dedicated empty fixtures (these ports avoid the usual
development services; all published ports bind only to loopback):

```powershell
docker run -d --name conduit-1396-redis -p 127.0.0.1:16396:6379 redis:7.4.2-alpine
docker run -d --name conduit-1396-postgres -p 127.0.0.1:15396:5432 -e POSTGRES_DB=cacheprobe -e POSTGRES_USER=cacheprobe -e POSTGRES_PASSWORD=cacheprobe postgres:16
$env:CONDUIT_CACHE_PROBE_REDIS = '127.0.0.1:16396'
$env:CONDUIT_CACHE_PROBE_POSTGRES = 'Host=127.0.0.1;Port=15396;Database=cacheprobe;Username=cacheprobe;Password=cacheprobe'
dotnet run --project tools/ConduitLLM.CacheProbe -c Release
```

The DB fixture creates schema and seeds one mapping if absent; it must be a dedicated
empty database. The probe never deletes a database or flushes Redis. Its default unique
cache namespace expires naturally. INFO and MEMORY instrumentation is read-only.
Connection strings are never printed. Omit PostgreSQL to run only Redis scenarios.
Use `dotnet run --project tools/ConduitLLM.CacheProbe -c Release -- compose` to validate
the production registered composition, generated serializer, telemetry, ownership and shared
Admin/Gateway namespace instead of the standalone compatibility scenario.

Publish and test the native cache/serialization path (EF baseline code is excluded):

```powershell
dotnet publish tools/ConduitLLM.CacheProbe -c Release -r win-x64 -p:PublishAot=true -m:1 -nr:false -o artifacts/cache-probe/native
./artifacts/cache-probe/native/ConduitLLM.CacheProbe.exe
$env:CONDUIT_CACHE_PROBE_PREFIX = 'conduit:cache-probe:restart-test:'
./artifacts/cache-probe/native/ConduitLLM.CacheProbe.exe write
./artifacts/cache-probe/native/ConduitLLM.CacheProbe.exe read
```

Keep Redis configured for the independent-process check. Other platforms can publish for
their native RID; this spike's recorded execution evidence is Windows x64. The full EF
service graph can produce existing linker warnings; the focused cache serializer has no
reflection fallback and introduces no FusionCache-specific linker diagnostics.

Stop/remove only these task-owned containers when finished:

```powershell
docker rm -f conduit-1396-redis conduit-1396-postgres
```
