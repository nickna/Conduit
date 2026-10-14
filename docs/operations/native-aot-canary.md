# NativeAOT image, benchmark, and canary runbook

## Current decision: promotion deferred

Admin and Gateway NativeAOT images are release candidates, not production defaults.
[ADR 0006](../decisions/0006-native-aot-persistence.md) keeps the production data
plane on JIT while EF Core NativeAOT query execution is experimental. The Gateway's
declared native feature matrix now process-tests authentication, provider HTTP/SSE,
request accounting, async tasks, S3 media, PostgreSQL/Wolverine, Redis, and JSON
SignalR paths. Published native tests also prove Wait-mode schema startup and live
PostgreSQL/Redis readiness failure and recovery. It continues to exclude MessagePack,
broad EF query execution and the Redis virtual-key authentication cache. Healthy
schema/dependency readiness does not establish full Admin or background-worker parity.

The first-party linker ratchet is now empty. Promotion nevertheless remains blocked
because the EF-backed Admin path is not approved for production NativeAOT and no
digest-pinned production benchmark, ordered soak, or rollback evidence has been
collected. A synthetic benchmark alone cannot clear these blockers.

The release workflow therefore publishes only immutable candidate tags:

```text
ghcr.io/nickna/conduit-admin-native:<version>-candidate-<run_id>-<run_attempt>
ghcr.io/nickna/conduit-http-native:<version>-candidate-<run_id>-<run_attempt>
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

The release-owned migrator must prepare the database before either application
starts. Run digest-pinned JIT and native images with the same OCI source revision,
configuration, PostgreSQL, Redis, Wolverine and deterministic provider fixture on
an otherwise idle Linux x64 Docker host. Pull both exact digests before measurement;
the collector inspects locally available images and does not pull them. It uses
`CONDUIT_MIGRATION_MODE=Wait`; `/health/ready` measures cold readiness only.

Build the workload probe separately and pass its published native executable or
managed DLL through `-WorkloadProbePath`. The collector never builds the probe or
seeds a database. Use existing benchmark credentials, settings and model mappings
from an isolated staging fixture. Gateway runs incur ordinary request accounting;
provide sufficient budgets and rate limits for the warm-up and all measured runs.
A passing driver proves its bounded HTTP contract, not every background-worker path.

Store a private JSON workload configuration for each required workload. Common
fields are `workload`, `baseUrl`, `requests`, `concurrency`, `warmupRequests` and
`timeoutSeconds`. Admin adds `settingKey` and `expectedValue`. Each Gateway file
adds `model`, `prompt`, `expectedContent`, `expectedPromptTokens` and
`expectedCompletionTokens`. Use exactly one `admin-setting-read` file for Admin,
and one `gateway-chat-nonstream` plus one `gateway-chat-sse` file for Gateway.
The collector replaces only `baseUrl` with its owned container's loopback origin.
The driver checks exact Admin values or provider content and usage through full
response completion, including SSE's terminal event and EOF.

Supply authentication only through `CONDUIT_NATIVE_BENCHMARK_API_KEY`: the Admin
master key for Admin or an existing virtual key for Gateway. Do not put credentials
in workload JSON. The collector gives the key to its private child process and
uses an owned temporary Docker environment file for container secrets. For Admin,
it also installs that key as the container backend master key. A Gateway virtual
key is never installed as a backend master key.

Use optional `-ContainerEnvironmentPath` for an existing private Docker environment
file with application configuration and backend credentials. Use UTF-8 NAME=value
entries without quotes or duplicate names, up to 64 KiB. Both variants receive
identical settings. The collector owns the PostgreSQL/Redis URLs, Wait-mode startup,
Wolverine provisioning setting and its container port; those mandatory values
override file entries. Never include `CONDUIT_NATIVE_BENCHMARK_API_KEY` in this file.

Provide `-EnvironmentEvidencePath` containing this credential-free metadata:

```json
{
  "schemaVersion": 1,
  "environmentId": "<staging-host-and-run-identifier>",
  "host": {
    "os": "linux",
    "architecture": "x64",
    "cpu": "<CPU model>",
    "logicalProcessors": 8,
    "memoryBytes": 17179869184,
    "dockerVersion": "<measured version>",
    "kernelVersion": "<measured version>"
  },
  "backingServices": {
    "postgresqlVersion": "<measured version>",
    "redisVersion": "<measured version>",
    "wolverineVersion": "<application package version>",
    "configurationId": "<configuration identifier without secrets>",
    "providerFixtureId": "<Gateway deterministic provider identifier or not-applicable for Admin>"
  }
}
```

```powershell
./scripts/aot/measure-native-container.ps1 `
  -Service admin `
  -JitImage ghcr.io/nickna/conduit-admin@sha256:<jit-digest> `
  -NativeImage ghcr.io/nickna/conduit-admin-native@sha256:<native-digest> `
  -DatabaseUrl $env:DATABASE_URL `
  -RedisUrl $env:REDIS_URL `
  -WorkloadProbePath /opt/conduit/probe/ConduitLLM.GatewayNativeAotTests `
  -WorkloadConfigurationPaths /secure/benchmark/admin-setting-read.json `
  -EnvironmentEvidencePath /secure/benchmark/environment.json `
  -ContainerEnvironmentPath /secure/benchmark/runtime.env `
  -PairedRuns 5 `
  -NativePackagingDirectory artifacts/native-image/admin/packaging-<owner> `
  -OutputPath artifacts/native-benchmark/admin.json
