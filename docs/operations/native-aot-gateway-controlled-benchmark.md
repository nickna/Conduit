# Controlled Gateway NativeAOT comparison, 2026-10-12

The selected Gateway native profile started faster and used less container memory
than the default JIT profile in this controlled comparison. Median JSON throughput
was 9.08% higher, while complete-SSE throughput was 4.18% lower and SSE p99 latency
was 7.70% higher. JSON throughput was higher in every recorded pair; SSE throughput
was lower in four of five. Production promotion remains deferred. These results
describe this fixture and environment; they do not establish production capacity
or isolate the cause of the differences.

## Original measurement and source roles

Byte-preserving original measurement records:
[original schema-4 report](benchmarks/2026-10-12-gateway-controlled.json) and
[original successful attempt](benchmarks/2026-10-12-gateway-controlled-attempt.json).
The retained original report is
`artifacts/epic1368/unit113-gateway-performance/gateway-benchmark.json`, SHA-256
`8a49e89c519cadbd6d0b3823464d5afb2238ce30e0ea1f27a287be6fc821f2da`.
The successful attempt is `1a8f81fd7f1840b6a359160b6dcb169a`, collected from
`2026-10-12T05:04:24.8816467+00:00` to
`2026-10-12T05:11:25.1066854+00:00`. Its original attempt-record SHA-256 is
`235eb931c15d4225a7a6a097eb4b5d28a894091c444643bdd030371cd7a521d5`.
The phase SDK exited successfully and its original launcher receipt records
joined cleanup and absence. The complete offline verifier passed; its receipt
SHA-256 is `7669eae1bf1c75db89b7286f650ff52f35a2b45cdeee8cef49cd94e1421d96f2`.

Both application images and the collector ran from
`b5ced25a67bc0c5356af1c8d5035443d800a7ede`. The image build input was the clean,
checksum-pinned Git context
`https://github.com/nickna/Conduit.git?ref=b5ced25a67bc0c5356af1c8d5035443d800a7ede&checksum=b5ced25a67bc0c5356af1c8d5035443d800a7ede`.
The collector source SHA-256 was
`bbf3eb7a0b143d942dc41474a3278338062e215782e4e1418447e6bf98dc284e`.
The retained managed provider and workload probe were separately published from
`45d99bcd1ed1c52b4acaa728e27f9b3704155dcd`; the exact DLL SHA-256 was
`5ec85fa270b295b213b87aa82d3b20b8883a05c616758dac31f072f2e3556242`.
That older fixture revision is not the application image revision being compared,
and no native-provider claim is made. The launcher runs the actual deterministic
provider program; owned full-duplex relays forward its bytes.

| Image identity | JIT | Native |
| --- | --- | --- |
| Requested registry index / observed local ID | `sha256:2aa7893268a0c337a1468c635b2fe806e49ba1f9543ba61e302eae38e5d14c5c` | `sha256:1b9d0d823c2a93b5bb322a79bf164f73ff6dfc4bd5b87028b9d237fec17da809` |
| Selected Linux/amd64 native manifest | — | `sha256:58fec7679ac650cc9ffff02b92966c6bf7ed941b25f960d72a9ae8a2490a2165` |
| Native config digest | — | `sha256:bfd61e18f49b7966a706e4872434923d380ca98ca54d37151204690d00d19c6f` |
| Native publish build ID | — | `39df67d0ec184bb0b39888ed88f8ad5d` |

The task-owned TLS registry served actual pushed images and both exact digests
were pulled before collection. Native packaging verification replayed the original
OCI archive and embedded clean-source application/health manifests and six-file
runtime inventory. It exercised the non-root packaged entrypoint and its 35-path
OpenAPI contract. The requested index, observed local ID, selected manifest and
config digest have distinct roles. Local original-archive association does not
establish registry promotion attestation; `registryDigestQualification` remains
false. The report is an inspection record, not a complete relocatable promotion
bundle: archives, journal, inputs, SQL snapshots and private logs remain retained
in the task evidence.

