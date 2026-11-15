# .NET 11 managed-service qualification

Phase 2 is tracked in [#1766](https://github.com/nickna/Conduit/issues/1766), under
the [.NET 11 epic](https://github.com/nickna/Conduit/issues/1763). This document
records qualification and a rehearsed rollback procedure. It does not authorize
production promotion. Native AOT retains its separate capability matrix and
[canary procedure](native-aot-canary.md).

## Candidate and evidence boundaries

[PR #1771](https://github.com/nickna/Conduit/pull/1771) merged as
`66c5c3dad682d40d13ed7391b327ea2397553d58`. Its reviewed head was
`e04e87b073fac689e803b794c5957f02be84c1a0`. Local packaged rehearsals use the
reviewed head's actual binaries and image identities; they are not relabelled as
merge-commit release images. A production release must retain its own exact
master SHA, successful CI, registry digests and attestations through the existing
[release process](ci-validation.md).

The controls are unchanged legacy source
`f68cb5fae1130bbe1ef4fc30aca7a3266094dbaa` and a separate Responses-fixed .NET 10
control, `911d7b6bbe54b4a6b77058b75ee2a86bc2d59097`. Rebuilds use SDK 10.0.404 and
runtime 10.0.15. They are separate from the original frozen phase-0 builds.
The candidate uses SDK 11.0.100/runtime 11.0.0, retaining EF Core 10 and Npgsql 10.
This comparison includes the migration's necessary source/compiler/framework
changes; a difference cannot automatically be attributed to the CLR alone.

The exact merged candidate's
[required CI run](https://github.com/nickna/Conduit/actions/runs/43768423253)
passed all 21 expanded jobs. The
[authenticated evidence intake](dotnet11-phase2-master-ci.json) verifies selected
artifact ZIP digests and individual TRX outcomes: main 6,337 passed with three
existing skips, serialization 290, fault injection 105, billing invariants 123
with one existing diagnostic skip, and native ARM64 duration/serialization/OpenAPI
39/290/109. All six component coverage ratchets passed; this is not a whole-repo
coverage claim. PostgreSQL 16/17 locks, migration, official SDK/contracts,
SignalR, durable transport/media/S3, CodeQL, dependency policy and both native
architecture packaged business-flow jobs also passed. These correctness results
are distinct from native performance measurements and the separate Native AOT
pipeline. Changes to the qualification tooling and WebAdmin image receive their
own PR CI and source/image identities.
The merged candidate's
[separate Native AOT pipeline](https://github.com/nickna/Conduit/actions/runs/43768423144)
also passed all three declared linux-x64 publication/image jobs. This preserves
its existing feature matrix; it does not qualify native ARM64 AOT or production
promotion.

## Recommended reference deployment

The actual production host and database inventory remains unconfirmed. These are
reference recommendations, not observations of a deployed environment.

| Layer | Recommendation | Qualification boundary |
| --- | --- | --- |
| Linux host | Maintained Ubuntu Server 24.04 LTS, native x64 or ARM64 | Use the maintained GA/cloud kernel appropriate for the hardware. GA generic is the 6.8 family; record the actual kernel, including HWE/cloud variants. |
| Application userspace | Existing pinned Resolute/Ubuntu 26.04 ASP.NET container family | Container glibc/userspace and the host kernel are separate inputs. Retain both identities. |
| x64 CPU | Require x86-64-v2; prefer v3-capable server CPUs | .NET 11 raises the minimum to v2. Linux ReadyToRun targets v3; supported older CPUs may do extra startup JIT work. Record VM-exposed features. |
| ARM64 CPU | Native ARMv8.0-A or later; prefer an LSE-capable server | Linux's minimum is unchanged, while the new ReadyToRun target includes LSE. Emulated measurements cannot qualify native performance. |
| Existing PostgreSQL | Keep its supported major; use the current minor patch | Qualify 16.16 for PostgreSQL 16. Avoid coupling a major database upgrade to this runtime rollout. |
| New PostgreSQL | 17.12, with PostgreSQL 16.16 compatibility coverage | These are the repository's exercised majors. PostgreSQL 17 support ends November 8, 2029. Newer majors need their own qualification. |

Recommendation checked November 15, 2026 against the primary
[.NET supported OS](https://github.com/dotnet/core/blob/main/release-notes/11.0/supported-os.md),
[CPU requirements](https://learn.microsoft.com/en-us/dotnet/core/compatibility/jit/11/minimum-hardware-requirements),
[official image families](https://github.com/dotnet/dotnet-docker/blob/main/README.aspnet.md),
[Ubuntu kernel lifecycle](https://ubuntu.com/kernel/lifecycle),
[Ubuntu release lifecycle](https://ubuntu.com/about/release-cycle) and
[PostgreSQL support policy](https://www.postgresql.org/support/versioning/).
Keep these pins under normal security maintenance after qualification.

## Completed bounded rehearsals

The local Linux amd64 results and their limits are recorded in the
[phase-2 issue](https://github.com/nickna/Conduit/issues/1766#issuecomment-6902384561).
The [packaged evidence index](dotnet11-phase2-packaged-evidence.json) retains
source/image/archive/report identities, the final financial and browser checks,
actual scan counts and cleanup limits. Its logical bundle paths identify locally
retained evidence, not public download URLs or production deployment digests.
They do not establish native ARM64 acceptance or production capacity.

| Rehearsal | Result and limits |
| --- | --- |
| Complete Gateway workload-method smoke | 23 cases × three sources × three repetitions, one request each: 207 observations. Only the three historical Responses JSON failures remained. All nine financial drains passed before shutdown. This is functional evidence, not a p99 estimate. |
| Exact financial conservation | Fixed .NET 10 and candidate each recorded `0.02311500` in request costs, ledger debit and lifetime spend. Candidate's three after-usage indeterminate holds retained `0.12414000`; fully reconciled is intentionally false. Legacy retained its known missing `0.00022500` charge and remains unqualified. |
| Durable spend recovery | A restored copy of an earlier failed smoke had two actual queued audio spends. Both settled once across 11→10→11, adding exactly `0.00250000`. Original failed SQL/RDB evidence and unrelated historical holds were preserved. |
| API image rollback | PostgreSQL 17.12 legacy→candidate→legacy and PostgreSQL 16.16 fixed-control→candidate→fixed-control passed. Each retained 12 request logs, `0.00480000` exact spend, no duplicate debits, unchanged migrations, protected credentials, cache restart/invalidation and queued message compatibility. |
| Complete three-service rollback | Admin/Gateway/WebAdmin 10→11→10 passed 62 checks: 33 API/shutdown and 29 WebAdmin checks, with 12 financial requests. Actual headless Edge exercised the browser/BFF and one-use credentials across swaps. |
| WebAdmin image maintenance | Pinning Node 22.23.5 and removing the unused global npm/npx bundle only from the production stage passed another complete 62-check rollback. All 2,144 `/app` entries retained identical hashes/links/modes/owners versus the base-update-only image. Its actual digest-addressed scan reported zero HIGH/CRITICAL and one MEDIUM finding; no exception or policy change was used. The tracked recipe's different layer grouping still needs its own exact source/image identities. |
| Authentication limits | Locally signed Clerk-compatible JWTs went through the production verifier. Unsigned, forged and non-admin issuance was denied; cross-version one-use credentials were accepted once and replay denied. This does not exercise a live identity provider, revocation or logout. |
| Data Protection | Actual production protector and file keyring worked across 10→11 and rotated 11→10; a different application name failed closed. Packaged rollback retained the same keyring. |
| Lifecycle and configuration | Eight packaged lifecycle and four actual-host configuration cases passed. Both applications clear JSON/CLI providers and use environment variables. Invalid environment values failed before readiness; valid environment values won over conflicting JSON/CLI inputs. |
| Frozen baseline integrity | All 365 original and 2,534 extended inventory entries still match their original byte lengths and SHA-256 hashes. Historical failures were not rewritten. |

Controlled .NET 10 background-worker failures stopped the host with exit 0;
.NET 11 surfaces the background-service exception and terminates unsuccessfully.
In the tested packaged PID1 environment the observed exit was 139, versus 134
behind an init process. Minimal managed and native `abort()` controls showed the
same placement-dependent distinction. Explicit SIGTERM exits were 0 for both
managed services. This bounded characterization does not make an arbitrary 139
an acceptable production exit. Investigate unexpected crashes, OOM and SIGKILL.
WebAdmin's shipped Next.js signal cleanup exits 143 after SIGTERM; this was
independently checked against its actual shipped code and bounded ready/stop
controls, then enforced in the three-service rehearsal.

## First native Admin/cache results

The initial [native measurement run](https://github.com/nickna/Conduit/actions/runs/43770860051)
used harness head `873587a32b22f4447dbbf2984779402a91b17604` to measure candidate
`66c5c3dad682d40d13ed7391b327ea2397553d58`. Both Admin/cache jobs passed execution,
artifact-digest verification and exact analyzer replay. The
[compact native index](dotnet11-phase2-native-cache.json) retains source/hardware,
per-run observations, paired summaries and raw-artifact identities. New runs
remain separate cohorts; their quantiles are not pooled with these results.

| Architecture | Fixed .NET 10 → .NET 11 paired Admin RPS ratio | Paired CPU/call ratio | Working throughput result |
| --- | ---: | ---: | --- |
| x64, 4 logical Xeon 6975P-C CPUs | 0.9641 | 0.9479 | Within the 0.95 threshold |
| ARM64, 4 Neoverse-N2 CPUs | 0.9189 | 0.9794 | Breach; no accepted tradeoff |

These are medians of same-run ratios, not ratios of aggregate medians. Both
architectures' fixed-control full-response p50/p95/p99 and sampled-memory
comparisons fit the working rules. That does not erase the ARM throughput
shortfall. Other control comparisons also breached diagnostics: x64 legacy→fixed
throughput/p99 and legacy→candidate p99, and ARM64 legacy→fixed CPU. Interpret
those variations before assigning a pure runtime cause. Admin hosts used actual
Server GC, so affected .NET 10 allocation ratios remain unavailable.

The separately scoped workstation-GC cache microbenchmarks show a pricing-cache
latency increase against the fixed .NET 10 control on both architectures:

| Architecture | Cold p50 ratio | L1 p50 ratio | Redis L2 p50 ratio |
| --- | ---: | ---: | ---: |
| x64 | 1.377 | 1.243 | 1.756 |
| ARM64 | 1.311 | 1.577 | 1.215 |

The .NET 10 pricing controls were substantially more stable. Inspected cache and
entity source blobs are identical across the three cohorts, while compiler,
runtime/framework JSON generation and the Microsoft Redis-cache adapter changed
together. L1 reconstructs owned graphs without inherently decoding payload JSON;
serialization alone is not an established explanation. The current pricing L1
fixture allocates 7,016 process-wide bytes at p50 on both candidate architectures.
This specific workstation-GC scope is separate from the invalid Server GC
allocation baseline and does not qualify historical observations without GC
metadata.

The [phase-4 investigation](https://github.com/nickna/Conduit/issues/1768#issuecomment-6903032657)
will isolate reconstruction, fixed-byte serialization, generation and Redis
adapter costs, then measure the actual Gateway lookup mix before an immutable
pricing-read-model pilot. No cache optimization has been implemented here.
The collector's 10,000-chunk snapshot pattern still allocates approximately
1.19 GB per stream in its synchronous thread-local scope; its bounded-work
replacement remains [phase 3](https://github.com/nickna/Conduit/issues/1767).
No result in this section grants performance acceptance.

## Matched measurement method

The [qualification fixture](../../scripts/qualification/README.md) runs default-GC
JIT services from clean source archives. The workflow runs x64 and ARM64 on native
Ubuntu 24.04 runners. Both runtimes execute on the same runner within each job;
cross-VM and cross-architecture rates are not matched pairs. Host-published
measurements remain separate from Resolute container performance.

The main profile repeats the frozen 23-case catalog and adds 1,000 measured
requests plus 100 separate warmups per run for seven ordinary JSON/SSE/tool/
Responses cases. Five repetitions are the default. Quantiles are calculated per
run and reported with medians/ranges and same-run pairs; request samples are not
pooled to manufacture confidence. Long-stream/cancellation/pressure distributions
remain diagnostics at their declared small sample sizes.

A separate pressure profile uses the frozen C# 10 MiB fast/paced standard and
enhanced SSE controls with a separately derived 64 KiB receive-buffer catalog,
including actual OS buffer observations and provider write/awaited-flush timing.
The original 8 KiB first case timed out in both native legacy controls before
reaching the candidate. Those incomplete-wire observations remain failed
evidence, not a demonstrated .NET 11 regression. A separate original-8-KiB
diagnostic profile runs each source once on the same VM, retaining TCP/sysctl
observations and explicit deadline outcomes with no financial or performance
qualification. Neither read pacing nor a fast provider flush proves upstream
backpressure. Admin/cache/collector measurements use a
separate profile. A list-based streaming-metrics microbenchmark is not evidence
of durable queue throughput.

Workload/provider binaries remain on .NET 10. Each source/run gets a fresh provider
with identical logical payload identifiers across runtime pairs. Exact sources,
SDKs, published binaries, observer versions, CPU/OS/kernel/glibc/cgroup data,
infrastructure identities, raw individual reports, metrics, financial SQL/RDB
and failed attempts are retained. Gateway windows exclude separate warmups but
include client startup/finalization and server background work. No idle CPU is
subtracted. A sampled working-set maximum is not an instantaneous allocation peak.

Working phase-0 budgets remain diagnostic proposals: throughput at least 95% of
baseline; full-response p50/p95/p99 at most `max(1.05 × baseline, baseline + 2 ms)`;
CPU/allocation +5%; sampled memory +10%; readiness at most
`max(1.10 × baseline, baseline + 50 ms)`. Minimum tail sample sizes and paired
repetitions are enforced by the analyzer. Every breach needs diagnosis or a
documented accepted tradeoff before rollout. Collection success explicitly writes
`performanceAcceptanceGranted: false`.

### Allocation gate: open

The complete smoke exposed four decreasing .NET 10 process allocation counters
under default adaptive Server GC, approximately −546 to −584 MB. The
[upstream DATAS fix](https://github.com/dotnet/runtime/pull/131069) retains allocation
history for retired heaps in .NET 11; .NET 10.0.15 omits that history. A positive
window can also be understated. Therefore affected .NET 10 values, including
positive ones, are retained as raw evidence and excluded from allocation ratios.
Their allocation acceptance gate remains unsatisfied.

The versioned observer adds actual GC-mode/configuration metadata on both
runtimes. The frozen observer remains preserved. No value is clamped, replaced
with the approximate high-water counter, or made comparable by changing GC
policy. Allocation-tick events count allocation-context handouts and do not
provide an exact substitute with an established window-error bound. A bounded
standalone reproducer recorded heap-count transitions without a sampled counter
decrease; asynchronously dispatched heap events cannot establish exact retirement
boundaries. This does not exonerate the real workload failure. Latency, CPU, sampled memory, protocol and exact financial
collection can proceed while allocation qualification remains open.

## Managed rollout and rollback procedure

This procedure combines the isolated three-image swap rehearsal with separately
tested SQL/RDB restore and live known-spend drain controls. The 62-check browser
rehearsal alone does not prove every queue-drain or backup/restore step under
arbitrary traffic. Adapt the procedure to actual deployment identities only after
the outstanding gates and separate rollout authorization.

1. Retain a coherent immutable .NET 10 three-image rollback set, archive hashes,
   OCI manifests/configs, source identities and successful policy receipts. Keep
   its runtime images available throughout the rollback window. Do not select a
   mutable tag. Reconstructed local baseline images do not identify the currently
   deployed production set.
2. Take verified database and Redis backups and retain the Data Protection keyring,
   application name/master key, provider-protection credentials, queue schemas
   and configuration. Retain the WebAdmin public origin and verifier identity.
   Do not combine database-major, messaging-format or keyring changes with this
   swap.
3. Use the selected Admin image's standalone migrator, then run normal hosts with
   `CONDUIT_MIGRATION_MODE=Wait`. Check schema readiness and migration history.
   Never apply downgrade migrations during rollback.
4. Stop admission to the selected instance set and drain while consumers remain
   alive. Require expected RequestLog counts, exact request-cost/ledger/spend/
   balance conservation and empty known active/scheduled/outgoing/dead-letter
   spend queues. HTTP EOF, zero Redis pending spend and process exit 0 are
   insufficient. Retain durable task/lease and queue state before stopping hosts.
   Record legitimate indeterminate usage holds and preserve their evidence;
   never force them to zero to satisfy a drain check.
5. Stop WebAdmin and managed hosts within their configured timeouts. Record actual
   exit codes, timestamps, finalizers, restart counts and OOM state. Enforce the
   declared WebAdmin SIGTERM 143 and managed-service SIGTERM 0 behavior; unexpected
   crashes or forced termination fail the rehearsal.
6. Switch the coherent three-image set while preserving shared state. Check
   migrations/readiness, authentication/authorization, protected provider and
   ephemeral credentials, BFF one-use/replay behavior, cache invalidation/restart,
   durable message delivery, streaming wire contracts and exact financial results.
7. If a gate fails, restore the retained coherent .NET 10 set and repeat the same
   readiness, state, wire and financial checks. Preserve schema, queue payloads,
   idempotency keys and unresolved holds. Do not reprice or fabricate missing
   usage while swapping runtimes.
8. Retain staging observations separately from production observations. Any later
   production promotion uses the existing exact-master release workflow and
   records its own observation interval, actual traffic, deployment digests,
   rollback trigger and outcome.

## Release gates

Use the [phase-2 checklist](https://github.com/nickna/Conduit/issues/1766) for
current completion and the latest native-run results. Evaluate these gates for
each actual release candidate:

- Complete exact-candidate required CI with its discovery, coverage, fault,
  PostgreSQL, CodeQL, dependency and image-policy evidence.
- Complete native ARM64 correctness and matched measurements, retaining actual
  CPU/kernel/glibc/quota and image/runtime identities. No emulated result closes
  this requirement.
- Review the expanded repeated-run performance results, diagnose budget breaches,
  and establish valid allocation acceptance or an explicitly accepted tradeoff.
- Qualify final source-bound images, including the independently tested WebAdmin
  base/runtime-dependency maintenance, without hiding historical HIGH findings.
- Confirm the actual deployment inventory and separately authorize production
  promotion. No production rollout has been performed as part of these fixtures.
