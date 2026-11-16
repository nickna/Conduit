# Managed JIT runtime qualification

This fixture collects the .NET 11 phase2 evidence tracked in [#1766](https://github.com/nickna/Conduit/issues/1766). It keeps source, compiler/runtime, infrastructure, protocol correctness, accounting and performance attribution explicit. It changes no production settings, database schema or financial state outside its task-owned fixtures.

## Source cohorts and execution

| Cohort | Application source | Compiler/runtime | Purpose |
| --- | --- | --- | --- |
| legacy | `f68cb5fae1130bbe1ef4fc30aca7a3266094dbaa` | SDK10.0.404/.NET10.0.15 | Unchanged pre-upgrade source, including the historical Responses defect. |
| fixed | `911d7b6bbe54b4a6b77058b75ee2a86bc2d59097` | SDK10.0.404/.NET10.0.15 | Separate #1770-fixed .NET10 control. |
| candidate | Explicit reviewed full commit SHA | SDK11.0.100/.NET11.0.0 | Migration source, including its necessary compatibility/billing corrections. |

The rebuilt controls are separate from the frozen original phase0 binaries and measurements. They use a later .NET10 compiler patch with explicitly recorded identities. Comparing fixed10 with candidate11 measures the migration's combined source/compiler/framework/runtime effects; it does not isolate a pure CLR-only cause or a later optimization.

The workflow uses native `ubuntu-24.04` and `ubuntu-24.04-arm` runners. Every comparison runs both runtime variants on the **same VM within one job**. Its host-published JIT results are distinct from final Resolute-container performance and from production observations. Actual CPU/features, OS, kernel, glibc, cgroup limits, SDK/runtime, application/driver binaries and infrastructure identities are retained. Cross-architecture or cross-VM rate comparisons do not substitute for matched pairs. QEMU rates do not qualify ARM64.

## Workloads

- `main`: the23-case phase0 Gateway catalog, repeated five times for all three sources; then seven ordinary JSON/SSE/tool/Responses cases with100 separate warmups and1,000 measured requests per run for fixed/candidate. Profiles remain separate.
- `pressure`: four separately derived 10 MiB standard/enhanced SSE fast/paced controls, with **64 KiB** requested receive buffers and actual OS values recorded. The frozen C# is unchanged; `pressure-native-catalog.json` identifies the new input cohort. One request per case/run is exploratory, not a p99 population estimate. Provider write/awaited-flush spans must be observed; pacing alone does not establish upstream pressure.
- `pressure-diagnostic`: the first **original 8 KiB** fast-reader case, once per source on the same native VM, with TCP/sysctl and bounded `ss -tinm` observations. The original catalog remains unchanged. Literal observed deadline failures remain failed protocol observations; diagnostic collection never qualifies performance or finance. Unexpected errors fail collection.
- `admin-cache`: matched Admin reads and source-backed populated cache/streaming-metrics-collector microbenchmarks. Whole-process observations are labelled separately from operation timings. A list-based collector microbenchmark does not establish durable queue throughput.

Workload and pressure C# sources are copied unchanged from phase0. They retain their historical `Phase0` names and narrative fields; external source/binary bindings identify the executed cohort. The client/provider always run on the fixed .NET10 runtime. Each Gateway cohort/run gets a fresh provider with identical logical payload IDs across runtime pairs. Unique artifact IDs and provider-instance scopes are separate: equal-length different cohort words can change BPE admission estimates.

The frozen clients remain unchanged. New collections use a separately versioned `ProviderV2` on SDK 10.0.404/runtime 10.0.15 with the same wire payloads and pacing. The original provider can lose its cancellation-timestamp callback when its registration is disposed before the exception handler runs. V2 records a current observation at catch only when the public request-aborted token is actually cancelled, retaining callback/catch provenance and the raw callback timestamp. It does not backdate cancellation, accept zero evidence or remap older datasets. Identity, complete published inputs and raw V2 fields are checked separately.

The frozen client validates complete Unicode content, fragmented tool arguments, event/usage/finish/DONE/EOF contracts, uploads and cancellation boundaries. Cancellation scenarios are not counted as completed-response capacity. Known legacy Responses502s remain failed observations with missing charges; their error rates never become successful throughput. No paid provider is contacted.

The Gateway observer is an opt-in startup hook used only in the Gateway child. `ObserverV2` adds actual Server GC/configuration metadata to the frozen observer, with the same fixed hook binary used by both runtimes. It samples working set every 100 ms and reports process-wide CPU/allocation/GC windows. Separate warmups are excluded from Gateway windows. The windows include client startup/finalization waits, server background services and observer work; there is no idle subtraction or pure per-request attribution. First-content relay timestamps come from the fixed provider/client on the same kernel clock. Gateway first-event flush can precede actual content and does not prove network-wire delivery.

**Allocation acceptance remains open.** The .NET 10.0.15 DATAS/Server GC counter can omit retired-heap history, even during positive windows. The analyzer preserves raw values and reports affected allocation metrics/ratios/budgets as unavailable. `allocationGateSatisfied` and `performanceAcceptanceGranted` remain false. Neither clamping nor changing GC settings can repair the frozen baseline. See [analysis details](README-analysis.md) for the upstream fix and validity policy.

## Commands and retained inputs

Run the workflow for a reviewed candidate that is an ancestor of the qualification checkout:

```powershell
gh workflow run runtime-qualification.yml --ref <qualification-branch> -f candidate_revision=<full-reviewed-commit-sha>
```

The workflow must already exist on the default branch for manual dispatch. A PR changing this fixture also starts qualification against its captured base revision. During initial implementation, retain the PR-triggered run separately from any later default-branch dispatch.

On a native Linux host with exactly the declared SDKs, Docker and Python installed:

```powershell
./scripts/qualification/run-runtime-matrix.ps1 -CandidateRevision <full-sha> -Architecture X64 -Profile main -OutputDirectory /absolute/fresh/output
./scripts/qualification/run-runtime-matrix.ps1 -CandidateRevision <full-sha> -Architecture Arm64 -Profile pressure -OutputDirectory /absolute/fresh/output
./scripts/qualification/run-runtime-matrix.ps1 -CandidateRevision <full-sha> -Architecture Arm64 -Profile pressure-diagnostic -OutputDirectory /absolute/fresh/transport-diagnostic
./scripts/qualification/AdminCache/run-admin-cache-matrix.ps1 -CandidateRevision <full-sha> -Architecture Arm64 -OutputDirectory /absolute/fresh/admin-cache-output
```

Three to six repetitions are supported; five is the default. The cap protects the frozen provider's bounded observation inventory. Use a fresh directory for every cohort. Source archives, all published application/tool binaries, exact configuration/catalog/script copies, individual client/provider/observer reports, raw metrics, SQL/RDB snapshots and analysis are retained. Failed cohorts remain separate. Token/body sizes and wire framing remain bounded.

## Accounting and interpretation

Before every Gateway SIGTERM, a fixed .NET10 read-only `DrainProbe` waits for the exact expected request-log count, log/ledger/spend/balance equality and empty known PostgreSQL spend queues while the consumer remains alive. It runs outside the measured window and retains every poll. Response EOF, Redis-zero and exit0 alone cannot prove durable settlement: an actual legacy smoke retained two audio spends in PostgreSQL after shutdown. A restored copy settled both once across11→10→11 without changing unrelated historical holds.

Final PostgreSQL/Redis snapshots are checked with exact Decimal/rate/unit arithmetic and no financial tolerance. Explicit after-usage indeterminate holds remain part of the expected policy; tools never release or reconcile them. Before-usage estimates are checked against persisted token cohorts, rates and reservation bounds rather than billed as unseen final provider usage. Normal RequestLogs have no universal billing ID, so the financial check uses exact scoped cohorts/counts plus the declared reservation contract instead of inventing per-request joins. Cross-store snapshots are not distributed atomic transactions.

The historical control is allowed to produce only its specifically verified #1770 failure signature and missing-charge amount. It remains unqualified. Any additional legacy failure, or any fixed/candidate financial failure, fails collection. The pressure profile has no legacy Responses requests and requires ordinary financial success.

[Analysis details](README-analysis.md) document per-run medians/ranges, same-run paired budgets, histogram boundaries, meaningful tail sample sizes and exact financial expectations. Working phase0 budgets are diagnostics: throughput>=95%; response tails<=max(1.05×baseline, baseline+2ms); CPU/allocation+5%; sampled memory+10%; readiness<=max(1.10×baseline, baseline+50ms). Low-count long/pressure tails remain exploratory. A passing collection job writes `performanceAcceptanceGranted:false`; review and diagnose every breach before a release decision. It never accepts a tradeoff automatically.

Actual packaged correctness, three-service rollout/rollback, image-policy scans, Data Protection, host configuration and controlled shutdown/failure receipts are separate qualification cohorts. NativeAOT keeps its declared x64 canary scope. Production migration/promotion requires separate authorization and the existing exact-revision release process; these fixtures do not operate a production environment.