## Load and environment

The recorded host was Docker Desktop's Linux x64 WSL2 VM: Intel Core i7-14700T,
28 logical processors, 50,391,158,784 reported memory bytes, kernel
`6.18.40.1-microsoft-standard-WSL2`, and Docker CLI 29.8.2. A Linux SDK container
ran the PowerShell collector and managed workload probe. Owned TCP relays reached
the Docker host's published application ports; a separate persistent SDK container
ran the actual provider behind its byte relay. This topology and deterministic
provider are part of the observation, rather than external-provider or
production-network measurements.

The real release Migrator prepared a fresh owned PostgreSQL 16.15 database,
including current migration
`20261011204857_AddExactOriginalBalanceAdjustmentEvidence` and Wolverine storage.
PostgreSQL kept its default 100-connection limit. Redis was 7.4.11 and Wolverine
was 6.48.3. Both profiles used the same database, Redis and configuration:
Production, Wait migration mode, Wolverine/Postgresql transport with automatic
provisioning disabled, normal 30-second batch spending flush intervals, in-memory
media, media lifecycle disabled, HTTPS redirection disabled and tracing disabled.
The parent rate limit was 100,000 requests per 60 seconds. IP filtering was
explicitly disabled identically for this bridge topology, and its database
override was absent. These controls do not qualify production security, IP
enforcement or tracing. Virtual-key authentication, allowed-model selection,
billing and per-key/group controls remained active. The private virtual key and
backend master key were separate; no credentials or private captures are public.

The seed used eleven positive generated linked IDs for provider, credential,
author, series, model, cost, identifier, mapping, route policy, group and key.
The fresh group had initial balance/credits 100 and spent 0. Input/output prices
were 2.5/10 per million tokens. Each validated response reported 10 prompt and
5 completion tokens, for a configured expected cost of `0.000075`. Persisted
financial outcomes come from separate settlement receipts, not fixture declarations.

Both authenticated workloads posted to `/v1/chat/completions` with the same owned
model alias and Unicode prompt. JSON had to complete with `native provider response`;
SSE had to complete with `native stream response`, final usage and `[DONE]` framing.
Each workload used 1,000 measured requests, 100 configured warm-up requests,
concurrency eight and a 30-second deadline.

| Workload | Input fingerprint |
| --- | --- |
| `gateway-chat-nonstream` | `09b91805707bbe79a2aa5b0921879538e9c085fb682f6d01b30429cedd4ef066` |
| `gateway-chat-sse` | `8c43d3ec0a5cbc56989acbd09b4100721afa52d2e5a557efc228eea22c91c0e3` |

One complete unrecorded warm pair preceded five recorded pairs. Their order
alternated JIT/native, native/JIT, JIT/native, native/JIT, JIT/native. All twenty
recorded workload rows completed with zero failures. They retain 20,000 measured
requests and configure 2,000 warm-up requests; the source-bound successful warm
pair validates and discards another 4,400, for 26,400 procedure-backed requests.
Individual warm-up traces are not retained. Separate final settlement and offline
readback independently confirmed the full 26,400-request provider/financial delta.

Two separate authenticated preflights, one JIT JSON and one native SSE request,
preceded measurement. Their owned RequestLogs cumulative counts 1 then 2 were
observed while each host remained live. Their successful source-bound procedure
stopped the hosts gracefully, required exit 0, removed without force and confirmed
absence. Measured hosts followed the collector's same graceful shutdown contract.
A drain host cannot reconstruct a JIT request log lost from an earlier process.

Every variant used a fresh container with available image layers. Cold readiness
is fresh-container startup, not a cold pull or cold host-cache measurement. The
five retained native raw rows carry distinct actual container IDs. Raw JIT rows
do not retain IDs; no independent five-JIT-ID join is claimed.

## Results

The following are the original report's five-run medians, recomputed by the
completed offline verifier. Original precision is retained below, including throughput.
Signed change percentages are displayed to two decimals; the original report
retains their six-decimal summaries. Memory is sampled Docker container usage,
not process RSS or a guaranteed instantaneous peak. Pull bytes mean selected
compressed layer payload, not measured network traffic.