```

For Gateway, use `-Service gateway`, the corresponding Gateway image digests and
both workload configuration paths. Request counts, concurrency, warm-up counts
and deadlines come from those files; the old `-Requests` and `-Concurrency`
collector arguments are rejected. The same workload inputs run against both
images. Each report retains input fingerprints without publishing credentials,
request bodies, expected private content or backing-service connection strings.

The collector runs one unrecorded warm-up pair, then an odd number of recorded
pairs from five through 21. It alternates JIT/native order, retains every raw
sample and computes each metric's median. Schema 4 separates common image,
compressed pull, cold-readiness and idle/steady/peak memory metrics from each
workload's throughput and full-response p50/p95/p99 latency. Native publish time
and artifact sizes come from the completed schema-2 packaging bundle for the
exact requested native image reference. The collector replays its original OCI
archive, embedded publish manifests and runtime payload, requires a clean Git
revision matching the declared commit and OCI revision, and retains the build ID,
toolchain, executable hashes, runtime inventory hash and all three manifest hashes.
It rejects the old service-only `-NativeBaselinePath` argument. Both container
variants run by their observed immutable local image IDs on Linux/amd64. Every
native sample records its actual container ID and verifies the packaged selected
manifest before and after workloads; cleanup still uses verified ownership.
After collecting metrics, the collector stops each owned host with a bounded
deadline (30 seconds by default, configurable with `-ShutdownTimeoutSeconds`).
It requires a stopped host with exit code zero before ordinary removal. A failed
or forced shutdown is reaped by its verified identity and fails the attempt.
For accounting workloads, retain independent database and Redis settlement
checks: a successful shutdown alone does not prove that every request log or
charge was persisted. Preflight requests must reach their persistence oracle
while their host is still running.
The native median and publish metrics retain the same sealed image binding.
Retain the entire packaging directory with the benchmark. The evaluator accepts
its relocated directory explicitly and replays it independently. Requested
repository/index digests, selected manifests, image configs and Docker local IDs
retain their distinct roles. Historical schema-1/2/3 benchmarks and packaging
bundles without a verified OCI association or clean source remain HOLD evidence.
This association does not establish production provenance or complete a canary.
Every collection starts a versioned attempt before preflight validation. The
collector holds an exclusive writer lock in `<OutputPath>.attempts`, records
`latest.json` and retains each GUID attempt's `attempt.json` and successful
`benchmark.json`. Successful reports are flushed and published atomically before
the attempt records success. A failed rerun preserves the previous report while
the new failed or running attempt disqualifies it as the latest result. Reports
created before attempt tracking are retained by their original hash for inspection.
Retain the entire attempt directory with the public report. The evaluator requires
schema 4, exact original report/archive hashes and matching successful attempt
identity and timestamps. Its default selection requires the latest successful
attempt. It rechecks the attempt after image and metric evaluation; an active
writer or changed attempt holds promotion. Archived selection requires the
explicit approval described below; legacy hash-named inspection copies cannot qualify.

Known private-command failures append a fixed classification to the existing
controlled error message:

| Tag | Meaning |
| --- | --- |
| `[stage=command-deadline]` | The command exceeded its own deadline. |
| `[stage=poll-command-deadline]` | Its poll callback propagated a nested command deadline. |
| `[stage=poll-memory-sample]` | The memory sampler reported a command or output-format failure. |
| `[stage=poll-memory-value]` | The memory sampler reported an invalid numeric value. |
| `[stage=poll-callback]` | Another poll callback failed. |

Unknown failures retain the generic message, and cleanup failure retains its
separate controlled wording. Tags exclude private exception text, arguments,
environment values and captured output. Polling intervals, deadlines, output
caps and process cleanup remain unchanged; a callback failure still terminates
its owned workload and fails collection. Preserve earlier unclassified attempts:
a later tag cannot establish their cause. Full persisted per-phase diagnostics
remain tracked in [#1642](https://github.com/nickna/Conduit/issues/1642).

The evaluator independently recomputes the medians and comparisons, validates
complete zero-failure workloads and identical load parameters and fingerprints
across every pair, and applies throughput and p99 thresholds separately to each
workload. Health-only schema 1 reports, missing or changed samples, different
source revisions, changed image/build bindings, incomplete metadata and the
unfilled benchmark template fail.

No production comparison has been accepted yet. Published native smoke and
workload-driver correctness tests do not supply operational improvement,
background-worker parity, ordered soak or rollback evidence.

The [2026-10-12 controlled Admin comparison](native-aot-admin-controlled-benchmark.md)
retains an actual five-pair measurement and its original report. Startup and memory
improved, while median throughput fell 53.53% and every recorded pair had lower
native throughput. This mixed engineering baseline leaves promotion deferred and
does not replace a representative production comparison or the ordered canaries.

The [2026-10-12 controlled Gateway comparison](native-aot-gateway-controlled-benchmark.md)
retains five authenticated JSON/SSE pairs and exact provider/financial settlement.
Native JSON throughput rose 9.08%, while complete-SSE throughput fell 4.18% and its
median per-run p99 increased 7.70%. Image size, startup and sampled memory improved.
The streaming results leave both the no-throughput-loss and 5% p99 criteria unmet;
production promotion remains deferred.

## Promotion gate

The machine-readable policy is
`deploy/native-canary/promotion-policy.json`. It is deliberately `blocked`; changing
it to `open` is a reviewed production decision, not a release-script side effect.
When the blockers are resolved, populate a copy of `evidence.template.json` from the
deployment and monitoring systems and record the accepted qualification timeline,
then evaluate it with the benchmark:

```powershell
./scripts/aot/evaluate-native-promotion.ps1 `
  -Service admin `
  -BenchmarkPath artifacts/native-benchmark/admin.json `
  -NativePackagingDirectory artifacts/native-image/admin/packaging-<owner> `
  -CanaryEvidencePath artifacts/native-canary/admin-evidence.json `
  -QualificationEvidencePath artifacts/native-canary/qualification.json

./scripts/aot/evaluate-native-promotion.ps1 `
  -Service gateway `
  -BenchmarkPath artifacts/native-benchmark/gateway.json `
  -NativePackagingDirectory artifacts/native-image/gateway/packaging-<owner> `
  -CanaryEvidencePath artifacts/native-canary/gateway-evidence.json `
  -AdminCanaryEvidencePath artifacts/native-canary/admin-evidence.json `
  -AdminBenchmarkPath artifacts/native-benchmark/admin.json `
  -QualificationEvidencePath artifacts/native-canary/qualification.json
