# Controlled Admin NativeAOT comparison, 2026-10-12

The selected Admin native profile started faster and used less container memory
than the default JIT profile in this controlled comparison. Its median throughput
was 53.53% lower, and throughput was lower in every recorded pair. Production
promotion remains deferred. These results describe this fixture and environment;
they do not establish production capacity or isolate the cause of the difference.

## Original measurement

The [original schema-4 report](benchmarks/2026-10-12-admin-controlled.json) retains
all five recorded pairs, image bindings, input fingerprints and environment
metadata. Its SHA-256 is
`cdde0278f4c5e7793c910d4c24594ce8c55f957b301875e9b1f7e8b6eece1156`.
The [original successful attempt](benchmarks/2026-10-12-admin-controlled-attempt.json)
is `7e869cca0b8541fe83204041b4513ad3`, collected from
`2026-10-12T01:58:49.7926300+00:00` to
`2026-10-12T02:01:15.6683633+00:00`. Both files preserve their original bytes.

Both application images were built from clean source
`45d99bcd1ed1c52b4acaa728e27f9b3704155dcd`. The collector ran from
`97f8889107c6abecbac8391d96e1359da2329600`, after fixes to exact Docker absence
handling and backing-service URL preservation. The source revisions have separate
roles; this is not an application benchmark of the later collector commit.

| Image identity | JIT | Native |
| --- | --- | --- |
| Requested registry index / observed local ID | `sha256:4843dcd4449f12df2d3e39c2e4b30604a69911c1414f7c2810a474f4e9e1bd96` | `sha256:1b9d61bbb7826cd91aa1ac233b128b0cc14830329cb6f620ddd589be47a3da61` |
| Selected Linux/amd64 native manifest | — | `sha256:2ed5999d9af28e4a64e49d463fc563737bfb4295850805d54d7bb3a61aa4ab48` |
| Native publish build ID | — | `37e81a1a72d34203bb70f45781c6d886` |

The task-owned TLS registry served actual pushed images; both exact digests were
pulled before collection. Native packaging verification replayed the original OCI
archive, embedded clean-source publish manifests and 19-file runtime inventory,
and checked the non-root executable entrypoint and packaged 191-path OpenAPI
contract. The registry and fixtures were temporary. This document and report are
an inspection record, not a complete relocatable promotion bundle: the original
packaging archives, attempt journal, inputs, logs and SQL snapshots remain in the
task's retained `artifacts/epic1368` evidence.

## Load and environment

The host was Docker Desktop's Linux x64 WSL2 VM: Intel Core i7-14700T, 28 logical
processors, 50,391,158,784 reported memory bytes, kernel
`6.18.40.1-microsoft-standard-WSL2`, and Docker CLI 29.8.2. A Linux SDK container
ran the PowerShell collector and a separately published managed workload probe.
Owned TCP relays reached the Docker host's published application ports. This
topology is part of the observation and must be considered in any reproduction.

The real release Migrator prepared a fresh PostgreSQL 16.15 database, including
all 52 migrations and Wolverine storage. PostgreSQL retained its default
100-connection limit. Redis was 7.4.11 and Wolverine was 6.48.3. Both profiles used
the same backend connections and configuration: Production, Wait migration mode,
Wolverine/Postgresql transport, automatic provisioning disabled, in-memory media,
media lifecycle disabled, HTTPS redirection disabled, Admin rate limiting disabled,
and tracing disabled. These controlled exclusions are not a production security
or observability qualification.

The authenticated `admin-setting-read` workload performed
`GET /v1/admin/global-settings/by-key/{key}` against one positive-ID owned setting
whose value was `benchmark café 雪 🔎`. The probe validated the complete JSON
response and exact UTF-8 value. Each variant used 1,000 measured requests, 100
warm-up requests, concurrency eight and a 30-second deadline. The input
fingerprint was
`11ad6c53b05b579e50ffe4741dce6e73cf1e2d073f3fa863405421788c620b0e`.

One complete unrecorded warm pair preceded five recorded pairs. The order
alternated JIT/native, then native/JIT. All recorded workloads completed with zero
failures. The raw rows attest 10,000 measured requests and configure 1,000 warm-up
requests. The successful source-bound procedure also validated and discarded
2,200 requests in its initial warm pair, for 13,200 exercised requests in total.
There is no independent counter of all warm-up requests. Two earlier one-read
preflights were separate from this count. Each variant started in a fresh
container with already available image layers; cold readiness is not a cold
registry pull or cold host-cache measurement.

## Results

Values below are independently recomputed medians of the five retained rows.
Memory values are Docker container usage samples, not process RSS or an
instantaneous maximum. Pull bytes are the selected compressed registry layer
payload, not measured network traffic.

