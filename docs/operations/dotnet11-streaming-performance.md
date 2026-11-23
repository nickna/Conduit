# .NET 11 phase 3: streaming and spend experiments

Tracking: [phase 3](https://github.com/nickna/Conduit/issues/1767),
[epic](https://github.com/nickna/Conduit/issues/1763).
The [phase-2 qualification](dotnet11-managed-qualification.md) gates remain open.
PR #1772 merged at `b1d49922b8dfdc6719c6d46d123f2eb5a265dcca`; this is the
before source for the historical experiments. The current Gateway PR comparison
uses the current base commit, including the dependency changes in that commit.
Both arms use SDK 11.0.100/runtime 11.0.0,
native Linux hardware and the same default GC policy. Historical qualification
sources and datasets are unchanged.

## Implementation and scope

| Experiment | Change and compatibility boundary | Disposition |
| --- | --- | --- |
| Enhanced metrics | Replace retained timestamp/interval lists with first/last/count aggregates. Adjacent intervals telescope to `(last - first) / (count - 1)`. The Gateway reads first-token state directly. Preserve the historical distinction between timestamp count and reported token count, including a late `RecordFirstToken`. | Implemented in #1778; focused behavior and allocation-scale checks pass. Native evidence/adoption status is tracked in #1767. |
| SSE UTF-8 framing | Prototype commit `483f50eec` serialized through generated metadata directly to UTF-8, then copied into a private frame. It reduced intermediate allocations and retained two byte arrays. | Deferred from production: all four original/repeat architecture blocks exceeded the standard-slow process CPU budget. Attribution remains unresolved; the production writer is restored to the before implementation. Prototype/evidence remain available in [#1781](https://github.com/nickna/Conduit/issues/1781). |
| Groq metadata | Lazily create immutable generated metadata once for the stable client policy. The prototype kept the public options-taking converter independent. | Deferred to #1782. The mixed-variant benchmark showed a repeatable x64 slowdown in the public-options control, even after its method body was restored. Isolated fresh-process evidence is needed before adopting the private cache. |
| General provider parsing | Benchmark controls characterize DOM/string/typed alternatives. Existing virtual transform/mapping hooks, malformed-frame exceptions and multiline handling make a generic replacement a compatibility change. | Defer to #1779 until those behaviors are specified and qualified. Micro controls alone do not prove compatibility. |
| BodyWriter / flush coalescing | The UTF-8 candidate keeps the existing `Body.WriteAsync` followed by `Body.FlushAsync` for each event. Complete serialization before writing protects the wire from converter failure. | Defer to #1780; no benchmark establishes a gain or an acceptable latency/backpressure policy for that alternative. |
| Spend receive batch | Measure 10/25/50/100 with concurrency one, strict ordering and 250 ms polling. Distinguish handler entry, handler completion and independently observed SQL settlement. | Production policy remains batch 10 pending measured results and a separate adoption decision. No partitioning, handler concurrency or transport change. |

Standard streams skip metrics collector construction and per-chunk metrics
calls. Content chunks without usage go directly to the writer, and standard
chunks with both choices and usage keep the projection that excludes usage.
Groq measurements are provider-specific;
the generic provider stub in the Gateway matrix does not measure Groq's metadata
optimization. Micro warmups exclude cold metadata initialization and cannot
establish first-request improvement.

The writer compatibility tests preserve content/DONE LF framing and named-event platform newlines,
serializer/encoder policy, cancellation checks, per-event flush completion,
error dialects, counters and DONE idempotence. Each call owns its frame through
the asynchronous write. This ownership check does not promise ordering for
concurrent calls on an HTTP response. No reusable mutable writer buffer is shared.
The UTF-8 prototype is preserved in history, rather than active production code.

## Reproduction and evidence

The `Phase 3 streaming performance` workflow runs `main`, `pressure`,
`micro-queue` and an identical-binary `control` separately on `ubuntu-24.04`
x64 and `ubuntu-24.04-arm` ARM64.
Each job has read-only repository permissions and no production secrets. The
main and control profiles have a 120-minute deadline, and pressure and
micro-queue have a 90-minute deadline. The control repeats the main workload with copies of the
same published Gateway and migrator, separate databases and fresh processes.
It measures variation and cannot satisfy the candidate comparison. The Gateway
candidate jobs retain their artifacts before checking the existing budgets.

For Gateway qualification, run from a full checkout containing the candidate:

```powershell
./scripts/performance/phase3/run-paired-gateway.ps1 `
  -CandidateRevision <full-candidate-SHA> -BaselineRevision <full-base-SHA> `
  -Architecture X64 `
  -Profile main -OutputDirectory <fresh-directory>
```

Repeat with `pressure` and on matching native ARM64 hardware. Main and control
CI use six fresh-process pairs so each arm runs first three times. Pressure
uses five pairs. The versioned provider, client, observer, capture and
reconciliation tools remain source-bound. Both sources must pass protocol and
financial checks. Main derives its catalog from the unchanged phase-0 fixture,
retaining all case options and increasing sample counts and warmups for
ordinary cases. It also runs seven ordinary protocol cases with 1,500 measured
requests and 150 warmups per run. Low-count long-stream and pressure tails
remain exploratory distributions.

For compatibility with the unchanged capture guards, the isolated databases,
resource labels and cohort IDs retain the `phase2` / `fixed` / `candidate` names.
Here `fixed` means the .NET 11 before source. Unique resource ownership, source
revisions, canonical archive hashes, actual workspace tool hashes and published
input hashes are recorded. The naming does not turn these into .NET 10 cohorts.

Gateway evidence includes per-run CPU, allocation, throughput, first flush,
p50/p95/p99, failures, observed queues/drains and exact monetary reconciliation.
Recorded spend conservation and known-charge checks must pass. Legitimately
indeterminate cancellation holds remain disclosed; a snapshot is not a fabricated
atomic SQL/Redis transaction. Financial proof is distinct from performance proof.

The existing working budgets remain diagnostic: throughput >=95% of baseline;
latency <=`max(1.05 * baseline, baseline + 2 ms)`; CPU/allocation <=105%; sampled
memory <=110%. Check every workload and individual repetition, without pooling
requests or silently accepting budget breaches. Unlike the older .NET 10 ServerGC
control, both .NET 11 arms can supply valid precise process allocation windows.
Those windows include background/runtime/observer work and are not per-request
traces or instantaneous peaks.

`run-paired-micros.ps1` archives both exact production sources and overlays the
same candidate benchmark-only measurement subtree on each. Raw archives remain
untouched. It records the overlay, commands, toolchain, published files and actual
loaded assembly hashes, then runs five fresh-process pairs in alternating order.
The five samples inside each process are not five independent process pairs.
Collector, writer and provider results have separate scopes. In-memory writer
throughput is not network throughput; short CPU measurements can be below the
process-counter resolution.

`run-owned-queue.ps1` uses a uniquely labelled, loopback-only PostgreSQL 16.15
fixture pinned by digest. It enters only that container's network namespace to
apply 0/2/10 ms synthetic PostgreSQL egress delay, verifies traffic through the
qdisc, and retains actual SQL round-trip samples. The driver publishes into fresh
owned databases, waits within the warmup deadline for its read-only observation
relations, reverses batch order between repetitions and validates raw JSON types,
exact event/envelope IDs, sequence, completion, settlement and empty final queues.
Missing relations alone are polled; SQL/authentication and later sampling failures
remain errors. Handler delays are simulated work, not database latency. SQL-observed
settlement latency is an upper bound at the sampling cadence.

These synthetic handlers do not debit the application ledger, exercise Redis
depletion notifications or prove durable application follow-on publication.
Existing real financial suites and Gateway closure must remain green before any
production batch change. The fixture is not a production topology or a forecast.

## Hardware and remaining gates

Windows x64 focused tests and micros are development diagnostics. Linux ARM64
measurements require the native ARM64 workflow runner or another controlled
native ARM64 machine; emulation is not accepted. Neither hosted architecture
establishes production kernel/network behavior, native AOT ARM64 operations,
controlled host-failure behavior or the unresolved phase-2 production inventory.
Do not close those gates using this experiment's results.

## Local verification before native collection

The combined Main selection passed 220 tests with one pre-existing skipped
concurrent-collector case. The collector remains owned by one sequential stream;
this change does not add concurrent-recording support. Reflection-disabled Groq
serialization passed 12 cases. Queue contracts and raw-report rejection checks
passed separately, and the normal benchmark build had no warnings or errors.

Five fresh Windows x64 process pairs (five inner samples per case) measured the
writer at 10,000 frames. Median allocation per frame was approximately
1,393 -> 769 bytes for 32-character payloads and 6,353 -> 2,753 bytes for
1,024-character payloads, in both standard and enhanced modes. Wire digests,
event/byte counters, exact flush counts and one DONE frame matched. These are
writer-only workstation-GC prototype diagnostics, not evidence of adoption. Both binaries already contained the
optimized collector, and other uncalled Gateway code differed; they are not an
exact whole-commit native comparison or network/financial qualification.

Consult [phase 3](https://github.com/nickna/Conduit/issues/1767) for current native
results, exact measured revisions, per-experiment decisions and follow-ups. The
workflow retains individual runs; collection success alone does not accept a
performance tradeoff. No overall percentage gain is promised.
