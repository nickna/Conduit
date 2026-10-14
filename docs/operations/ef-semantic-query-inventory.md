# Compiler inventory of EF query callsites

`scripts/aot/collect-ef-query-inventory.ps1` runs the developer-only
`tools/ConduitLLM.EfQueryInventory` analyzer against the actual Admin and Gateway
project graphs. It inventories resolved compiler operations and their symbols.
It does not establish the number of SQL queries, runtime query shapes, complete
repository-suite execution, EF NativeAOT support, or completion of #1373.

## Collecting evidence

Run from a PowerShell 7 session with the repository's .NET SDK and Git available:

```powershell
pwsh -NoProfile -File scripts/aot/collect-ef-query-inventory.ps1 `
  -SourceRevision HEAD `
  -ArtifactDirectory artifacts/ef-query-inventory/my-fresh-capture `
  -CommandTimeoutSeconds 300
```

For a new collection, the output directory must not already exist. The collector keeps it private to
the current user, retains all source copies and captures, and performs no cleanup
of other directories. A restricted host may need permission to set that private
directory's ACL. No service or database starts, and no database connection is
needed. Command output stays in private files; the console prints stage and
completion metadata.

The default run compiles four separate source copies, sequentially: normal
Admin, normal Gateway, native-profile Admin, and native-profile Gateway. Each is
extracted from one retained `git archive` of the requested commit. Uncommitted
service edits are excluded. Tool source is copied separately before its build,
so an uncommitted developer analyzer can inspect a fixed committed service tree.
Every copied tool source, analyzer DLL, injection target, source archive, commit,
and Git tree has an identity in the receipt.

The normal profile runs `dotnet build -c Release -p:PublishAot=false`. The native
profile runs `dotnet build -c Release -p:PublishAot=true --runtime win-x64` on
Windows, or `linux-x64` on Linux. `-NativeRuntime` selects either supported RID.
The latter profile uses the repository's actual `PublishAot` conditionals and
`CONDUIT_NATIVE_AOT` symbol; it is a managed compiler pass and does not run ILC or
publish a native executable. The collector does not substitute the analyzer-only
`ConduitAotAudit` flag, enable EF tooling, or add EF Tasks to an owning project.

For a bounded first increment, select one graph:

```powershell
pwsh -NoProfile -File scripts/aot/collect-ef-query-inventory.ps1 `
  -Profiles normal -Services admin `
  -ArtifactDirectory artifacts/ef-query-inventory/admin-release
