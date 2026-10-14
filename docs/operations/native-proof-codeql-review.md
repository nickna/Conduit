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
