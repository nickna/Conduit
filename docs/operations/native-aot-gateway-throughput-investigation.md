# Gateway complete-SSE investigation, 2026-10-16

The historical complete-SSE regression remains unexplained. A fresh source-matched
Gateway baseline stopped during the discarded JIT warm pair with eight HTTP 502
operations. It produced no paired performance report and never reached its SSE
workload. This failed experiment cannot establish whether the historical SSE
result reproduces, identify a runtime bottleneck, or justify an optimization.

The [original comparison](native-aot-gateway-controlled-benchmark.md),
[original report](benchmarks/2026-10-12-gateway-controlled.json), and
[original attempt](benchmarks/2026-10-12-gateway-controlled-attempt.json) remain
unchanged. Their source roles, five alternating pairs and adverse SSE observation
remain authoritative: native median SSE throughput was 4.18% lower and the median
of five per-run p99 values was 7.70% higher. A failed follow-up does not replace
that result. [Issue #1647](https://github.com/nickna/Conduit/issues/1647) remains open.

## Fresh experiment and retained provenance

Both fresh application images were built from clean
`b5ced25a67bc0c5356af1c8d5035443d800a7ede`. The deterministic managed provider and
default workload probe were freshly published from
`45d99bcd1ed1c52b4acaa728e27f9b3704155dcd`, preserving the original separation
between application and fixture revisions. The original images and ignored
unit113 evidence bundle were unavailable; their documented cleanup removed the
original application images. This is a new experiment, not an original-byte replay.

| Fresh identity | Value |
| --- | --- |
| JIT requested digest / observed local ID | `sha256:7fe4a87f71b28aed2f02a17b58d3813d0e6cc85a29ebc3c0850a54e774017543` |
| Native requested digest / selected manifest / observed local ID | `sha256:640685bfe9c885a4c24031a5ec4678c43f9a09c450658d0d7fdac140a59d72ca` |
| Native config digest | `sha256:d20590664bd98942c521e8c264549b4b4f034c0cd198fe3ab6ddafbf52c020ab` |
| Native publish build ID | `37e31bb930d4477abef48a91136b19c7` |
| Fresh default provider/probe DLL SHA-256 | `71def16ba43c42176e2248da1ea6a2f579538ef30aa816e30a7cecf06da3ac96` |
| Collector SHA-256 | `d244b10106c7098a8b3fdc3022f86375026316cbf3de20443bd16f286f3226f4` |

Both image digests were pushed and pulled through the owned TLS registry. The
complete native OCI archive, clean-source publish manifests, executable, health
probe and runtime inventory passed the packaging helper and independent typed
benchmark reader. Those bindings describe inspection of this fresh image; they
do not constitute registry promotion attestation.

The selected SDK was 10.0.401, ILCompiler 10.0.12 and clang 18.1.3. Compiler and
linker executable hashes match the historical tool evidence. Fresh binaries,
build timestamps, registry identities and the host kernel differ. Original
third-party trim/AOT warnings remain in the retained publish logs; the fresh
Gateway publish is not described as globally warning-free.

The host was Docker Desktop/WSL2 Linux x64, 28 logical processors, Docker 29.8.2,
kernel `6.18.54.1-microsoft-standard-WSL2`. The fixture used a fresh GUID database
on PostgreSQL 16.15 with `max_connections=100`, a separate Redis 7.4.11 instance,
52 exact-source release migrations and eleven positive generated linked IDs.
The graph was inserted once. An evidence-publication error after its transaction
committed was recovered through read-only identity lookups; no accounting rows
were reset or reseeded.

Both profiles selected Production, Wait migration mode, Wolverine/Postgresql,
automatic provisioning disabled, in-memory media, disabled media lifecycle/HTTPS
redirection/tracing, normal 30-second spending flushes, and the same private
database, Redis and distinct backend/virtual credentials. IP filtering was
disabled for this bridge topology and its database override was independently
absent. Parent limits were 100,000 requests per 60 seconds; the key allowed
100,000 RPM and 200,000 RPD. Group and token/parallel limits were null. The
collector's two optional ambient numeric rate-limit overrides were absent.

New opaque byte relays used TCP NoDelay and 65,536-byte copy/requested socket
buffers. Their implementation was retained and tested for exact bytes, half-close
and active cancellation. The historical relay implementation was unavailable;
its socket settings are not reconstructed. The provider's intentional 25 ms SSE
delay was unchanged. These controls do not isolate a single historical variable.

## Preflights and failed collection

One JIT JSON request and one native SSE request passed the original complete
response validator. Persisted log counts were read while the respective hosts
were live. Both hosts subsequently exited zero, were removed without force, and
were confirmed absent. Before the native request, a broad invariant check had
misclassified the JIT host's expected owned UsageTracked audit and singleton
reconciliation checkpoint as unrelated writes. The JIT host was stopped before
that failed check was inspected, and the native host had already started with no
business request sent. Further requests paused until the correction was reviewed.
The original failed gate remains retained.
Read-only correction joined the complete audit/checkpoint row hashes back to the
retained live snapshot, validated their source-derived semantics, and confirmed
all other unrelated rows unchanged. The corrected broader gate was established
after the JIT host stopped, not retroactively described as an original live pass.

The two-request preflight then passed three independently balanced live
observations at least 30 seconds apart on an unmeasured native drain, followed by
exit-zero graceful removal, independent absence checks and a balanced post-stop
readback. It matched two logs/audits/provider operations, 20 input/10 output
tokens, cost/spent/debits `0.00015000`, balance `99.99985000`, and absent financial
Redis obligations. A first read-only polling series was superseded after an
oracle source update; three fresh observations and post-stop verification used
the same final frozen oracle. All original receipts and both oracle versions are
retained.

The planned baseline retained the full original protocol: one discarded warm
pair, five recorded pairs in alternating order, JSON then SSE inside each fresh
host, 1,000 measured requests and 100 warmups per workload, concurrency eight,
30-second request deadlines, complete content/usage/DONE/EOF validation and
unchanged zero-failure requirements. Builds and tests stopped before collection.

Collection attempt `db78cc2b445a4493b1fec5017941cdef` ran from
`2026-10-16T21:28:07.3985013+00:00` to
`2026-10-16T21:29:10.7590298+00:00`. The
[original failed attempt](benchmarks/2026-10-16-gateway-investigation-failed-attempt.json)
has `reportSha256: null`. Its
[closed failure diagnostic](benchmarks/2026-10-16-gateway-investigation-failed-diagnostic.json)
records workload/probe-exit in the JIT discarded-warm-pair variant, with both
owned-container and private-file cleanup confirmed. The phase name identifies
the collector's unrecorded pair; it does not mean the probe failed its own
100-request warmup.

Independent server-side observations after collection matched:

| Cumulative observation, including two preflights | Value |
| --- | ---: |
| Successful RequestLogs / matching UsageTracked audits | 1,094 / 1,094 |
| Provider accepted JSON / SSE operations | 1,093 / 1 |
| Input / output tokens | 10,940 / 5,470 |
| Cost / group spent / debit total | 0.08205000 |
| Group balance / lifetime credits | 99.91795000 / 100.00000000 |
| Additional ErrorResponseSkipped audits, HTTP 502 | 8 |
| Unresolved started reservations / known units | 8 / 0 |
| Held amount per reservation / total | 0.0410075 / 0.32806 |

The eight error audits have distinct request IDs, null usage and null calculated
cost. All twelve other provider counters, including authentication failures,
were zero. Limits and the absence of post-shutdown database backends do not
establish why these operations failed. The unresolved holds were preserved;
successful-request debits are not equated with full settlement of the attempt.

These counts are consistent with 100 JSON warmups, 992 successful measured JSON
operations and eight measured failures, beyond the separate preflights. The
collector used in that attempt threw on the probe's nonzero exit before retaining its structured
stdout report. Individual client results and latencies therefore cannot be
reconstructed from this failed attempt. Provider counters count accepted fixture
operations; persisted successful logs do not independently prove client EOF.
No fresh throughput, latency percentile or complete-SSE comparison is reported.
The [public numeric summary](benchmarks/2026-10-16-gateway-investigation-summary.json)
retains the source/image identities, server-side accounting, unresolved holds,
tooling checks and original receipt hashes without private input contents.

## Transport evidence and disposition

The original provider was sealed after an independent final counter read. Its
supervisor and relay exited zero; the unmodified provider has no cooperative
stop entrypoint and was deliberately killed as an owned child after relay drain,
with exit 137. Both children and all capture streams joined. The stopped
container was retained. Its relay counted 1,115 accepted/connected/completed TCP
connections, zero connect/copy failures, zero final active connections and peak
active eight. TCP counts include health/state traffic and do not prove HTTP
acceptance or explain the eight operations. Fully flushed provider/relay stderr
was empty; that does not exclude a silent close or rejection.

The complete private PostgreSQL custom archive passed header and `pg_restore
--list` verification with 472 TOC entries. Its SHA-256 is
`61ab35e47bd170c6b308d07a80313e34c0c927958db619a4d835093c5d5708fb`.
Private captures and archive contents remain private. No balances, provider
counters, SQL rows, Redis keys or indeterminate holds were reset to retry the run.
The complete private Redis RDB also passed integrity verification and exact
remote/local SHA-256 equality:
`5012ad3ade913e7a53449fe191d4a4413847822918e38abf353e5af5f188a6f4`
(92,704 bytes). The reserved amount and exact hashes of the eight started and
eight indeterminate records remained unchanged before and after capture. A fresh
unpooled management query explicitly counted zero target-database backends after
both archives. That post-shutdown observation cannot establish pool pressure
during the failed workload; the earlier grouped query's JSON null remains
preserved alongside the independent empty-array/count explanation.

One supplemental transport pair used fresh provider identities and counters,
without Gateway, SQL, Redis or traffic to the failed fixture. Each arm completed
100 warmup and 1,000 measured JSON requests at concurrency eight, with zero
failures and the original complete JSON validator. The direct client ran beside
the provider on loopback. The relayed client ran in a separate bridge container
through a newly published host-loopback port and the unchanged opaque relay.
Both used HTTP/1.1 UTF-8 `StringContent` with computed Content-Length and a
synthetic prompt. The relayed arm's 1,103 completed TCP connections include
1,100 business requests plus state/health traffic; connect/copy failures and
final active connections were zero. All new containers were removed after joined
capture flush. Independent readback passed 4,470 checks over all 2,200 request
rows, counters, lifecycle receipts and component hashes. The raw receipt SHA-256
is `7e5c936d8b538b1af0603abbcb4c175bbe7aa38a1ef2e3bfd26406e67b8cb120`;
the compact independent summary is
`80d878d4c73237ef1192c9a6c8959abc06b83a57b009b9ee7255797cf39fa0be`.
This single transport-only control did not reproduce the Gateway errors and
cannot establish their cause or a Gateway performance comparison.

The [opt-in client diagnostic sidecar](native-aot-canary.md#reproducible-comparison)
now supports schema-2 complete-SSE observations: first/last completed body reads,
first nonempty data event, exact DONE-frame observation, read/event counts, and
the existing header/body/validated/end boundaries. That extension left default
schema-2 stdout and the production collector unchanged. Admin/non-streaming sidecars retain
schema 1. The full validator still runs after EOF; seeing DONE cannot bypass a
deadline, malformed usage, duplicate/trailing events or incomplete consumption.

The extension passed 67 focused real-HTTP tests and a zero-warning probe analyzer
build. A frozen Linux diagnostic probe/guard, bounded Gateway SERVER/provider
HTTPCLIENT OTLP receiver, numeric metrics/PostgreSQL observers, and independent
offline join verifier were also prepared. Receiver and bootstrap proofs passed;
they are tooling checks, not completed application profiling. The diagnostic arm
was not launched because its successful-baseline settlement gate was unmet.
HTTPCLIENT Activity can end at headers and cannot by itself separate the full
provider stream from relay time. The planned SSE-only diagnostic arm would also
omit the baseline's preceding JSON phase; any difference would not isolate
tracing overhead.

## Current investigation tooling

The collector now writes schema-2 failure diagnostics with a nullable
`workloadAggregate`. On a nonzero probe exit, it validates the bounded, complete
schema-2 stdout report against the selected workload and configuration before
retaining only its workload ID, explicit `measured` phase, requested/executed/
completed/failure counts, and configured warmup size, concurrency and request
deadline. `warmupRequests` describes configuration; it does not independently
observe warmup completion. Malformed, mismatched or extra output produces a null
aggregate. Original schema-1 failure diagnostics remain readable. The default
probe report and strict successful-result validator are unchanged: nonzero exit
still fails collection, creates no performance report and cannot update the
successful alias. Retained diagnostics exclude raw child stdout/stderr, private
configuration, URLs, credentials, request/response content and exception text.

Typed `HttpClientHelper.SendJsonRequestAsync` also emits warning event 1702,
`ProviderJsonFailureDiagnostic`, with only closed `Stage` and `Category` codes,
numeric or null `ObservedStatus`, a closed `HttpRequestError` code and a W3C hex
`TraceId` when available (`none` otherwise). Stages distinguish request creation,
buffered send and response processing. An actual returned HTTP response records
its status; an exception from the default buffered send leaves observed status
unknown, because that await includes body buffering and does not establish
whether headers arrived before failure. Exception status metadata is not treated
as an observed response. GET and SSE helpers are unchanged. A request can emit
multiple diagnostic records, for example an upstream error response followed by
a translator failure; correlate by trace when available rather than counting
records as requests. These tooling changes do not establish a current-head
reproduction or the cause of the historical failures.

The tooling passed 86 focused helper, endpoint and reservation tests. A focused
collector run passed 186 contracts, including actual failed child processes,
fractional timing values, strict aggregate projection, prior successful-report
preservation and owned cleanup. An earlier complete collector run exposed an
integer-only parsing mistake in the new failure projection; the corrected
projection has the focused proof above. That earlier failure is retained and is
not a complete-suite pass.

A fresh local fixture used clean source revision
`efd79af0879f2ff6755a8fb2b21da70c5b7f937f`, frozen JSON/SSE validators and a Windows
.NET 10.0.13 client. This source predates the diagnostic event above. One JIT JSON
preflight and one native SSE preflight passed their complete validators. Three
live observations, more than 30 seconds apart, and a post-shutdown observation
agreed on two usage logs/audits, 20 input and 10 output tokens, cost/spend/debits
`0.00015000`, balance `99.99985000`, zero error audits and zero unresolved financial
obligations. No accounting or provider state was reset. Other host workloads
remained present, so this setup does not establish a performance comparison.

The standard collector attempt `b27c1b4ef1c544c985a27ad093d9dad4` then stopped at
JIT image metadata validation, before creating a measured host or sending a
workload. The local JIT image lacked a registry manifest accessible through the
collector's normal `docker manifest inspect` command. Its schema-2 failed-attempt
receipt has a null workload aggregate and no performance report. Independent
post-failure readback still showed exactly the two settled preflights and zero
database backends. This metadata failure is separate from the historical
provider-communication failure and supplies no evidence about its cause.

The failed collection is retained separately in
[issue #1702](https://github.com/nickna/Conduit/issues/1702). A
[2026-10-17 networking follow-up](native-benchmark-container-networking.md)
reproduces the current communication failure and isolates unanswered TCP SYNs
when recently closed frontend four-tuples are reused through Docker Desktop's
host-gateway/published-port path. Direct fixture-bridge controls pass, including
138 recent tuple reuses. The collector now supports a verified existing bridge
so containerized backing services can communicate directly. The unavailable
original packet evidence still prevents retrospective attribution of the
historical failure, and the historical SSE performance question remains open.
No application or persistence optimization was selected. This investigation
establishes no accepted qualification window, canary,
ordered Admin/Gateway soak, rollback, successor ADR approval or production
promotion. Production remains on JIT under the existing blocked policy.

## Standard SSE metrics work, 2026-10-18

Current-source inspection found avoidable work in the standard
`/v1/chat/completions` stream. `EmitPeriodicMetrics` checked the collector's current
metrics and recorded a timestamp for every content-bearing chunk even though the
standard response writer suppresses enhanced metrics events. `GetMetrics` builds
a metrics object and, once multiple timestamps exist, a temporary list of all
inter-chunk intervals. Repeating that snapshot on each content chunk makes the
total interval work grow quadratically with a long stream's chunk count. These
timestamps describe chunks, rather than independently counted model tokens.

The endpoint now returns from periodic metrics collection immediately when
enhanced events are disabled. Enhanced `/v1/conduit/chat/completions` streams keep
their periodic and final metrics. Content, caller-selected usage, terminal
`[DONE]`, provider usage capture, transport evidence and accounting finalization
continue through their existing paths. The focused endpoint tests exercise both
dialects, usage opt-in and opt-out, client cancellation with observed usage, and
1,024 content chunks with and without enhanced events.

This code finding does not identify the cause of the historical runtime
comparison. The original provider emits exactly two content-bearing chunks,
separated by its intentional 25 ms delay, followed by one empty-choices usage
chunk and `[DONE]`. That short fixture does not exercise growing per-chunk
interval snapshots: its two content chunks encounter zero and one previously
recorded timestamps. Both JIT and native profiles used this workload. Removing
discarded metrics work does not establish a NativeAOT-specific bottleneck or
demonstrate recovery of the original 4.18% throughput loss and 7.70% p99 increase.

A new performance comparison still requires a fresh, source-matched fixture,
the discarded pair and five alternating recorded pairs, complete JSON/SSE
validation, unchanged load and deadlines, and independent exact
provider/financial/Redis settlement and owned cleanup. Run it without concurrent
builds or contract tests and retain separate instrumented observations of
provider stream completion, application work, transport and backing services.
The historical report and failed follow-ups remain unchanged, and
[issue #1647](https://github.com/nickna/Conduit/issues/1647) remains open until that
evidence resolves the streaming observation.

## Completion-path candidate, 2026-10-18

A fresh uninstrumented three-variant comparison completed all 39,602 Gateway
operations with complete JSON/SSE validation, exact financial/provider/Redis
settlement, verified PostgreSQL/Redis archives and owned cleanup. The
[retained report](benchmarks/2026-10-18-gateway-persistence-candidate.json)
preserves all eighteen host samples, including the predeclared discarded group,
all five recorded groups and their execution orders, raw probe hashes, source and
image identities, metric ranges, and final accounting receipts.

The candidate combined positive scalar cost-ID caching with Native request-log
batching. It did **not** resolve the streaming regression:

| Median of five recorded runs | Native control | Native candidate | JIT reference |
| --- | ---: | ---: | ---: |
| Complete-SSE requests/second | 151.672753 | 149.434138 | 164.242011 |
| Complete-SSE p50 ms | 52.579374 | 53.318783 | 47.634276 |
| Complete-SSE p95 ms | 60.017268 | 61.943688 | 56.347842 |
| Complete-SSE per-run p99 ms | 64.851191 | 67.886788 | 74.108335 |
| Non-streaming requests/second | 243.051279 | 249.198000 | 329.495719 |
| Non-streaming per-run p99 ms | 44.733785 | 44.953706 | 46.989427 |

Native control source was `234670caf30fd87452282d1a817cec452f594844`;
both candidate profiles used `ddbd0180f387c609db0291c613c8ed74dca362b0`.
The source-45 provider/probe, JSON-then-SSE ordering, 100 warmups, 1,000 measured
requests, concurrency eight, 30-second deadlines and complete validators were
unchanged. All six three-variant permutations were fixed before traffic, with
group zero discarded and groups one through five retained. Builds, tests,
tracing, profilers and PostgreSQL statistics were disabled during timing.
Native before/after images shared the exact OS base; JIT used the standard
no-RID publish and matched runtime configuration. The report records these
roles and limits explicitly. Readiness includes Docker launch and polling;
memory displays were sampled outside request timing and are not a steady-state
or peak-memory measurement. The p99 column is a median of per-run percentiles,
not a pooled percentile or confidence interval. Group variability is retained.

The objective, declared before recorded results were read, required candidate
Native median SSE throughput at least the JIT reference and median per-run p99
no worse, alongside before/after evidence and the complete JSON tradeoff.
Throughput failed that objective. The lower candidate p99 relative to JIT does
not demonstrate recovery, and no favorable subset replaces this result.

The queued Native log experiment is therefore removed from the proposed fix.
It introduced a process-crash durability tradeoff without demonstrating an SSE
benefit. Native retains its original typed per-request durable writer. The
candidate commit, retry/failure test receipts and measurements remain retained.
Separate matched diagnostic traces and zero-drop packet captures locate extra
Native work before provider transmission and during completion; they do not
assign the remaining wall time outside SQL to CPU. Further work targets
provider/credential graph round trips and resolved CPU stacks. Issue #1647
remains open; this candidate establishes no production promotion.

## Provider-lookup candidate, 2026-10-18

A second fresh comparison tested source
`0906b29de737c5a507647908b4b0a5a30e9fcba9`: positive scalar cost-ID caching,
joined provider/credential reads and the original durable per-request Native
log writer. It used the same control, workload, six execution orders, discarded
group and five recorded groups as the preceding comparison. All 39,602 responses
passed complete validation and exact financial/provider/Redis settlement;
archives and owned cleanup passed. The
[full report](benchmarks/2026-10-18-gateway-provider-lookup-candidate.json)
retains all samples, ranges, raw hashes and independent closure evidence.

| Median of five recorded runs | Native control | Native candidate | JIT reference |
| --- | ---: | ---: | ---: |
| Complete-SSE requests/second | 155.595829 | 160.698457 | 178.714662 |
| Complete-SSE p50 ms | 51.433095 | 49.591357 | 44.021796 |
| Complete-SSE p95 ms | 57.712710 | 58.389472 | 51.322707 |
| Complete-SSE per-run p99 ms | 60.802173 | 64.877831 | 58.474274 |
| Non-streaming requests/second | 253.509214 | 260.563858 | 338.919300 |
| Non-streaming per-run p99 ms | 42.146247 | 44.420383 | 35.248519 |

This candidate also **fails the unchanged resolution objective**. Native median
SSE throughput improves 3.28% over its control, but remains 10.08% below JIT;
median per-run p99 is 6.70% worse than its control and 10.95% worse than JIT.
The candidate loses both SSE metrics to JIT in every recorded group. Its modest
throughput improvement does not establish regression recovery. JSON throughput
improves 2.78% over Native control while its p99 worsens 5.40%; both remain worse
than JIT. These are ratios of run-summary medians, without a significance or
pooled-percentile claim.

The full-run manifest initially omitted the required seed-tools image field.
That omission was caught before timed traffic; the original manifest was
retained and a separately reviewed metadata-only correction bound the existing
seed image. Frozen source, workload, objective and runner bytes were unchanged.
All timings ran after builds, tests and offline profiling had closed.

Issue #1647 remains unresolved. Further work examines redundant request-time
key/group reads and serial round trips within existing Native billing-audit
flushes. Those flushes are background work; the earlier diagnostic trace does
not place their hundred-row loop on each request's awaited HTTP path. No
production promotion follows from this comparison.

## Authentication and audit-pipeline candidate, 2026-10-18

A third fresh comparison tested source
`7cc1b4e20a8aed1f20d1695e96722ee44252acb2`. It retained the preceding
candidate's durable Native request-log writer, positive cost-ID cache and joined
provider reads, removed a redundant key lookup after group validation, and
pipelined Native billing-audit inserts in chunks of at most 100 commands within
the existing single transaction. Native control remained source `234670`.
The workload, six execution orders, discarded group, five recorded groups and
resolution objective were unchanged. The
[full report](benchmarks/2026-10-18-gateway-auth-audit-candidate.json) retains all
eighteen samples, all 36 raw report hashes, per-group directions and ranges,
image/source identities and independent accounting and cleanup proofs.

| Median of five recorded runs | Native control | Native candidate | JIT reference |
| --- | ---: | ---: | ---: |
| Complete-SSE requests/second | 150.183666 | 152.491850 | 167.506589 |
| Complete-SSE p50 ms | 52.446501 | 51.731575 | 47.005056 |
| Complete-SSE p95 ms | 61.832110 | 61.882167 | 54.175918 |
| Complete-SSE per-run p99 ms | 66.236360 | 69.834046 | 65.097521 |
| Non-streaming requests/second | 225.803914 | 230.386479 | 304.394831 |
| Non-streaming p50 ms | 34.273522 | 33.961788 | 24.646619 |
| Non-streaming p95 ms | 44.126129 | 44.936609 | 33.089244 |
| Non-streaming per-run p99 ms | 50.239449 | 52.864863 | 45.076359 |

This candidate **fails both unchanged resolution criteria**. The ratio of
median SSE throughput improves 1.54% over Native control, while median per-run
p99 worsens 5.43%; both remain worse than JIT. The median paired-group changes
are approximately +0.009% throughput and +0.97% p99. All five groups remain in
the result, including the JIT group's 169.701674 ms p99 outlier. Neither a
favorable subset nor a pooled percentile replaces the declared comparison.
The complete JSON tradeoff is retained above and in the report. Five groups do
not establish statistical significance.

All 39,602 operations passed full response validation and exact settlement:
39,602 request logs and usage audits, matching provider counts and three Redis
marker sets, USD 2.97015000 spent/debited, and USD 97.02985000 remaining from the
initial USD 100. Every one of the 38 archived ledger entries has a matching
running balance. All 41 unrelated public tables remained unchanged, financial
liabilities and audit errors were zero, and post-stop database backends were
zero. Three spaced final live checks, shutdown checks, verified PostgreSQL and
Redis archives, and removal of all owned resources passed. Execution success
does not resolve issue #1647 or authorize production promotion.

The next declared experiment isolates the Gateway Native publish optimization
preference. Its Native control will be this third candidate's retained `7cc1b4e`
image, rather than the earlier `234670` image; the candidate will default an
otherwise unset `OptimizationPreference` to `Speed` only during Gateway Native
publication. It will retain the same workload, execution orders and resolution
criteria, with a JIT reference from the final source. The compiler's evaluated
properties, response arguments and profile inputs must be captured before any
efficacy claim. This remains an unverified candidate; the preceding failed
experiments remain preserved.