```

`-AnalyzerAssemblyPath` skips the tool build, copies the supplied DLL into the
owned capture, and records the original reference and copied DLL hashes;
its source identity is explicitly `UNKNOWN`. Prefer the default source-backed
tool build for evidence intended for review.

`-Resume -ArtifactDirectory <prior-capture>` can repeat reconciliation and
continue the remaining fresh graphs after a collector reconciliation failure,
or reconcile a completed capture with an updated collector.
It preserves the previous receipt, verifies the joined capture seals before any
child command, checks immutable input hashes, and reuses only
a successful joined compiler invocation with identical arguments. A failed
compiler build requires a new capture directory. Requested profiles, services,
and native RID must match the original request.
Captures made before joined seals existed cannot be resumed as qualified
evidence; they require a new collection.

## Records and integrity checks

The collector imports only `Invoke-BoundedProcess` and `Protect-PrivatePath` from
the pinned `measure-native-container.ps1` helper, whose SHA-256 is
`c55d7c2750f2c5e6299eb35c659983b953c3c0c99c25d7f07b3508b59bf75501`.
Its sole derivation sets `ProcessStartInfo.WorkingDirectory` to the explicit
command source directory. The helper retains its UTF-8 handling, per-stream
4 Mi-character output limit, shared deadline, process-tree termination, and
bounded cleanup. The original and derived helper hashes are recorded.

An isolated `CustomAfterMicrosoftCommonTargets` import adds the analyzer and
compiler-visible project identity. No application project references the tool,
and no default build imports it. Before `CoreCompile`, the import records actual
SDK, configuration, RID, `PublishAot`, defines, source files, the actual Csc
`ReferencePathWithRefAssemblies` inputs,
and project references. Each project has its own original SARIF and input receipt.

The analyzer emits informational diagnostics:

| ID | Meaning |
|---|---|
| `CEQ0000` | One compilation sentinel, including syntax-tree checksums, defines, and reference identities |
| `CEQ0001` | A resolved callsite with exact target, original and reduced method symbols, assembly, caller, span, source checksum, root evidence, and expression dependencies |
| `CEQ0002` | Query-shaped names in inactive preprocessor text; lexical evidence only |

The collector rejects `AD0001`, unsuppressed compiler errors, failed builds,
missing original SARIF, missing or repeated compilation sentinels, missing
referenced C# projects, project or define mismatches, and missing or changed
compiler syntax trees. It preserves diagnostics already suppressed in source in
the original SARIF and records their identities. It adds no warning suppressions.
Source files and reference assemblies also have file hashes. Tracked compiler
sources must match the retained Git archive entry hashes. Generator-created
trees are tagged and retain their compiler checksums; some exist only in the
compiler and do not have a physical source file.

Immediately after a successful compiler child joins, the collector seals the
original SARIF, compiler input receipts, source and reference files, and command
stdout, stderr, and invocation receipt hashes. It records the seal's hash before
reconciling diagnostics. Resume checks those original hashes; removing a callsite
diagnostic, changing a reference DLL, or editing a command stream cannot be
relabeled as original successful evidence.

`inventory.json` is the inspectable aggregate. `graphs/<profile>-<service>/inventory.json`
contains a reconciled graph. Per-project `compiler-inputs.b64` and
`diagnostics.sarif` remain beside it. `commands/<stage>/` retains stdout, stderr,
arguments, working directory, exit code, elapsed time, and process-join receipts.
The versioned JSON shape is described by
`scripts/aot/ef-query-inventory.schema.json`.

## Reading the result

`status=captured` means the requested compiler graphs reconciled. A per-graph
`complete=true` has that same limited meaning. The top-level runtime reachability
remains `UNKNOWN`, and complete runtime query scope, native publication, and
native repository-suite qualification remain false. A failed collection retains
its stage and private original evidence and exits unsuccessfully.

Classification distinguishes actual EF terminals, query operators, mutations,
raw SQL, provider functions, direct ADO.NET calls, and negative lookalikes such as
in-memory enumeration or same-named application helpers. Callsite records keep
local query-root evidence; `unknown` dependencies for parameters, helper returns,
generic closures, expressions, callbacks, and callers need separate analysis.
The collector does not infer runtime reachability from a method name or caller.
The classifier covers recognized resolved invocation operations. Implicit
`foreach` and `await foreach` enumeration, unlisted sinks such as
`Enumerable.ToLookup`, and future or unlisted EF API names remain outside it.
`GetDbConnection` access itself is omitted; recognized `DbCommand`
`ExecuteReader`, `ExecuteScalar`, and `ExecuteNonQuery` calls are recorded as ADO
execution. An unknown root on an `ef.sync-enumeration` record also remains
unknown; that category does not prove EF execution.

The conditional exclusion ledger also lists matching lexical names in C# files
under compiled project directories that were absent from that compilation.
Those file matches can include comments or strings. Neither those matches nor
inactive preprocessor observations have bound symbols, and neither contributes
to resolved-callsite counts. Unreferenced project directories, runtime-created
expressions, dynamically loaded code, and arbitrary callback targets remain
outside the proved compiler scope.

Comparisons across profiles should use source-relative project and file identity,
span, exact symbol, and classification. Shared projects appear in both service
graphs, so adding all graph counts double-counts those compiler callsites.
Preserve the original receipts when deduplicating. A later runtime query
qualification still needs an agreed query-shape scope and executable per-case
PostgreSQL evidence; this inventory supplies source evidence for that work.

## Recorded local compilation proof

The 2026-10-12 capture inspected commit
`b97a51e3119ce2cf6cf830c0822e8608bf4a63d3`, Git tree
`76d3eaf1ecb81ee73eaf0609847c04204f5f56d4`, with SDK 10.0.401.
All nine bounded commands joined successfully. Both native profiles used
`PublishAot=true` and `win-x64`. The final aggregate passed the versioned JSON
schema validation and contains four joined capture seals.

| Profile | Service graph | Compilations | Compiler source inputs | Recognized callsites | Excluded lexical observations |
|---|---|---:|---:|---:|---:|
| Release | Admin | 12 | 1,265 | 2,386 | 20 |
| Release | Gateway | 14 | 1,336 | 1,916 | 20 |
| PublishAot compile | Admin | 12 | 1,344 | 2,283 | 27 |
| PublishAot compile | Gateway | 14 | 1,415 | 1,813 | 27 |

These are compiler records under the supported classifier policy. They include
negative lookalikes and shared-project duplicates across service graphs.
The native-profile columns do not describe native execution or native SQL query
qualification.

The developer [semantic contracts](../../Tests/ConduitLLM.EfQueryInventoryTests/README.md)
passed 34 cases with zero failures. The separate
[resume integrity contracts](../../Tests/ConduitLLM.EfQueryInventoryTests/ResumeContracts.ps1)
passed seven cases, including removed callsite diagnostics, altered command
stdout, reference DLL and compiler-input corruption, and missing or changed
seals. These are compiler and collector contracts, not repository-suite runtime
cases. The largest service graph compiled 14 projects; that number is separate
from the 34 and seven contract cases.

The source and artifact hashes below identify different evidence roles. Original
private files remain in the ignored local capture and test output directories.

| Evidence role | SHA-256 |
|---|---|
| Source Git archive | `695fa0988853df5446a3b4ea81e2a1b5cf5b8ca275a177051452bb9cb5005a4c` |
| Analyzer DLL actually loaded | `4846153913d7578b6d605a0d49731caec84e62e9f4a4ade2250cc8f3ff0e808c` |
| Actual compiler injection target | `f1c9debb4365f36d53f2b9a6fcdc7f2feda056a62d12f65bd3a172a94bd2b6d7` |
| Collector that produced the capture | `0b6dceee51bc190e486d0999227e505c350c67ae003c8708953b7e97f42e7d2c` |
| Final local aggregate | `ff3737ead3701a45234232939da7aa6410d27fec689b2f567cd4c79d0015be4b` |
| 34-case semantic contract receipt | `eda41e1c034501cea74f02d28925ef38cd8ffa475ea60fbe2590d9792f221779` |
| Seven-case resume contract receipt | `ba9c7eec1fc530fb5794a58e5240bdc6315f6deb3ee03a88f0b0fdc0665c1e1a` |
| Independent retained-file replay | `703d09c4243bbe6c4f6f9f00f3930439f30e2017d59ff9ce1732e365ed5308df` |

Independent replay verified all four graphs and 52 compilations, original SARIF
rows, source and reference bindings, exact assembly identities, and capture
seals. Git archive text has CRLF under the local Git setting; its comparison with
canonical Git blobs permits only exact UTF-8 CRLF-to-LF conversion. Compiler
checksums bind the raw files actually compiled. Native service roots have the
requested RID, while referenced class libraries may legitimately have an empty
RID. The discovery method has four terminals in each JIT graph and is excluded
from each native graph, with lexical exclusion evidence retained.

The aggregate retains `runtimeReachability=UNKNOWN`,
`completeRuntimeQueryScope=false`, `nativePublication=false`, and
`nativeRepositorySuiteQualified=false`. This result does not close #1373.

Earlier failed or incomplete collector attempts remain retained and do not
qualify this final collection.
