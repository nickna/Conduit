# NativeAOT image, benchmark, and canary runbook

## Current decision: promotion deferred

Admin and Gateway NativeAOT images are release candidates, not production defaults.
[ADR 0006](../decisions/0006-native-aot-persistence.md) keeps the production data
plane on JIT while EF Core NativeAOT query execution is experimental. The Gateway's
declared native feature matrix now process-tests authentication, provider HTTP/SSE,
request accounting, async tasks, S3 media, PostgreSQL/Wolverine, Redis, and JSON
SignalR paths. It continues to exclude MessagePack, broad EF query execution, the
Redis virtual-key authentication cache, and full readiness/database-health parity.

The first-party linker ratchet is now empty. Promotion nevertheless remains blocked
because the EF-backed Admin path is not approved for production NativeAOT and no
digest-pinned production benchmark, ordered soak, or rollback evidence has been
collected. A synthetic benchmark alone cannot clear these blockers.

The release workflow therefore publishes only immutable candidate tags:

```text
ghcr.io/nickna/conduit-admin-native:<version>-candidate
ghcr.io/nickna/conduit-http-native:<version>-candidate
```

It never gives these images a version, `latest`, or `beta` deployment tag. Existing
JIT candidates continue through the migration and promotion jobs independently.
Changing that boundary requires a successor persistence ADR and every parent-epic
acceptance criterion to pass.

## Image contract

Both native Dockerfiles target only `linux/amd64` (`linux-x64`) and use the .NET 10
Ubuntu chiseled-extra runtime-dependencies image. This retains certificates, ICU,
and time-zone data without carrying the ASP.NET runtime, SDK, shell, or package
manager. The image runs as the image-defined non-root `APP_UID`, starts the native
executable directly, and uses a separately compiled native health probe.

CI runs `scripts/aot/verify-native-image.ps1` to reject a wrong architecture, root
user, indirect entrypoint, missing health check, SDK/Roslyn/EF design files, PDBs, or
native debug files. Release builds add OCI version, revision, and creation labels,
BuildKit SBOM and provenance attestations, and a blocking Trivy scan for critical
vulnerabilities. Debug symbols come only from the Docker `symbols` target and are
retained for 14 days as a GitHub Actions artifact; they are never pushed in the
runtime layer.

Build and verify locally:

```powershell
docker build --file Services/ConduitLLM.Admin/Dockerfile.native --tag conduit-admin-native:local --build-arg TARGETARCH=amd64 .
./scripts/aot/verify-native-image.ps1 -Image conduit-admin-native:local -Service admin

docker build --file Services/ConduitLLM.Gateway/Dockerfile.native --tag conduit-http-native:local --build-arg TARGETARCH=amd64 .
./scripts/aot/verify-native-image.ps1 -Image conduit-http-native:local -Service http
```

Do not add `linux-arm64` until that RID is published and receives the same process,
image-content, protocol, and canary coverage.

## Reproducible comparison

The database must already be migrated by the release-owned migrator. Run the JIT and
native images on an otherwise idle Linux Docker host, pinned by digest and backed by
the same PostgreSQL and Redis instances:

```powershell
./scripts/aot/measure-native-container.ps1 `
  -Service admin `
  -JitImage ghcr.io/nickna/conduit-admin@sha256:<jit-digest> `
  -NativeImage ghcr.io/nickna/conduit-admin-native@sha256:<native-digest> `
  -DatabaseUrl $env:DATABASE_URL `
  -RedisUrl $env:REDIS_URL `
  -NativeBaselinePath artifacts/native-aot/reports/native-baselines.json `
  -OutputPath artifacts/native-benchmark/admin.json
```

The report records image and compressed pull sizes, cold readiness, idle,
steady-state, and peak working set, throughput, p50/p95/p99 latency, failures, and
the native publish time/artifact size from the Phase 0 baseline. Run at least five
times after one unrecorded warm-up and retain the median report plus host CPU, RAM,
Docker, kernel, PostgreSQL, and Redis versions with the canary evidence. Do not
compare runs from unlike hosts. `-NativeBaselinePath` is required for a promotion
decision even though the measurement script permits a benchmark-only run without it.

No production comparison is accepted yet. The existing ~102–103 MiB native
executable baseline did not exercise the production database workload, so it cannot
justify promotion. Promotion is deferred because of that missing workload parity and
ADR 0006, not because an unmeasured benefit is assumed.

## Promotion gate

The machine-readable policy is
`deploy/native-canary/promotion-policy.json`. It is deliberately `blocked`; changing
it to `open` is a reviewed production decision, not a release-script side effect.
When the blockers are resolved, populate a copy of `evidence.template.json` from the
deployment and monitoring systems, then evaluate it with the benchmark:

```powershell
./scripts/aot/evaluate-native-promotion.ps1 `
  -Service admin `
  -BenchmarkPath artifacts/native-benchmark/admin.json `
  -CanaryEvidencePath artifacts/native-canary/admin-evidence.json

./scripts/aot/evaluate-native-promotion.ps1 `
  -Service gateway `
  -BenchmarkPath artifacts/native-benchmark/gateway.json `
  -CanaryEvidencePath artifacts/native-canary/gateway-evidence.json `
  -AdminCanaryEvidencePath artifacts/native-canary/admin-evidence.json
```