```

The gate requires at least a 10% image reduction, 20% cold-readiness reduction, 15%
idle-memory reduction, no throughput loss, no more than 5% p99 regression, error
rate at or below 0.1%, 24 consecutive healthy monitoring windows, full feature
coverage, and tested automatic and manual rollback. Admin must complete its seven-day
soak before a Gateway canary starts. Gateway then completes its own seven-day soak
with every row declared supported in `gateway-native-aot-feature-matrix.md` passing.
The evaluator also requires at least five digest-pinned zero-failure benchmark pairs,
matching source revisions, host and backing-service metadata, every raw common and
workload measurement, and a linux-x64 native publish baseline from the measured image
build. Dashboard and alert links, critical-vulnerability review and verified
SBOM/provenance attestations remain mandatory. It independently recalculates medians
and comparison percentages and rejects incomplete reports or changed request counts,
concurrency, warm-ups, deadlines and input fingerprints. Throughput and p99 thresholds
apply to each workload separately. Claimed soak hours cannot exceed recorded timestamps.

## Accepted qualification window and archived evidence

Start from `deploy/native-canary/qualification.template.json`. The pending template
always holds promotion. An accepted schema-1 record contains its kind
`conduit-native-benchmark-qualification`, a nonzero lowercase GUID in N format as
`qualificationId`, `status: "accepted"`, UTC `windowStartedAt` and `windowEndsAt`,
and exactly `admin` and `gateway` entries in `services`. The Gateway entry can
remain null while only Admin evidence has been accepted. Each accepted service
entry contains exactly these fields:

```json
{
  "selection": "latest",
  "attemptId": "<successful attempt GUID in N format>",
  "reportSha256": "<original benchmark file SHA-256>",
  "canaryEvidenceSha256": "<original canary evidence file SHA-256>",
  "latestAttemptId": "<acknowledged latest attempt GUID in N format>",
  "latestAttemptSha256": "<original acknowledged attempt.json SHA-256>",
  "acceptedAt": "<UTC time when this service's evidence was accepted>",
  "acceptedBy": "<reviewer identity>"
}
```

Use lowercase hashes of the original file bytes, rather than reformatted JSON.
For latest selection, the successful and acknowledged latest attempt IDs must be
identical. Record acceptance after both the selected collection and canary have
completed. All timestamps must use UTC with `Z` or `+00:00`.

Choose the evidence window explicitly during review. It must cover the selected
collection, canary and acceptance times, and evaluation must occur inside it.
Gateway requires both services' evidence in that same window, with Admin's
acceptance recorded before the Gateway canary starts. The actual ordered
168-hour soaks, and any gap between them, determine the required coverage; the
gate does not invent a fixed age limit. A later Admin collection attempt also
invalidates the prior Admin acknowledgment used for Gateway qualification.
Extend or replace a window only through a new review of the retained evidence.

The committed `historicalEvidencePolicy` is `latest-only`. A reviewed policy may
explicitly select `approved-archive` while preserving every other promotion gate.
For a historical service entry, set `selection` to `historical`, identify the
successful archived attempt in `attemptId` and `reportSha256`, and acknowledge the
current failed attempt in `latestAttemptId` and `latestAttemptSha256`. The failed
attempt must follow the selected success, lie inside the accepted window and
complete before acceptance. The evaluator reads the selected GUID's immutable
`benchmark.json` directly. `BenchmarkPath` remains the public output path locating
its `.attempts` directory, even if a failed publication left that public file
malformed or obstructed. Supplying an archive path or a legacy inspection copy
does not establish approval.

A running attempt, active writer, subsequent attempt or changed acknowledgment
always holds promotion, including under the archive policy. The evaluator uses
bounded strict JSON, checks the original report, canary and qualification hashes,
then replays those records and the policy after OCI validation. It checks window
expiry again before returning its decision. The acceptance record documents a
reviewer's decision; it does not authenticate that identity or change the blocked
production policy. No production qualification, soak or rollback is claimed by
the repository's synthetic contract tests.

## Canary procedure

1. Record the JIT release digest and database schema version. Run the migrator before
   either application starts; a canary must never mutate schema.
2. Start the corresponding JIT image and native candidate digest side-by-side with
   the same configuration and backing services. `deploy/native-canary/compose.yml`
   is a local/staging harness; use the production orchestrator's equivalent security
   and availability controls for the actual canary. All four harness cohorts use
   `CONDUIT_MIGRATION_MODE=Wait` so readiness verifies the schema version without
   applying migrations; the standalone migrator remains release-owned.
3. Exercise contracts against JIT first, then shadow safe/read-only traffic to native.
   Do not mirror writes unless the workload supplies idempotency and isolates effects.
4. Route 1% of eligible traffic to Admin native. Advance through 5%, 25%, 50%, and
   100% only after the required healthy windows at each step. Start Gateway only
   after Admin's full soak is accepted.
5. Store benchmark JSON, candidate/JIT digests, dashboard and alert links, feature
   results, alert history, rollback evidence, complete attempt directories and
   the accepted qualification record together. Evaluate the policy; a nonzero
   result is a hold.

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
