# Repository native spike: expanded cases and controlled failures

Measured on Windows for `win-x64`, 2026-10-12, using the isolated source described
in the [EF query spike](ef-native-query-spike.md). This experiment adds nine
confirmed repository cases and separates compiler, context-discovery, and query
precompilation failures. It does not complete #1373 or qualify native EF execution.

## Expanded declared plan

The [public additions](ef-native-repository-spike-additions.json) bind nine
`VirtualKeyBalanceTrackingTests` facts to their original source and passed Main
Core TRX identities. Each case retains a fresh instance, direct awaited test call,
the original SQLite fixture and mock context factory, and original `IDisposable`
cleanup. The original 88 Core bindings remain byte-identical; the PG driver and
its 136 cases retain their original plan identity.

The new combined plan is a lower bound of 233 cases: 97 Core (84 SQLite, 3 mock,
10 pure) plus 136 PostgreSQL cases. Twenty broader concrete-construction classes
remain recorded as service/endpoint-mediated, fixture/probe, or mixed/unreviewed
candidates. This is not exhaustive repository-subject discovery. No backend was
silently substituted, and SQLite/mock/pure cases are not described as PostgreSQL
execution.

## Scratch-only compile bridge

The earlier PG publish failed because seven EF reference adapters are excluded
by `CONDUIT_NATIVE_AOT`. Only in the isolated copy, their leading guards became
`#if !CONDUIT_NATIVE_AOT || CONDUIT_EF_QUERY_SPIKE`. A local Configuration project
property appends that symbol when `ConduitEfQuerySpike=true`.

Production native selection, service DI, query filters, global build properties,
and warning policy are unchanged. Exact reverse-delta checks preserve all eight
preimages. Actual MSBuild symbol evaluation retains `CONDUIT_NATIVE_AOT` with
the opt-in both on and off; C# preprocessing exposes seven adapter types on and
zero off. This establishes compile availability only.

Both dispatchers successfully built with `PublishAot=true` and the opt-in symbol:
the PG source graph compiled, and all 97 Core direct bindings compiled. These
commands were **managed builds with native compiler symbols**, not NativeAOT
publication or runtime execution.

## Controlled publish outcomes

All completed commands below were joined. Elapsed wall time comes from their
original bounded invocation receipts, rounded to two decimals.

| Operation | Seconds | Exit | Outcome |
|---|---:|---:|---|
| Unchanged 88-case Core publish, serial and shared compilation off | 138.89 | 1 | Configuration query-precompiler failures; Core/Providers context-discovery failures; MSBuild wrappers; no file-lock diagnostics |
| PG native-symbol compile with bridge | 76.39 | 0 | Managed compile control; earlier missing-type obstruction absent |
| 97-case Core native-symbol compile with bridge | 21.04 | 0 | Managed compile control for all 97 direct bindings |
| Fresh inert startup graph build | 45.29 | 0 | Managed factory DLL/apphost/deps/runtimeconfig inventory bound |
| PG publish with explicit startup DLL | 93.32 | 1 | Our caller input rejected by EF Tasks extension validation before query generation |
| PG publish with corrected startup apphost | 146.14 | 1 | Query-precompiler errors in Configuration, Core, and Providers; earlier adapter, file-lock, startup-validation and context-discovery diagnostics absent |
| 97-case Core publish with corrected startup apphost | 155.02 | 1 | Same Configuration/Core/Providers query-precompiler signatures as corrected PG; no published test executable |

The serial Core run's 520 Configuration error renderings reduce to the same
241 project/token/type/message signatures as the earlier aligned CLI pass.
`MSB4181` messages are wrapper failures, not new failed query signatures.
The absence of `CS2012`/`MSB3026` in this retry does not establish the original
lock's cause or a general file-lock fix.

The corrected PG attempt keeps both model scaffolding and query precompilation
at `publish`; neither stage was disabled to obtain a successful result. Its 522
query-error renderings include 520 Configuration, one Core, and one Providers
record, across 243 signatures. The corrected Core attempt has the same signatures
and rendering counts. Neither suite produced a published native test executable;
no real PostgreSQL/Redis fixture started. All 233 planned cases are
`NOT_EXECUTED`: no native test constructor, body, or cleanup ran.

## Pinned EF Tasks startup contract

For the installed EF Tasks 10.0.12, use these experimental publish arguments:

```powershell
-p:ConduitEfQuerySpike=true
-p:EFStartupAssembly=<fresh-inert-startup>.exe
-p:DbContextType=ConduitLLM.Configuration.ConduitDbContext
-p:EFScaffoldModelStage=publish
-p:EFPrecompileQueriesStage=publish
-p:BuildInParallel=false
-p:UseSharedCompilation=false
-m:1
```

Build the inert executable graph first and hash-bind its apphost, DLL, deps,
runtimeconfig and referenced assemblies. Its ordinary `Main` exits 2 and starts
no service; its design-time factory forwards to `ConfigurationDbContextFactory`.