The gate requires at least a 10% image reduction, 20% cold-readiness reduction, 15%
idle-memory reduction, no throughput loss, no more than 5% p99 regression, error
rate at or below 0.1%, 24 consecutive healthy monitoring windows, full feature
coverage, and tested automatic and manual rollback. Admin must complete its seven-day
soak before a Gateway canary starts. Gateway then completes its own seven-day soak
with every row declared supported in `gateway-native-aot-feature-matrix.md` passing.
The evaluator also requires digest-pinned zero-failure benchmark inputs; every raw
size, readiness, memory, throughput, and latency measurement; a matching linux-x64
native publish baseline; matching JIT and backing-service evidence; dashboard and
alert links; critical-vulnerability review; and verified SBOM/provenance attestations.
It independently recalculates every comparison percentage and rejects inconsistent or
incomplete reports. Claimed soak hours cannot exceed the recorded timestamps.

## Canary procedure

1. Record the JIT release digest and database schema version. Run the migrator before
   either application starts; a canary must never mutate schema.
2. Start the corresponding JIT image and native candidate digest side-by-side with
   the same configuration and backing services. `deploy/native-canary/compose.yml`
   is a local/staging harness; use the production orchestrator's equivalent security
   and availability controls for the actual canary.
3. Exercise contracts against JIT first, then shadow safe/read-only traffic to native.
   Do not mirror writes unless the workload supplies idempotency and isolates effects.
4. Route 1% of eligible traffic to Admin native. Advance through 5%, 25%, 50%, and
   100% only after the required healthy windows at each step. Start Gateway only
   after Admin's full soak is accepted.
5. Store benchmark JSON, candidate/JIT digests, dashboard and alert links, feature
   results, alert history, and rollback evidence together. Evaluate the policy; a
   nonzero result is a hold.

## Dashboard and alerts

Every panel is split by `service`, `runtime=jit|native-aot`, candidate digest,
deployment, and route where applicable. The canary dashboard contains:

- replica health, restarts, readiness, rollout percentage, and candidate age;
- request rate, 4xx/5xx/error ratio, throughput, and p50/p95/p99 server duration;
- process RSS/working set, CPU, thread count, handles/file descriptors, and GC/runtime
  counters available from the native process;
- PostgreSQL latency/errors/pool pressure, Redis latency/errors, Wolverine queue
  depth/dead letters, provider failures, streaming disconnects, and storage failures;
- JIT/native differential panels for every threshold in the promotion policy.

Page and automatically set native traffic weight to zero for any of these conditions:

- native readiness is failing for two consecutive checks or crash looping;
- five-minute error ratio exceeds 0.1% or is more than 0.05 percentage points above JIT;
- p99 exceeds the JIT cohort by 5% for three consecutive five-minute windows;
- PostgreSQL, Redis, or Wolverine error/dead-letter rates rise above the JIT cohort;
- supported-contract, authentication, storage, streaming, or provider synthetic fails;
- memory grows for 30 minutes without reaching steady state, or CPU/memory exceeds
  the JIT cohort by 20% for 15 minutes;
- a critical security finding or contract mismatch is detected.

Alert telemetry and traffic-control actions must remain functional when the native
replica is unresponsive. A human can hold or roll back for any unexplained divergence;
passing numerical thresholds never overrides a correctness concern.

## Rollback and rehearsal

Automatic rollback sets native traffic weight to zero, keeps the healthy JIT cohort
at the recorded digest, and pages the release owner. Manual rollback performs the
same traffic change and scales native replicas to zero; if necessary, redeploy the
recorded JIT digest. Never run a down migration. Database changes follow the existing
expand/contract policy so the corresponding JIT image remains compatible.

Rehearse both paths before recording a soak as successful: inject a failing native
readiness check and verify automatic removal, then manually restore the recorded JIT
digest and run health, OpenAPI/contract, PostgreSQL, Redis, and Wolverine smoke tests.
Record timestamps, alert IDs, traffic-controller events, digests, and test results in
the evidence JSON. The repository provides the procedure and gate, but does not claim
that a production rollback or soak has occurred.

Keep building and testing JIT images throughout the first complete native release
cycle. Native can become the default only after the policy is open, both ordered
soaks pass, rollback evidence is accepted, and every acceptance criterion in #1368
is complete.