| Common metric | JIT | Native | Native change |
| --- | ---: | ---: | ---: |
| Image bytes | 487,602,912 | 228,022,668 | −53.24% |
| Compressed layer bytes | 138,374,168 | 68,604,724 | −50.42% |
| Cold readiness, ms | 2,167.7013 | 578.7509 | −73.30% |
| Idle memory bytes | 129,813,709 | 103,788,052 | −20.05% |
| Steady memory bytes | 287,624,397 | 163,368,141 | −43.20% |
| Sampled peak memory bytes | 287,624,397 | 180,669,645 | −37.19% |

| JSON metric | JIT | Native | Native change |
| --- | ---: | ---: | ---: |
| Workload elapsed, ms | 7,847.7866 | 7,194.5873 | — |
| Completed requests/second | 127.42446386093118 | 138.99337909208495 | +9.08% |
| Full-response p50, ms | 63.930155 | 56.056067 | −12.32% |
| Full-response p95, ms | 68.178562 | 64.011571 | −6.11% |
| Full-response p99, ms | 72.062861 | 68.032252 | −5.59% |

| Complete-SSE metric | JIT | Native | Native change |
| --- | ---: | ---: | ---: |
| Workload elapsed, ms | 7,054.3904 | 7,362.3734 | — |
| Completed requests/second | 141.75569302203633 | 135.8257650990644 | −4.18% |
| Full-response p50, ms | 55.998237 | 59.901218 | +6.97% |
| Full-response p95, ms | 60.097606 | 64.086421 | +6.64% |
| Full-response p99, ms | 66.858575 | 72.003789 | +7.70% |

All raw pairs are retained, including the third pair's higher JIT JSON tail.
The actual pair order is shown in the first row for each pair.

| JSON pair / order | Profile | Elapsed, ms | Requests/s | p50, ms | p95, ms | p99, ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1 / JIT, native | JIT | 7805.3455 | 128.11732677304292 | 63.86778 | 68.038472 | 71.994192 |
| 1 | Native | 7194.5873 | 138.99337909208495 | 56.056067 | 64.011571 | 67.989932 |
| 2 / Native, JIT | JIT | 8060.4769 | 124.0621383084666 | 64.016484 | 71.938905 | 72.062861 |
| 2 | Native | 7346.4866 | 136.11948873628924 | 59.911968 | 64.083212 | 76.177672 |
| 3 / JIT, native | JIT | 9177.8671 | 108.95777734676501 | 67.907299 | 119.984756 | 178.822323 |
| 3 | Native | 7821.2331 | 127.8570766545751 | 60.100108 | 76.072787 | 83.878675 |
| 4 / Native, JIT | JIT | 7839.2231 | 127.56366125107475 | 63.92909 | 68.133665 | 75.837369 |
| 4 | Native | 7134.2849 | 140.16821784058553 | 56.025715 | 63.962161 | 64.068117 |
| 5 / JIT, native | JIT | 7847.7866 | 127.42446386093118 | 63.930155 | 68.178562 | 72.017332 |
| 5 | Native | 7174.5022 | 139.3824926278509 | 56.042286 | 63.987289 | 68.032252 |

