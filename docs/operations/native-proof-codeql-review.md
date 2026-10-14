# Native proof CodeQL review

This review follows issues [1658](https://github.com/nickna/Conduit/issues/1658),
[1659](https://github.com/nickna/Conduit/issues/1659) and
[1657](https://github.com/nickna/Conduit/issues/1657). Their historical comparison
is the 163 open-alert IDs at `ce6d8ec5839d1180b1a324fcfeef43f50081df77`, rather
than the 166-result check summary. Alert inventories retain reported locations;
later edits can move those lines. An alert is a source-review input, not proof of
a production defect. No query suppression or alert dismissal is used.

## Issue 1658: exact metric counters

[The 25-site inventory](codeql/issue-1658.csv) covers only native metric and
BillingAudit lifecycle counters. `TryReadNonnegativeInteger` retains exact sample
keys, duplicate detection and finite parsing, then rejects negative, fractional
or oversized samples before checked conversion to `long`. The ceiling is
9,007,199,254,740,991, the largest consecutive integer in the binary64 sample
domain. Nonnegative fractional values exceed their truncation, so validation
needs no floating-point equality.

Lifecycle queued/flushed/depth/failure comparisons, active-model counts, queue
depth and histogram observation counts use integers. Missing lifecycle samples
still mean an unregistered zero. Every original lifecycle conservation assertion
is retained; cost and duration tolerances remain `1e-9`. No intentional floating
equality remains in the two scoped files.

Focused verification runs `NativeMetricSamplesTests`,
`BillingAuditRuntimeServiceTests` and `BatchAuditShutdownMetricsTests`. Metric
contracts cover integral decimal/exponent notation, the exact-domain ceiling,
fractional/negative/malformed/nonfinite/oversized values, duplicate and missing
samples, fractional aggregate counters, and real Prometheus export. The Release
run passed all 55 selected cases with zero failures/skips; the Release probe
build also passed. Original TRX evidence is retained in
`artifacts/issues/1658/managed/issue-1658.trx`.

The fresh Linux native Admin, Gateway and probe publications and full
`gateway-native-parity.ps1` run passed on October 13, 2026 (America/Los_Angeles),
using SDK 10.0.401 / runtime and ILC 10.0.12. The linker audit reported zero
first-party diagnostics. Both actual Gateway processes passed the quiet timer,
drained workload wave, fresh prepared marker, single pending tail, exclusive
graceful final flush and independent six-event SQL audit oracle. Final accounting
and all owned fixture cleanups passed. The source archive digest is
`04debfbf132ad01f473c335051e935ade185e1d08ae93e6de6bb3fb3802dbceb`, binding
base `ba951c9b7c2306a6268334ef5ad66aeaa5b51320` plus the retained patch; all three
changed code-file hashes were independently matched to the working tree.
Original manifests, logs, executables and source hashes are retained under
`artifacts/issues/1658`. This is a local frozen-source execution, not hosted CI
or production qualification.

## Issue 1659: fixture control flow and redundant locals

[The 24-site inventory](codeql/issue-1659.csv) records six indentation sites,
nine redundant assignments, two upcasts and seven LINQ suggestions. All six
ambiguous scopes now have explicit braces and line breaks. The Gateway/route
preflight and agentic matrices, foreign-key access checks, all 36 discovery
filter pairs and both adapters remain intact. The Redis cleanup Cartesian
product retains both scopes, four limits and both base/sum keys. SSE JSON delta
closure remains unconditional, including the final empty delta.

Eight unread connection bindings are now resource expressions in the same
`await using` blocks. Acquisition, occupied-pool cancellation, pending-task
drain, awaited disposal and following recovery assertions retain their order.
The Admin diagnostic stage initializes directly to its first observable value.
Both Moq null-return setups use the named `value` argument to select the value
overload without redundant upcasts.

Five deferred LINQ rewrites preserve enumeration order and the exact selected
values. Two suggestions are intentionally retained: the benchmark JSON loop
performs one `TryGetProperty` lookup for each of two names, while the chunk-size
hex parser checks the first invalid character and byte limit incrementally.
Projection/filter rewrites would obscure these checks or repeat lookups. No
awaited operation moves into LINQ and no request/test row is removed. Literal
`Path.Combine` notes and the production billing complexity note remain outside
this issue's scope.

Release focused verification passed 121 unit cases (runtime execution store,
dependency routes and HTTP request reader) and 140 relational/fixture cases
(discovery, execution/configuration/pricing/BillingAudit persistence, pool
warmup, converted values, Redis ownership cleanup and benchmark workload), all
with zero failures/skips. Original results are retained in
`artifacts/issues/1659/managed/issue-1659-unit.trx` and
`issue-1659-integration.trx`. An independent complete-diff review confirmed every
matrix, lease scope, request budget, generated-ID check and financial oracle.

Fresh Linux native Gateway, persistence and converter probes also passed. The
full two-host Gateway gate retained every supported protocol/function matrix,
settled accounting and graceful BillingAudit tail; the persistence probe
retained SQL/pool cancellation, generated IDs, ordering and owned-schema cleanup.
The converter executable verified all 31 properties, 126 PostgreSQL checks and
two dictionary comparers, with its owned schema removed and zero first-party
linker warnings. No EF query-execution claim is made by that converter proof.

The frozen current-source archive digest is
`2c2621d297f75526be244e510a168496539c3dd889d55ce2c4064365b8767539`, binding parent
`2946a90a8f02670a84c06bbafe5671e07ac5bd9d` plus its patch. All 20 changed C# file
hashes were independently matched to the working tree. The unchanged Admin and
Gateway executables were reused from issue 1658 only after checking all 2,394
service/build inputs, both executable hashes and their original publication
manifests. Those original manifests were preserved; they do not claim new
service publication. Original current-probe publications, execution evidence,
reuse bindings and cleanup results are retained under `artifacts/issues/1659`.