The package's explicit `StartupAssembly` validator requires lowercase `.exe`.
It then changes the path to the sibling `.dll` for `dotnet exec` of EF tooling
and derives the sibling deps/runtimeconfig paths. Supplying `.dll` directly was
our rejected input, not a native query failure. This is the pinned Windows
experiment's contract, not a claim about other versions or Linux behavior.
The installed documentation/error hint names `EFStartupProject`, while these
targets consume `EFStartupAssembly` and `DbContextType`. See the
[pinned task source](https://raw.githubusercontent.com/dotnet/dotnet/95017c711e6afc1085133d440e42b4bd78155701/src/efcore/src/EFCore.Tasks/Tasks/Internal/OperationTaskBase.cs).
The guidance mismatch is tracked in [#1663](https://github.com/nickna/Conduit/issues/1663).

## Evidence and remaining work

| Receipt | SHA-256 |
|---|---|
| Nine-case source/TRX addendum | `0d5b5e54b4aade2ffadd9e5fe0e6fece629ddb1fc8a7106c8e4f72c18a2996ea` |
| Combined lower-bound plan | `3518791234fa7ef1b0a060cf1eb08d0f12aaf47836db1fbe09c167a604aca1ac` |
| Independent expanded-driver review | `ea6da51e30f368ca7f2b71c2ee749401bb19f1dfad7027d0d1fce163319014ca` |
| Independent compile-bridge review | `9703d8ef8aaf406d458c512ae552ecea5301fe4181f2c5598148575bbb3e37e7` |
| Serial Core publish | `1df6bb8e26b85f25b694117d2077bdba09525720998fda2838707bd30bf5b29a` |
| Serial Core failure triage | `2d09be8d7b20ad7649ae32ea35cf5e9ab5bc3849282097d77eaaf8bc9ad76653` |
| Serial/CLI signature comparison | `97734dc64484c0012076bbaa96c8baac379c209eb79ddf195e9b650cee2f83c8` |
| PG corrected-apphost publish | `e7a8e1844c7a14d8341456f51dedafc2524422793e944500fbdb97d869f3b3ce` |
| Core 97-case corrected-apphost publish | `e886d8f147702ce4270904a2ce62a475ac35c284c55190ee812f7fa235af3e3a` |
| Independent corrected PG failure triage | `523a8e27c6ec0c5a0625e8f62d373812b3d06be8f4d527c0b6eb6e3c798e602b` |
| Independent corrected Core failure triage | `6422e8c1bdfc20404476f6dec08cf7493e3173ee684070198396c1bc930797e9` |
| Closed 233-case native outcome ledger | `0fde9fe71807c8bef076df641e42378d8b88d67e6f62dcb520c3aa5a9029b2fc` |
| Independent outcomes and public-additions review | `23de24dda2a5fe169f47bdbe305ceeda58fc10ad4349af093daf1f68b8158b3d` |

Complete repository-subject discovery and preserve fixture/backend semantics.
Resolve the observed EF query-precompiler blockers or prove a typed replacement
for each required operation, then execute the complete native PostgreSQL suite
and measure its workload. Existing compiled-model soft-delete omissions remain
an explicit parity gap. Production remains JIT under ADR 0006; published native
suite execution and full persistence qualification remain unproven.

## Representative static discovery rewrite

`EfFunctionDiscoveryRuntimeStore.ListEnabledAsync` now uses four complete query
expressions for both optional filters, purpose only, provider only, or neither.
The rewrite preserves enabled filtering, unknown enum values, detached reads,
cancellation, and awaited context disposal without adding an ordering promise.
The existing three-row PostgreSQL parity suite passed without failures or skips,
including all 36 filter combinations and cancellation recovery. The fresh
PostgreSQL 16.15 and Redis fixtures were removed; no test schemas remained.

An isolated Configuration-only comparison pins EF runtime/Design/Tasks/CLI
10.0.12 and Npgsql provider 10.0.3. It uses the `cea2377ad` source plus the
single-method working-tree rewrite; production profile selection is unchanged.
Both managed startup builds passed, while
both query-precompiler commands exited 1. Error renderings changed from 520 to
526: the ambiguous generic `query.ToListAsync` rejection rendered eight times
before and six after, while each of the four new complete expressions produced
two `ParameterSymbol` translation-error renderings. The old token matches other
owners, so its count change alone does not establish a unique source attribution.
No generated binding or native execution was established. Static query shape and
passing managed PostgreSQL parity do not resolve the remaining EF compiler limits.
The similar `ParameterSymbol` failure family is already reported in
[EF #35494](https://github.com/dotnet/efcore/issues/35494); a shared root cause
has not been established for this Conduit occurrence.

The managed parity receipt is
`d6448bf091b1a66de11ddd02f470609edb704400934746307e6a45e6c6df1e2f`;
the before/after comparison receipt is
`65237eaf7dacaed5cbc3ce35a58f90438d5c57f3f933151af37b7b9478c8f2c1`.
The exact-token comparison review is
`25f544a4c975c548365eedc01b6d20faee30273c94c2b9bd5ddc02ffdc66bdc2`.
Independent managed test-identity, source and captured-cleanup verification is
`a11f962da33cdf1e13aa673d4fc16a88c5e838f024418d86c7d9781be435d874`.
