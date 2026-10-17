# Admin throughput investigation, 2026-10-16

The fresh source-matched baseline did not reproduce the lower native throughput
reported in [the original comparison](native-aot-admin-controlled-benchmark.md).
Native completed a median 1,579.2961 requests/s versus 1,349.3172 for JIT, and was
faster in all five recorded pairs. This is a new experiment. It preserves the
original 53.526211% loss and does not establish its cause or a performance fix.

## Evidence and provenance

The [new original schema-4 report](benchmarks/2026-10-16-admin-investigation-baseline.json)
has SHA-256 `446106aa1610661facc7ba73ced5159e71f371b0aa61633163a4ae3892a457e6`.
Its [successful attempt](benchmarks/2026-10-16-admin-investigation-baseline-attempt.json)
is `1aa5a2e7ae764b9cbfd07dafc3a20f05`, collected from
`2026-10-16T20:39:05.0305774+00:00` through
`2026-10-16T20:41:37.9539105+00:00`. Both files retain the collected bytes.

Both fresh application images use clean source
`45d99bcd1ed1c52b4acaa728e27f9b3704155dcd`, matching the original application
revision. The release Migrator built from that revision prepared exactly 52
migrations and 41 public tables. The ordinary managed workload probe was published
from that same revision; its workload implementation is unchanged at the original collector revision
`97f8889107c6abecbac8391d96e1359da2329600`. The new production collector and
evidence helpers use `ba1749686` independently of the application source.

| Fresh image | Exact registry digest |
| --- | --- |
| JIT | `sha256:1dd73b3e9cbdd4f4456d782ea376c283a19d19fdb202ccc82655742b4d91dc7e` |
| Native | `sha256:3adc5f56030e6c60d997ec3c882708b183791061807932991f23d6d76f6f6ec7` |

The task-owned TLS registry served actual pushed images. Both exact references
were pulled before collection. Full native packaging verification replayed the
retained OCI archive, runtime inventory and embedded clean-source publish
manifests; the collector repeated its binding checks before and after collection.
The native build ID is `e7d1103e1a404caa82cd9dbd408fa445`. Its SDK 10.0.401,
ILCompiler 10.0.12 binary hash and clang 18.1.3 driver hash match the original
report. New build timestamps, image identities and executable hashes remain
separate from the original build.

The original image references and ignored 62-input artifact bundle were not
available in this workspace or the inspected legacy checkouts. The committed
original report and attempt remain unchanged; the original report still hashes
to `cdde0278f4c5e7793c910d4c24594ce8c55f957b301875e9b1f7e8b6eece1156`.
This investigation cannot replay or independently revalidate that missing bundle.

## Controlled baseline

The collector ran one discarded JIT/native warm pair and five recorded
alternating pairs, with a fresh application container for every variant. Each
variant used 100 warm-up requests, 1,000 measured requests, concurrency eight,
HTTP/1.1 and a 30-second per-request deadline. Every response passed the original
authenticated six-field JSON/UTF-8 contract. A fresh GUID setting key with the
same value `benchmark café 雪 🔎` gives a new fingerprint,
`066e5560271d3aad523f6fa8473868e3ff18639d2d007bde575dfe48a6d8aa58`,
shared by every fresh row. It is not the original private fixture's fingerprint.

The host is the same Intel Core i7-14700T with 28 logical processors and Docker
29.8.2. The current Linux kernel is `6.18.54.1-microsoft-standard-WSL2`, versus
`6.18.40.1-microsoft-standard-WSL2` originally; reported memory is 50,390,425,600
bytes. PostgreSQL remains 16.15 with its default 100-connection limit, Redis is
7.4.11, and Wolverine is 6.48.3. The database and Redis instance are isolated,
task-owned fixtures. Runtime choices remain Production, Wait migration mode,
Postgresql messaging with automatic provisioning disabled, in-memory media,
disabled media lifecycle, disabled HTTPS redirection and Admin rate limiting,
and disabled tracing. No persistence, worker or pool optimization was applied.

A Linux SDK container ran the collector and original probe without CPU or memory
quotas. Byte-only loopback relays reached the host-published application ports.
The newly retained relay uses `NoDelay=true` on both socket legs and 65,536-byte
requested socket/copy buffers. The historical relay implementation and settings
were not retained. Equivalent topology does not establish identical transport
behavior. Images, kernel, relay implementation and fresh fixture identities
prevent treating this as a one-variable replay of the original measurement.