| Metric | JIT | Native | Native change |
| --- | ---: | ---: | ---: |
| Image bytes | 567,954,835 | 237,689,444 | −58.15% |
| Compressed layer bytes | 156,532,029 | 69,735,445 | −55.45% |
| Cold readiness, ms | 2,117.5191 | 499.7519 | −76.40% |
| Idle memory bytes | 128,345,702 | 94,204,068 | −26.60% |
| Steady memory bytes | 158,230,118 | 106,640,179 | −32.60% |
| Sampled peak memory bytes | 158,544,691 | 107,479,040 | −32.21% |
| Workload elapsed, ms | 882.2527 | 1,898.3877 | — |
| Completed requests/second | 1,133.4621 | 526.7628 | −53.53% |
| Full-response p50, ms | 3.057834 | 2.665098 | −12.84% |
| Full-response p95, ms | 46.472646 | 47.982728 | +3.25% |
| Full-response p99, ms | 50.225591 | 49.742775 | −0.96% |

| Pair | Order | JIT elapsed, ms | Native elapsed, ms | JIT requests/s | Native requests/s |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | JIT, native | 846.5839 | 1,262.5981 | 1,181.2178 | 792.0177 |
| 2 | Native, JIT | 994.5071 | 1,898.3877 | 1,005.5232 | 526.7628 |
| 3 | JIT, native | 934.5496 | 1,985.0384 | 1,070.0342 | 503.7686 |
| 4 | Native, JIT | 822.4957 | 1,902.4742 | 1,215.8118 | 525.6313 |
| 5 | JIT, native | 882.2527 | 1,594.4419 | 1,133.4621 | 627.1787 |

The native image's embedded publish evidence records 232,695 ms total publish
time: 226,254 ms for the application and 6,441 ms for the health probe. Its main
executable is 104,068,704 bytes; the runtime artifact inventory totals
112,606,665 bytes. Actual Docker build wall time was approximately 53.13 seconds
for JIT and 279.32 seconds for native; these are separate measurements from the
embedded native publish duration.

Request latency and overall batch throughput have different timing boundaries.
Similar p99 latency does not explain or negate the lower batch throughput. The
report does not retain individual latency samples, so offline checking recomputes
row arithmetic, medians and comparisons but cannot reconstruct per-row quantiles.
The profiles differ in selected persistence and worker composition as well as
compilation. Attribution requires a separate instrumented investigation that
retains its own original results; this run is not replaced by a favorable rerun.

## Verification and disposition

The offline verifier passed original attempt/report hash joins, native OCI and
publish binding, JIT layer-byte checks, workload fingerprints, zero-failure row
contracts, throughput arithmetic, memory aggregation, medians and comparisons.
Its receipt SHA-256 is
`061a6899ea79f235180d0e18e0a9a7516a728fdfad190700bc6ab12e5789fe43`.
Before/after SQL snapshots were byte-identical for the owned setting's six scalar
fields, all unrelated settings' six fields, the complete migration history and
all 41 public table counts. Wolverine's own schemas were outside the table-count
oracle because backend nodes and envelopes may change during normal operation.

The original failed readiness attempt `2b02f90fdc784003b6bbd3aa35fd241d` remains
failed with a null report hash. The later successful attempt advances the journal;
it does not rewrite that failure or use the intervening diagnostics as a benchmark.

After the successful measurement and verification, cleanup deleted exactly one
owned setting by matching all six captured scalar values. Its transaction proved
zero remaining owned rows and an unchanged unrelated-settings hash. All three
owned backend containers, two named volumes and six known image references were
then confirmed absent. Shared base images and original evidence were preserved.
The cleanup receipt SHA-256 is
`bb069bf2b668223c7cc522d1078ae27f1b8afa5920241f2148bef3a05438a289`.

The lower throughput is tracked in
[#1645](https://github.com/nickna/Conduit/issues/1645). It remains an investigation
item, with no assigned cause. The current promotion policy requires no throughput
loss, so these observations leave that numerical criterion unmet as well as the
remaining operational requirements.

This is a measured engineering baseline with mixed outcomes. It does not satisfy
representative production benchmarking, SBOM/security/provenance acceptance,
excluded-worker parity, ordered Admin/Gateway soaks, an actual rollback, or approval
of a successor persistence ADR. The [canary runbook](native-aot-canary.md) and
blocked promotion policy continue to govern production deployment.

The [2026-10-16 follow-up investigation](native-aot-admin-throughput-investigation.md)
retains a fresh source-matched baseline and a separate instrumented arm. Neither
reproduced this historical throughput loss; the original result and its unresolved
causal question remain preserved.
