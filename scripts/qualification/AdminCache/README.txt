Admin/cache/collector supplemental matched qualification

The standalone entrypoint is run-admin-cache-matrix.ps1. Required arguments:
  -CandidateRevision <40hex> -Architecture X64|Arm64 -OutputDirectory <freshpath>
Optional: -Runs 3..6 (default5), -FunctionalSmoke, -BuildOnly,
  -LegacyDotnet <path>, -CandidateDotnet <path>, -ObserverProject <csprojpath>.
Default execution requires native Linux, matching process architecture, both
SDK10.0.404 and11.0.100 installed, runtime10.0.15/11.0.0, Docker, Git, tar,
PowerShell and Python. No production service image/registry is changed.

Example after promotion (adjust the explicit sibling observer project path):
pwsh -File scripts/qualification/AdminCache/run-admin-cache-matrix.ps1 \
  -CandidateRevision 66c5c3dad682d40d13ed7391b327ea2397553d58 \
  -Architecture X64 -OutputDirectory artifacts/admin-cache-X64 -Runs 5 \
  -ObserverProject scripts/qualification/ObserverV2/Conduit.ServerObserver.csproj

Normal execution archives f68/911/candidate into output/work/{legacy,fixed,candidate}.
It publishes Admin+Migrator, compiles cache/collector wrappers against those
archived Core/Composition ProjectReferences, then publishes one fixed f68.NET10
client, one fixed.NET10 server observer and one fixed.NET10 micro observer.
Private toolchain global.json files select the exact build SDK without editing
archived source SDK declarations. Recipes, all published files, source-tree/
archive hashes and fixed harness inputs are retained. Wrapper bodies link the
byte-identical frozen/Cache.Program.cs and Collector.Program.cs files; compiler
and target framework changes are explicit, not a workload or service optimization.

The default ObserverProject is ../ObserverV2/Conduit.ServerObserver.csproj.
An explicit parameter keeps promotion independent of folder naming. The observer
source is copied to output/work/observer-project before compilation; the original
is not edited. The same observer/client binaries are used for all runtime pairs.

The job owns PostgreSQL17.12/Redis7.4.2 fixtures bound to loopback15477/16477,
three separate databases and RedisDB2/3/4. These ports belong to this separate
job, not main/pressure. It migrates/seeds each fixture with the fixed f68probe
(IpFilter:Enabled=true), performs only setting reads and synthetic populated
cache operations, then verifies full RequestLogs/ledger/group row hashes remain
unchanged. No charge/financial or Wolverine delivery-capacity claim is made.
Cleanup verifies exact task labels before deleting only owned containers/volumes/
network. Published code, source archives and raw data remain for replay.

Each of5run triplets alternates cohort order. Every cohort starts a fresh Admin
process, observes startup/readiness, runs one first authenticated read, then
the frozen admin-setting-read workload (100internalwarmups+1000measured,8clients).
HTTP time ends after complete EOF and strict expected-setting validation.
The steady server-observer window includes ALL1100validated calls plus fixed
client startup/exit and background/observer activity. CPU/allocated bytes/GC are
divided by1100, not1000. This differs from Gateway windows that exclude warmup;
do not compare absolute CPU across services. 100ms peakWS and process lifetime
peak are separate metrics. Post-window forcedGC is retention evidence only.

The actual Admin GC mode/config is recorded by the same fixed micro hook in
metadata-only mode (no second timer). That metadata-only hook also proves each
fixed client actually runs.NET10.0.15 on the requested architecture.

Cache: complete mapping/discovery/pricing graphs; cold distinct-key+immediateL1
and a fresh reader process that forbids all business-loader repairs.100warmups+
1000measured samples/state, nine states total. Loader counts prove logical
miss/fill/hit states, not an invented FusionCache emitted-counter/queue metric.
Reader mutation/ownership re-reads are outside timed intervals but included in
whole-microprocess diagnostics. Each writer and reader has6600totalcachecalls.
Synthetic populated factories are not PostgreSQL I/O or application capacity.

Collector: actual production StreamingMetricsCollector at10/100/1000/10000chunks,
one warmup+3measured streams for each of2modes. Each invocation contains32streams
(24measured+8warm) and88880recorded chunks. The control omits per-chunk snapshots;
it is mechanistic evidence, not deployed optimization. This collector owns a
timestamp List and has no queue/drain API. Existing Wolverine recovery proofs
remain separate from these measurements.

Whole-microprocess CPU/GC/WS/alloc counters include startup, fixture construction,
warmups, validation, ownership rereads, report construction, disposal and observer.
They are normalized by all cachecalls or all32collectorstreams, not timed-row
pure costs. Collector's timed same-thread allocation counter remains separate
from awaited-cache timed process-wide allocation and whole-process counters.

Allocation qualification: retain raw counters; never clamp/filter decreases.
Default.NET10ServerGC/DATAS GC.GetTotalAllocatedBytes is affected by the retired-
heap aggregation bug, including positive undercounts. All such Admin windows
have allocation comparisons unavailable; allocation acceptance stays open.
WorkstationGC is not affected by THIS retired-heap omission, but observed negative
cache samples/timeline steps still invalidate that metric. The synchronous
collector uses GetAllocatedBytesForCurrentThread and verifies unchanged thread.
No GC policy is changed to obtain comparisons. Primary source:
https://github.com/dotnet/runtime/pull/131069

Source-origin caveat: frozen report SourceRevision=f68 and embedded Windows/noARM
text are historical fixture literals deliberately retained. Actual production
cohort/compiler/runtime/native architecture and assembly hashes are attributed
externally. Do not relabel raw frozen reports or treat the literal as current
production provenance.

Outputs:
  configuration.json; source-bindings.json; fixed-harness-bindings.json
  work/{legacy,fixed,candidate} source archives and published role directories
  prepare/ and measure/{bindings.json,manifest.json,*rawreports,*process.json}
  summary.json: strict inventory, Admin runs,9cache states,8collector modes/sizes,
    whole-process diagnostics, per-run summaries and matched-run ratios.
  *-state-before/after/final.json; cleanup.json; published-input-integrity.json
  qualification-state.json (collection only; performanceAcceptanceGranted=false)

Admin working diagnostics match the primary names/formulas:
validatedRatePerSecond>=0.95baseline;
p50/p95/p99Milliseconds<=max(1.05baseline,baseline+2ms);
serverWindowCpuMillisecondsPerValidated<=1.05baseline;
serverWindowAllocatedBytesPerValidated<=1.05baseline when counters qualified;
serverWindowSampledPeakWorkingSetBytes<=1.10baseline.
Report median/range of independent same-run ratios. Default5pairs required;
p95 requires100measured/request samples per run andp99requires1000. These are
working diagnostics, not user-approved SLOs. Startup/first-read and cache/
collector comparisons are descriptive; no new application-capacity thresholds.

-FunctionalSmoke reduces effective runs to1, Admin20+2warmups and cache5+2warmups.
Collector frozen loops stay unchanged. Smoke output cannot accept budgets.
-BuildOnly publishes inputs/config without creating fixtures or timing work.
It supports an external fixture controller for a Docker-container local smoke;
the CI default runs the complete standalone native-VM matrix.

Replay: python summarize.py --directory OUT/measure --configuration
OUT/configuration.json --output OUT/summary.replayed.json
Analyzer checks: python -m unittest discover -s THISDIR -p 'test_*.py' -v