| Pair | Order | JIT requests/s | Native requests/s | JIT elapsed, ms | Native elapsed, ms |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | JIT, native | 1,320.5731 | 1,556.0347 | 757.2470 | 642.6592 |
| 2 | Native, JIT | 1,394.8060 | 1,628.3128 | 716.9456 | 614.1326 |
| 3 | JIT, native | 1,422.3691 | 1,579.2961 | 703.0524 | 633.1935 |
| 4 | Native, JIT | 1,349.3172 | 1,609.9941 | 741.1156 | 621.1203 |
| 5 | JIT, native | 551.8871 | 1,502.7597 | 1,811.9648 | 665.4424 |

The ratio of median throughputs is +17.044095% for native. Median full-response
p50 is 5.627870 ms JIT / 4.936408 ms native; p95 is 7.391426 / 6.399483 ms;
p99 is 13.802599 / 7.794578 ms. The fifth JIT row remains in the original report
and all arithmetic. Its lower throughput has no assigned cause. Default-probe
reports retain quantiles rather than individual latency samples.

Independent offline verification passed 356 checks covering all ten measured
rows, workload identity, alternating order, arithmetic, medians, comparisons,
57 source hashes, successful attempt/report joins and full retained native
packaging identity. Independent before/after SQL snapshots match every owned
setting scalar, all unrelated settings, migration history and all 41 public table
counts. Wolverine-owned schemas are outside that oracle. The stable SQL data
hash is `153704e42f2075a74ea51c3ee21e5400316c2179afe8984f317e03df604bcb9a`.