| Complete-SSE pair / order | Profile | Elapsed, ms | Requests/s | p50, ms | p95, ms | p99, ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1 / JIT, native | JIT | 7036.6734 | 142.11260678945254 | 55.998237 | 60.085275 | 64.03584 |
| 1 | Native | 7386.3427 | 135.3849991281883 | 59.901218 | 64.086421 | 72.018696 |
| 2 / Native, JIT | JIT | 7206.4969 | 138.76367587142096 | 56.066524 | 63.917035 | 67.892841 |
| 2 | Native | 7781.6016 | 128.50824950997233 | 60.069393 | 71.88108 | 75.96387 |
| 3 / JIT, native | JIT | 7429.5176 | 134.59824094097308 | 59.910588 | 64.083047 | 72.403379 |
| 3 | Native | 7286.3974 | 137.2420340400319 | 56.119606 | 64.120272 | 72.003789 |
| 4 / Native, JIT | JIT | 6950.4957 | 143.8746304094541 | 55.972647 | 60.05213 | 66.858575 |
| 4 | Native | 7054.3529 | 141.756446576411 | 56.00578 | 60.101675 | 63.995137 |
| 5 / JIT, native | JIT | 7054.3904 | 141.75569302203633 | 55.989313 | 60.097606 | 64.176817 |
| 5 | Native | 7362.3734 | 135.8257650990644 | 59.908081 | 64.035604 | 68.066275 |

The native image's embedded publish evidence records 218,050 ms total publish
time: 211,224 ms application and 6,826 ms health probe. The main executable is
97,429,952 bytes; the complete runtime inventory totals 104,070,289 bytes.
The selected native toolchain was SDK 10.0.401, ILCompiler 10.0.12 and Ubuntu
clang 18.1.3 with the recorded `bfd` linker flavor. Original tool executable
hashes are retained in the embedded evidence. Actual Docker build wall times
were 26,092.5705 ms JIT and 265,027.4312 ms native; these are separate from
the embedded publish duration.