The ten recorded rows attest 10,000 measured requests and configure 1,000 warm-up
requests. The discarded validated warm pair adds 2,200 requests by procedure;
13,200 total is not an independent global request counter. A failed configuration
attempt, `f833d3ac0cdd405381e673c436ecce67`, issued no workload requests and retains
a null report hash. It exposed the inherited StrictMode singleton-array bug filed
as [#1701](https://github.com/nickna/Conduit/issues/1701). Only the experimental
wrapper changed to use the collector's standalone session semantics.

Full build outputs, OCI/rootfs archives, source/input hashes, private configuration,
SQL snapshots and experimental scripts remain in task-local ignored
`artifacts/issues-1469-1696/issue-1645`. Committed reports are inspection records;
they are not a downloadable, relocatable promotion bundle.

## Separate instrumented arm

Arm `210c4ff1c20f4335ace7ad6dfe621e82` reused the same digest images, backend,
private authentication, setting, relay, workload controls and twelve-variant
schedule. It enabled the existing ASP.NET OpenTelemetry path at 100% parent-based
sampling, collected OTLP/HTTP protobuf, and used the new opt-in client diagnostic
probe. Its frozen client source is based on `ba1749686` plus the explicitly hashed
diagnostic changes; it is separate from the original default probe and application
provenance. The published diagnostic DLL hashes to
`ec0a8c3c3f55f076df14b566b21422c0620691aaadd3c828b461ede3e9e6fd0f`.
The [committed instrumented summary](benchmarks/2026-10-16-admin-investigation-instrumented-summary.json)
retains all twelve public variant rows, client statistics, sampled counter/SQL
observations and original receipt hashes. Its SHA-256 is
`24f0db183566cf654b9dc3543c600d2e5df81079102a0f5c6894d6706ff0f125`.

The completed arm retains all 13,200 client rows and unique matching SERVER
spans, including the discarded warm pair and every request warm-up. All matched
spans have HTTP 200 and status UNSET; none has ERROR status. All client request
indices, workers, milestone ordering, raw quantiles, throughput arithmetic and
trace/parent joins were independently verified, with 111 raw files hashed and
rechecked. The receiver accepted 36 exports, retained 13,200 matching spans and
filtered 36 nonmatching spans. It recorded no invalid spans, capacity drops,
duplicate drops, malformed exports or timeouts.

The table contains medians of five **per-run statistics**, excluding the
discarded pair. Percentiles are nearest-rank per-run percentiles, not pooled
percentiles. Independently computed component medians need not sum to a total.

| Instrumented quantity | JIT | Native |
| --- | ---: | ---: |
| Completed requests/s | 1,267.6932 | 1,467.3637 |
| Mean client total, ms | 6.306537 | 5.434671 |
| Mean client start to headers, ms | 6.222768 | 5.242597 |
| Mean headers to completed body, ms | 0.057477 | 0.135254 |
| Mean body validation, ms | 0.028911 | 0.028000 |
| Mean whole SERVER span, ms | 4.689299 | 3.891822 |
| Client total p95, ms | 7.225002 | 6.919929 |
| Whole SERVER p95, ms | 5.362800 | 5.133500 |

Native throughput was higher in every instrumented recorded pair; the ratio of
medians is +15.750697%. These measurements are retained separately from the
tracing-disabled baseline. Their difference does not isolate tracing overhead.
Client start-to-headers includes client work, transport and server processing.
The SERVER span covers the ASP.NET request pipeline; the existing custom Admin
duration histogram starts after security middleware and is a different boundary.
The server spans do not subdivide request time into SQL, runtime or transport
causes. Client monotonic offsets and server Unix timestamps have different origins;
analysis converts durations using the recorded frequencies rather than
subtracting timestamps across those origins.

During measured phases the probe-process CPU medians were 322.680 ms JIT and
359.552 ms native, with allocated-byte medians 23,121,864 and 23,170,896. Every
measured phase recorded one Gen0 collection and zero Gen1/Gen2 collections.
These are client-process observations, not application CPU or GC measurements.

Runtime and PostgreSQL observers bracketed each complete 1,100-request invocation,
including its 100 warm-ups and setup/join edges. They do not define a measured-only
1,000-request counter window. PostgreSQL recorded 20–24 activity samples per
recorded variant at a requested 100 ms interval; sampled backend maxima were
11–12. The only non-null sampled waits were `Client/ClientRead`. No Lock/IO wait
was sampled, which does not prove such waits never occurred and cannot measure
client pool-rental waits. The observed commit deltas ranged from 255–1,303 for JIT
and 74–95 for native despite 1,100 successful requests in every invocation:
statistics lag and background work prevent using these deltas as query counts.
No statement text, parameters, arbitrary metric labels or connection tags were
retained in public observations.

Server metric capture retained 334 samples: 333 valid and one invalid sample in
JIT pair two; the invalid sample remains recorded and is not imputed. Across the
five recorded variants per runtime, median observed server counter differences
were 3.915363 / 1.748853 CPU seconds, 117,672,576 / 36,384,120 allocated bytes and
16.125 / 7.784 ms GC pause for JIT / native. These bracket the full invocation and
include application background and scrape work; they are not per-request or
measured-phase CPU/GC attribution.

Two opaque, variant-local Npgsql pools exposed configured maxima of 32 and 75;
these differ from PostgreSQL's server-wide 100-connection limit. Pending and
timeout fields were missing for both pools throughout, and remain null rather
than zero. One pool had no observed operation-histogram delta in every variant.
Available-pool median observation deltas were 1,147 / 1,135, with median per-run
mean operation durations 1.765264 / 1.350035 ms. These partial observations are
neither SQL statement counts nor pool-wait durations. In JIT pair four, every
sampled used-connection gauge was zero despite more than 1,100 operation
observations, illustrating how 100 ms sampling can miss short occupancy.

All application containers, probe/guard processes, receivers and observers were
joined or confirmed absent before completion. Derived private arm inputs were
removed; original prepared fixture inputs remain private. The independent final
SQL gate again matched all ten identity fields, including all six owned values,
unrelated settings, migration history and 41 public table counts, with zero target
database backends afterward. Earlier instrumented fixture failures occurred
before workload probes: Windows CRLF readiness and live-file sharing required
fixes. Their failed arms, cleanup evidence and unchanged SQL gates remain separate.
The final Windows bootstrap was proved with the real sampler before the successful
arm. Its diagnostic-only outer guard is 540 seconds versus the baseline's
900 seconds; the per-request 30-second deadline is unchanged.

## Disposition

The original lower-throughput observation remains valid as its recorded result.
Both fresh arms show the opposite direction in this changed environment, so this
investigation supplies no demonstrated historical bottleneck or justified
application/persistence optimization. In particular, source differences between
the selected JIT and native profiles are not a causal explanation of the old loss.
Recovering the original images, relay and complete environment would be necessary
for an exact replay; new transport controls would test the new relay's sensitivity
and would still not reconstruct that history.

The [opt-in diagnostic CLI](native-aot-canary.md#reproducible-comparison) now retains
per-request client timings and correlation IDs for subsequent investigations.
Its focused suite passed 57 cases, its actual CLI proof passed six cases, and its
NativeAOT analyzer build reported no warnings or errors. The successful Linux
instrumented arm provides additional real application/trace coverage of the same
frozen probe source. No production canary, accepted qualification, ordered soak,
rollback or promotion is recorded. The existing blocked policy and
[canary requirements](native-aot-canary.md) continue to apply; this investigation
does not close the historical causal question in [#1645](https://github.com/nickna/Conduit/issues/1645).