Batch throughput and request latency have different timing boundaries. Neither
explains the other, and the profiles differ in persistence selection and worker
composition as well as compilation. Individual latency samples are not retained;
offline checks can recompute request/elapsed/throughput arithmetic, percentile
order and five-run medians, but cannot reconstruct each run's quantiles. No cause
is assigned. Any follow-up must preserve this adverse result rather than replace
it with a favorable rerun. [Issue #1647](https://github.com/nickna/Conduit/issues/1647) tracks the complete-SSE performance regression.

## Independent financial and provider settlement

The pristine and separately settled two-request preflight phases passed their
original producer checks. Their receipt hashes are respectively
`6b48ad671b18a17760e317aabcc79126da73546985dc8fd070adaa2695f05151` and
`395f9ee94457c62916cc5ec9f9707b788514101cdaaf2fba1c8f4f7558e8fc43`.
The file-only independent preflight receipt SHA is
`9173b74ffcdf65665d7ae0579dd7f0df50fae228dcaabd676e62b7831ab47fda`.
It closes retained inputs, durable live counts, source/query joins and observed
accounting; it does not execute live Docker or SQL queries.

Final settlement passed, with original receipt SHA-256
`24177d5861848482b65c216e90e30c43baf89ca264cfaa6d7a30e3840e923aca`.
Its final cumulative state and separately settled preflight difference matched
the observed values below; the offline verifier independently joined their originals.

| Phase | Logs | Input/output tokens | Cost / spent / debit total | Balance | JSON/SSE provider counters | Status |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| Pristine | 0 | 0 / 0 | 0 | 100 | 0 / 0 | Producer PASS; independent file readback PASS |
| Separate preflights | 2 | 20 / 10 | 0.00015000 | 99.99985000 | 1 / 1 | Producer PASS; independent file readback PASS |
| Final cumulative | 26,402 | 264,020 / 132,010 | 1.98015000 | 98.01985000 | 13,201 / 13,201 | Producer PASS; offline readback PASS |
| Measurement delta | 26,400 | 264,000 / 132,000 | 1.98000000 | −1.98000000 | 13,200 / 13,200 | Independently joined PASS |

Each non-pristine settlement phase uses an explicitly pinned new native image as
an unmeasured drain host, no chat requests or re-enabled EF workers, with the
normal 30-second flush configuration. Three balanced live observations must span
complete 30-second intervals, followed by successful graceful removal and a
balanced post-stop readback. The preflight's observed gaps were 36.8360948 and
36.2803365 seconds, followed by unchanged post-stop state. Final balanced sample
gaps were 39.7367853 and 39.6994801 seconds. Its post-stop observation at
`2026-10-12T05:14:09.0517006Z` remained balanced, and the original producer confirmed
exit-0 graceful removal of the explicitly selected new native drain.

The independent SQL oracle checks distinct logs, exact token/cost/routing/status
and default-field predicates, group balance/credits/spent, valid debit/idempotency
fields and totals/latest balance. Actual preflight ledger rows were two and final
batch ledger rows were 26, with 26 distinct valid idempotency keys. Ledger rows
are not equated to request count. GlobalSettings and unrelated financial
fingerprints stayed unchanged across pristine, preflight and final receipts.
The eleven fixed owned Redis obligation keys were absent in every retained final
balanced observation and the post-stop readback, as they were at pristine and
preflight. Started/settled, rate and cache markers are outside the financial
obligation test. Final JSON/SSE counters were 13,201 each, with all other twelve
counters zero. No balances, counters or SQL
rows are reset. A drain can flush shared Redis obligations; it cannot recover a
request log lost with another process.

## Verification, cleanup and disposition

The offline verifier passed original attempt/report joins, native
OCI/publish/source/toolchain closure, JIT layer-byte checks, environment/workload
fingerprints, zero-failure raw rows, row arithmetic, sampled-memory summaries,
medians and comparisons, preflight durability and the separately settled exact
provider/financial delta. Receipt SHA-256:
`7669eae1bf1c75db89b7286f650ff52f35a2b45cdeee8cef49cd94e1421d96f2`.
Original query sources and producer receipts are joined without executing SQL
by this offline verifier.

After successful final settlement and root-approved independent verification,
the cleanup producer passed, with receipt SHA-256
`525416319fd4a1e6673c99eb0d9ba6f2c2e223a8d7233c2de5531ceaae77d98a`.
It retained a fresh read-only financial witness and a complete 2,068,765-byte
private PostgreSQL custom archive and TOC. Original remote/local archive hashes
both were `f084c890bce73d5c9d8ad20c08244083931e55c2199435bc296449f3006d10c6`.
It joined the persistent provider's capture streams with exit 0, removed four
owned containers and two volumes, and confirmed all six known application image
references absent. Its protected-base checks and original-input hash closure
passed; original evidence directories were retained. There was no SQL mutation,
global prune, financial reset or original deletion. Independent live readback
passed all 21 exact Docker checks:
four container names and IDs, two volumes and six image references were absent,
and five protected base image identity/tag/digest sets were unchanged. All 342
original input hashes remained unchanged before and after the check; private
archive, TOC and remote hash captures were verified by hash only. Independent
receipt SHA-256:
`f70f95c42956f134c78b8c1dc357bbe13eeae0185e4957fcf4e346bd367e1433`.

This fresh owner/source is separate from the earlier failed unit111 fixture,
whose partial accounting and failed attempt were archived before its owned
resources were removed. That failed run remains unqualified; it was not reset
or resumed into this result. The unit113 attempt history and any failures must
also remain intact.

The recorded SSE throughput loss leaves the policy's no-throughput-loss criterion
unmet, and its 7.70% p99 increase exceeds the 5% p99 limit, regardless of the JSON
improvement and memory/startup reductions. Numerical criteria must be reported
per workload. Broader qualification remains blocked.
This controlled whole-profile VM observation does not establish an isolated
persistence microbenchmark, representative production load, security/SBOM/provenance
acceptance, excluded-worker parity, Admin HTTP billing enqueue, causal attribution,
accepted qualification windows, ordered operational Admin-then-Gateway soaks,
an actual rollback, successor-ADR approval or production promotion. ADR 0009's
staged selected-slice boundary and JIT rollback remain in force. See the
[Admin matrix](native-aot-admin-feature-matrix.md),
[Gateway matrix](gateway-native-aot-feature-matrix.md),
[Admin comparison](native-aot-admin-controlled-benchmark.md),
[staged persistence decision](../decisions/0009-staged-native-persistence-refactor.md),
[promotion policy](../../deploy/native-canary/promotion-policy.json) and
[canary runbook](native-aot-canary.md).
