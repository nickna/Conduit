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

## Representative static media ownership rewrite

`MediaRecordRepository.GetByVirtualKeyIdAsync` now has two complete expressions
rooted directly at `context.MediaRecords`. The default branch explicitly checks
`DeletedAt == null`; the other uses `IgnoreQueryFilters`. Both preserve owner
filtering, descending creation time, detached reads, cancellation, awaited context
disposal, and the existing slow-operation logging. The explicit predicate also
protects this read when the experimental model omits its global media filter.

Five SQLite rows and the real PostgreSQL 16.15 media parity fact passed without
failures or skips. They cover both deletion branches, two owners, ordering, empty
results, detached reads, and cancellation recovery. A separate SQLite context
removes the media filter without changing process-wide generation settings;
this is a filterless-model contract test, not published native execution. The
PostgreSQL fact also preserves the EF/typed-Npgsql runtime contract. Independent
replay closed 98 retained originals, exact TRX identities and counters, zero
remaining test schemas, both owned fixture removals, and unchanged base images.

The isolated Configuration-owner comparison uses `5702b54bb` source plus this
single-method rewrite, EF runtime/Design/Tasks/CLI 10.0.12 and Npgsql 10.0.3.
Both managed startup builds passed. Actual compiler records bind the old terminal
to an unknown local query root and both new terminals to the `MediaRecords`
DbSet property. Both EF precompiler commands still exited 1, taking 16.54 and
16.27 seconds. The old complete query token produced two dynamic-query error
renderings; each new branch produced two `ParameterSymbol: virtualKeyId`
translation-error renderings. Total error renderings changed from 526 to 528,
across 245 and 246 token/exception/message signatures. No generated binding,
native executable, database query execution, or native repository-suite
qualification was established by this compiler experiment. This remains the
failure family covered by [EF #35494](https://github.com/dotnet/efcore/issues/35494),
without assuming a common root cause.

The managed PostgreSQL receipt is
`66f55e80862298226e409f07d7a103244fff789c4234ea91253fb1f2447bf05e`;
independent managed replay is
`cd76bff7bc3de46d7974798ae5ebbba03e51b3a9acd75961f947bddfae341eb5`;
the focused compiler comparison is
`2ff2e4f0dbc0271347ed94aa77fde357230824d390568b5e0d3297f940529980`.
Independent compiler replay closed 69 originals, including source-token and
actual SARIF bindings, the exact three-signature delta, and the before Git blob;
its receipt is
`9d5891fd4205f47e2b9b4b91b573c587b04fa5327fae0f0991c61ecb828d574f`.
Setup-only and restricted-environment deadline attempts remain retained and
unqualified; the final isolated commands were joined. Docker's successful stop
output exposed a deprecated cleanup flag, tracked separately in
[#1664](https://github.com/nickna/Conduit/issues/1664).

## Local copies for EF query precompilation

`GetByVirtualKeyIdAsync` now copies the owner ID and query cancellation token to
locals before its two expressions. Context creation still receives the original
token; deletion branches, ordering, disposal and logging are unchanged. The
[pinned EF 10.0.12 translator](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/efcore/src/EFCore.Design/Query/Internal/CSharpToLinqTranslator.cs#L361)
handles locals flowing into queries but rejects method parameters in that path.
The local-copy workaround is also reported in
[EF #35494](https://github.com/dotnet/efcore/issues/35494).

A fresh Configuration-owner comparison starts from `3e39b28c5` and changes only
those local copies and references. Both managed build controls pass. Both EF
precompiler commands still exit 1, but the two targeted `ParameterSymbol`
signatures disappear: four raw renderings before, zero after, with no replacement
failure for either complete query token. Totals change from 528 renderings across
246 signatures to 524 across 244. Every unrelated signature and its rendering
count is unchanged. Actual compiler records bind both expressions in both phases
to EF `ToListAsync<MediaRecord>` and the `MediaRecords` DbSet property.

The corrected comparison receipt is
`802042840053f3e26bc28e4528e67890873d4f50e338b55b269a08f689bcba5d`;
independent source/token/SARIF/log replay is
`f40f7d4d22c770b55f3013cc763fcafb8390169b86782d906205c58acd3cf665`.
It closes all 61 sealed references. The first derived summary incorrectly summed
the parameter-error family as zero before; that receipt remains retained, and
the corrected summary reports four. Original compiler captures were unchanged.
The frozen variant was applied to production source only after this capture.

With those exact variant bytes applied, five existing SQLite rows and the real
PostgreSQL 16.15 media parity fact pass without failures or skips. Independent
replay verifies their source bindings and TRX identities, both deletion branches,
owner isolation, ordering, detached reads, cancellation recovery and the SQLite
filterless model. Both owned fixtures were removed, no test schemas remained,
and protected base images were unchanged. The PostgreSQL receipt is
`1e1f790ee9527af72319b0e56f5f41dfc21102ffc82a4a77b2d4c221afe117c1`;
independent behavior replay is
`a9432f61a8edd85580680b0222dcec7d2d0138d053c86eaf242a3efaead4742d`.

No generated files or native test process were produced, and the compiler
experiment executed no database queries. Removing these targeted translation
errors does not prove generated-query execution, complete repository-suite
qualification, or production readiness. The remaining owner failures still
require fixes or verified replacements; production remains JIT under ADR 0006.

## Guarded scalar copies for discovery queries

`ListEnabledAsync` copies nonnullable purpose/provider values inside its original
`HasValue` guards and uses a local query cancellation token in all four complete
expressions. Context creation still receives the original token. Enabled
filtering, optional and unknown enum values, no tracking, awaited disposal, the
public signature, and `GetEnabledByIdAsync` are unchanged.

A fresh Configuration-owner comparison starts from `87218b656` and pins EF
runtime/Design/Tasks/CLI 10.0.12 with Npgsql provider 10.0.3. Both managed build
controls pass. Both query-precompiler commands still exit 1, but all eight focused
error renderings disappear, with no replacement failure for any of the four
query token sequences. Totals change from 524 renderings across 244 signatures
to 516 across 240; every unrelated signature and rendering count is unchanged.
Actual compiler records bind each expression in both phases to EF
`ToListAsync<FunctionConfiguration>` and the `FunctionConfigurations` DbSet
property. Independent replay verifies all 61 sealed references, original logs,
source checksums, token sequences, and SARIF bindings.

A nullable-copy candidate was measured and retained without applying it. It
removes the eight parameter-error renderings but introduces six replacements:
each of the three filtered expressions fails at `Nullable.Value` with
"Nullable object must have a value." Its totals are 522/243. The applied variant
evaluates `.Value` outside each query under the original guard, then captures
the nonnullable local. This is an observed compiler workaround, not evidence of
generated native query execution.

The scalar comparison receipt is
`ed2f6e0004f1b6d2dad0c265b26532af35187602e1af7d91867d614e81e1348e`;
independent compiler replay is
`7192ed94c3364a9608bb9350ee6e14ae30f10b2d07ff5191165d542a15a8a0a5`.
The unapplied nullable comparison is
`bfa470040cfab5bf6d9b22466f542ebcccc55cf48690915a00e11ebbe5ec9ad8`;
its inner-exception evidence is
`3ac969a72cb00ad760162d95987d6c72267a6668f0ddd94499fbc9605fae59fc`.

The exact scalar variant passes five real PostgreSQL 16.15 parity rows without
failures or skips. The existing fact checks all 36 optional/known/unknown enum
filter combinations, persisted scalars, enabled filtering, and detached reads.
The cancellation theory now covers both list and ID reads in both EF and typed
Npgsql. It observes an actual PostgreSQL lock wait, cancels the read, and verifies
both ID and list recovery with a single connection slot. Pre-cancelled list
reads remain covered. All 21 frozen source/build/test/fixture/helper inputs and
their byte copies are unchanged at four check stages. Both owned PostgreSQL and
Redis fixtures were removed and verified absent by ID and name; no test schemas
remained, and protected base image references were unchanged.

The managed PostgreSQL receipt is
`b800927cfcb9a14c7b4e911dfec28b4eafbc8033611a4e49a2cabf590f0e0b6c`;
the original five-row TRX is
`643f6a7d8bcf01c2fa73bf4c38d71b0590447d10cc49941c30daa786c75d5c71`.
Independent source, TRX-identity, raw command, ownership, and cleanup replay is
`df98d7983da0d32387637818226dac8a45b843dc9e992fb6aaed5f04fabcbe13`;
it verifies 119 retained references and all 34 joined commands.

No generated files or native process were produced by either compiler
comparison, and neither executed database queries. The remaining owner failures,
complete native repository-suite execution, and production qualification remain
unresolved. Production remains JIT under ADR 0006.

## Local copies for discovery ID reads

`GetEnabledByIdAsync` copies its ID and query cancellation token to locals after
context creation, which still receives the original token. Its enabled predicate,
no tracking, awaited disposal, public signature, and the four list branches are
unchanged.

The isolated Configuration-owner comparison starts from `5a4a1d99d` with EF
runtime/Design/Tasks/CLI 10.0.12 and Npgsql provider 10.0.3. Both managed build
controls pass. Both query-precompiler commands still exit 1, but the exact ID
query token's two `ParameterSymbol: id` error renderings disappear, with no
replacement diagnostic. Totals change from 516 renderings across 240 signatures
to 514 across 239; every unrelated signature and rendering count is unchanged.
Actual compiler records bind the terminal in both phases to EF's predicate and
cancellation-token `FirstOrDefaultAsync<FunctionConfiguration>` overload and the
`FunctionConfigurations` DbSet property. The comparison seals 70 retained
references and produces no generated files or native execution.

The measured comparison receipt is
`4ef11e8c061714b31d414d8b1c06a032f25b40a0b4aebdc7be601120202be442`.
Independent original-source/token/compiler/log replay is
`55dd65f8ad38656846bb5702e303ebad39f617471e153fb3550fd1e5443e8acd`.
The first setup stopped before any EF command because archive line endings
differed. Its raw archive and failure remain retained and unqualified. The fresh
capture preserves the raw archived target and verifies exact canonical byte
equality before normalizing only that target in its isolated source tree.

The exact applied variant passes all five existing real PostgreSQL 16.15 parity
rows without failures or skips. ID comparisons cover enabled, disabled, absent,
and unknown-enum records, populated and null scalars, and detached reads. Both
EF and typed Npgsql cancellation rows observe a lock-blocked ID read, cancel it,
and verify ID and list recovery with a single connection slot. The list-filter
and list-cancellation cases also pass. All 21 frozen inputs and byte copies are
unchanged at four stages. Both owned fixtures were removed and verified absent
by ID and name; test schemas are absent and protected image references unchanged.

The managed PostgreSQL receipt is
`c97dc30c86a98e9bdc4cea5cb5bcdae5cbcf49d97ecdbc57ad52af763851742c`;
the original five-row TRX is
`248019c970f502168f8c47c0a15630f7b57fe223de3f80dc52fdb6b3b6a4089c`.
Independent source, TRX-identity, raw-command, fixture-ownership, and cleanup
replay is
`da9a4fb2ee86a1f3445c6a28226a7c93026315aa860847baeaf6be2d4dda222b`;
it verifies all 34 joined commands and their 68 captured output streams.

Removing this translation failure does not establish generated-query execution,
complete native repository-suite qualification, or production readiness. The
remaining owner failures still require fixes or verified replacements;
production remains JIT under ADR 0006.

## Local copies for pre-execution configuration reads

`EfFunctionExecutionConfigurationStore` copies query values and cancellation
tokens to locals in its three reads. Single-ID lookup still includes disabled
configurations. Batch lookup preserves the original null/empty fast return,
including an already-cancelled token, and captures the original collection
reference. Credential lookup retains disabled rows and the legacy primary/name
ordering. Public signatures, no tracking, awaited disposal, native guards, and
original-token context creation are unchanged.

A fresh Configuration-owner comparison starts from `8915a7a88` with EF
runtime/Design/Tasks/CLI 10.0.12 and Npgsql provider 10.0.3. Both managed build
controls pass. Both precompiler commands still exit 1, but all six focused
parameter-error renderings disappear without replacements: totals change from
514 renderings across 239 signatures to 508 across 236. Every unrelated
signature and rendering count is unchanged. Actual compiler records bind the
three terminals in both phases to EF's `FirstOrDefaultAsync`/`ToListAsync`
overloads and their `FunctionConfigurations`/`FunctionCredentials` DbSet roots.
The comparison seals 139 retained references and produces no generated files or
native execution. Its receipt is
`9ca5b9a7b46785b29af10c106a5732c8fcc9cb87df56f28b0ca104456493025a`.
Independent original-log/source/token/SARIF/compiler-input replay is
`089c75a3821ee67cc5c9262d01f48296b035d98c2aa6728cd413fdfffaa019b4`.

The exact applied variant passes all four existing real PostgreSQL 16.15 parity
rows without failures or skips. Configuration reads preserve known/missing IDs,
null/empty/duplicate ID lists, disabled rows, unknown enums, scalar fidelity,
and detached reads. Credential reads preserve strict legacy ordering, scope
selection, opaque secrets, null fields, disabled rows, and detached reads.
Pre-cancelled reads and null/empty fast returns pass; both credential pool-wait
cancellation rows recover with a single connection slot. All 26 frozen inputs
and byte copies are unchanged at four check stages. Both owned fixtures were
removed and verified absent by ID and name, no test schemas remained, and
protected image references were unchanged. No SQLite owner parity is claimed.

The managed PostgreSQL receipt is
`27afcba5b66d7ab1258bb70bf9317a313f739a673dd9f959551f1aa0a5b46317`;
the original four-row TRX is
`f57f9bfeae5f636794c37dc4162263239efb77536b107075c9420e63be53c9cd`.
Independent source, TRX-identity, raw-command, ownership, and cleanup replay is
`03eef96022e2b287b9bfbc1e2410a6d1bb4cefc016cd755e287f0f57ff8827b1`;
it verifies all 34 joined commands and their 68 captured output streams.

That four-row run cancels before execution and during pool acquisition. The
[#1665](https://github.com/nickna/Conduit/issues/1665) additions provide six
observed SQL-in-flight cases and three typed persistence-driver counterparts,
reconciled in the [current query inventory](persistence-aot-query-inventory.md).
They do not alter the historical four-row receipt or establish EF published-native
execution; no current cancellation failure has been demonstrated.

Removing these translation errors does not establish generated-query execution,
complete native repository-suite qualification, or production readiness.
Remaining owner failures still require fixes or verified replacements, and
production remains JIT under ADR 0006.

## Local copies for execution readers

Both methods in `EfFunctionExecutionRuntimeReader` copy the execution ID/key and
query cancellation token to locals after context creation, which still receives
the original token. Both configuration includes, no tracking, unbounded key
filtering, requested-time ordering, awaited disposal, public signatures, and the
native guard are unchanged.

The fresh Configuration-owner comparison starts from `13f1a6cd8` with EF
runtime/Design/Tasks/CLI 10.0.12 and Npgsql provider 10.0.3. Both managed build
controls pass. Both precompiler commands still exit 1, but the reader's exact
`id` and `virtualKeyId` parameter-error signatures disappear: four renderings
before, zero after, with no replacements. Totals change from 506 renderings
across 235 signatures to 502 across 233; every unrelated signature and rendering
count is unchanged. Actual compiler records bind both terminals and all eight
callsites in both phases to the direct `FunctionExecutions` root. Both EF
`Include` calls retain their `FunctionConfiguration` navigation and type binding.
These compiler callsite counts do not describe executed SQL statements.

The comparison seals 302 retained references. Its receipt is
`cad1ada89df52429d080854b0b897b1603c16c78491b77bf79abc63d22c0a3a4`.
Independent original-log/source/token/SARIF/compiler-input replay is
`91b85cf2350914304facadfe4f5ed041106499469bf01d0c322275a1c5c11d21`.
An intermediate scope receipt was overwritten while adding explicit include
checks; its earlier bytes were not retained. This loss is recorded explicitly;
the immutable final scope copy, frozen source, and fresh actual compiler evidence
establish the result. The old preparation receipt is not treated as replayable
evidence. The compiler comparison produced no generated files or native process
and executed no database queries.

The exact applied variant passes the complete fourteen-case real PostgreSQL
16.15 execution persistence class without failures or skips: six reader cases
and eight coupled writer cases. Reader cases cover all scalars, JSON/null/unknown
enum/UTC fidelity, included graphs and inverse identity, 105 unbounded ordered
rows, key isolation, detached mutation, pre-cancellation, and observed SQL/pool
cancellation with single-slot recovery. The writer cases retain graph rejection,
read-to-write mutation isolation, and existing writer contracts. All 33 frozen
inputs and byte copies are unchanged at four stages. Both owned fixtures were
removed and verified absent by ID and name, no execution schemas remained, and
protected image references were unchanged.

The managed PostgreSQL receipt is
`58f92be0149371d39c8790216f082c7edcb1fb87cbc1ecd72a20912c05a8a239`;
the original fourteen-row TRX is
`c2ebbe8329752b64f0165e22657c406caeaee0967772fc500ebd372cc5320c12`.
Independent source, TRX-identity, raw-command, ownership, and cleanup replay is
`741588958fd29cbc9ed57f1c0918f3316954ce975775c0552e4b9f86076d8e97`;
it verifies all 34 joined commands and their 68 captured output streams.

Removing these translation errors does not establish generated-query execution,
complete native repository-suite qualification, or production readiness.
Remaining owner failures still require fixes or verified replacements, and
production remains JIT under ADR 0006.

## Complete virtual-key queries with scalar snapshots

All five public methods in `EfVirtualKeyRuntimeStore` now use local scalar query
inputs. The two key reads project the same 18 key and 14 group fields into flat
anonymous snapshots, then construct the original DTO graph after the awaited
read. The three balance-state reads are complete expressions in their original
duplicate, applied-update and unique-violation recovery contexts; their applied
flags remain false, true and false. Context creation still receives the original
token at each original boundary. Validation, detached reads, cardinality, key-ID
ordering, execution strategy, transaction/rollback, idempotency validation,
ledger creation, context disposal, and timestamp evaluation points are preserved.
Both timestamp setters use row-value lambdas, and both decimal comparisons use
explicit `0m` without changing signed-zero balance arithmetic.

A first local-copy candidate was measured and rejected without applying it.
It removed the original eight signatures but introduced four replacements:
required-member DTO initialization failures in both reads, a decimal literal
translation failure in the balance update, and a scalar timestamp funcletizer
failure in touch. Totals changed from 502/233 to 494/229 renderings/signatures,
leaving eight focused renderings. Inspection of pinned EF 10.0.12 source at
`95017c711e6afc1085133d440e42b4bd78155701` informed the accepted scalar-snapshot,
decimal-literal and timestamp-lambda variant. The source mechanisms and observed
stacks are retained; the exact null-state cause in the funcletizer was inferred,
not instrumented. The timestamp failure family remains tracked in
[#1660](https://github.com/nickna/Conduit/issues/1660); a focused projection/decimal
compiler reduction is an open follow-up in
[#1667](https://github.com/nickna/Conduit/issues/1667).

The accepted fresh Configuration-owner comparison starts from `7f887caca` with
EF runtime/Design/Tasks/CLI 10.0.12 and Npgsql provider 10.0.3. Both managed build
controls pass. Both precompiler commands still exit 1, but all 16 focused error
renderings across eight signatures disappear, with no added signature or shared
rendering-count change. Totals change from 502 renderings across 233 signatures
to 486 across 225; every unrelated signature and rendering count is unchanged.
Actual source/token/span/SARIF/checksum and type bindings cover all 29 before and
37 after owner callsites, eight before and ten after terminal/update targets,
both complete 32-field projections, all 22 scalar alias evaluation points and
all three complete state reads. Global inventory counts change from 1,253 to
1,261. These are compiler callsite counts, not executed SQL statements.

The comparison seals 582 references, including the rejected first candidate;
all 432 sealed references from the rejected comparison are unchanged. The mutable focused working source
is excluded from that immutable set and guarded separately; the original before
source and accepted variant remain retained. The comparison receipt is
`0da546f683e01c073c34fc9390bb630a05a0be26d09bb0f75cce8f54a754ddea`;
independent original-source/token/SARIF/compiler-input/log replay is
`3915448d2d1963648c25337cebafeb60f19adad501343bc6c041afca1fcc46fb`.
The source-application receipt is
`1885de46db9f33c3e4ea3aba1e6e94bc185eaa29f80b5d5a50a8b3c7656a1480`.
The compiler experiment produced no generated files or native process and
executed no database queries.

The exact applied variant passes all 19 selected real PostgreSQL 16.15 cases
without failures or skips: five virtual-key runtime parity rows, eleven legacy
ledger replay rows and three financial precision rows. Sixteen rows exercise
runtime persistence contracts; three exercise only the recovery script. The
new runtime cases cover complete enabled/disabled and optional-null snapshots,
all key/group fields, detached row-version mutation, group and ordered-hash
isolation, caller-supplied touch timestamps, misses, and touch without a ledger
entry. Both EF and typed Npgsql cancel all five operations before execution,
during occupied single-slot pool acquisition, and during observed PostgreSQL
lock waits, then recover using the same store and pool. The balance cancellation
case reaches the held ledger-insertion lock after the balance update and verifies
rollback of the complete group snapshot and ledger count. Existing exact decimal,
concurrent replay, identity-conflict, approval and migration contracts pass
without rewriting the ledger or financial test classes. These cases do not force
every execution-strategy retry or unique-violation catch branch; that explicit
coverage gap remains a non-blocking follow-up in
[#1666](https://github.com/nickna/Conduit/issues/1666), with no demonstrated
production failure.

All 255 frozen inputs and byte copies are unchanged at four stages. The 38 joined
commands retain all 76 output streams. Both owned PostgreSQL and Redis 7.4.2
fixtures were removed and verified absent by ID and name, all four schema-prefix
checks returned zero, and protected image references were unchanged. The managed
PostgreSQL receipt is
`ed257373b62d5a0603e4f9dd9439ba72e557a2ba60540bad7c20be18fb5c3d65`;
the original 19-row TRX is
`728a0a930cdcdb50dd54026587816e9f2d91e416b4935ad5ff724c455a7f2510`.
Independent source, exact TRX-identity, raw-command, fixture-ownership and cleanup
replay is `adb9d85e2d48bc63c5b8a088ec14b2df3eaf98020a9f33a3c71e4133c223b22c`.

Removing all focused diagnostics does not establish generated-query execution,
complete native repository-suite qualification, or production readiness.
Both new follow-ups remain open. #1373 and #1375 remain open, and production
remains JIT under ADR 0006.

## Local copies for function pricing selection

`EfFunctionPricingRuntimeStore.FindEligibleCostAsync` copies the configuration ID,
pricing timestamp, and query cancellation token to locals after context creation,
which still receives the original token. Canonical mapping IDs, active filters,
effective/expiry boundaries, priority/date ranking, no tracking, awaited disposal,
the public signature, and the native guard are unchanged.

The fresh Configuration-owner comparison starts from `da920eb30` with EF
runtime/Design/Tasks/CLI 10.0.12 and Npgsql provider 10.0.3. Both managed build
controls pass. Both precompiler commands still exit 1, but the exact pricing
query's two `ParameterSymbol: functionConfigurationId` renderings disappear,
with no replacement diagnostic. Totals change from 508 renderings across 236
signatures to 506 across 235; every unrelated signature and rendering count is
unchanged. Actual compiler records bind the outer EF `FirstOrDefaultAsync`,
nested `Queryable.Any`, and five query operators in both phases, with proven
`FunctionCosts` and `FunctionCostMappings` roots. These are seven compiler
callsites, not separately executed SQL queries.

The comparison seals 228 retained references and verifies the earlier media,
discovery, and pre-execution configuration source is preserved. Its receipt is
`c28f07decd241378f24238205432c18b6f2e9459e423c210e063fbab3404268d`.
Independent original-log/source/token/SARIF/compiler-input replay is
`f1b8a6eb0dd0469bb97d0541a2cd32fe9addeb22927069806412e799260f0aac`.
The setup-only first attempt and failed file-only summation seals remain retained;
the actual before/after commands each ran once, and corrected sealing reuses
their original captures. No generated files or native process were produced, and
the compiler comparison executed no database queries.

The exact applied variant passes all four existing real PostgreSQL 16.15 pricing
rows without failures or skips. They cover canonical versus shadow mapping IDs,
inactive/future/expired candidates, priority/date ranking and unspecified exact
ties, microsecond effective/expiry boundaries, unknown enums, decimal precision,
SQL NULL versus JSON null, all persisted scalars, detached reads, and the legacy
billing-service selection oracle. Both implementations preserve pre-cancellation
and recover from pool waits and observed SQL lock waits with a single connection
slot. All 33 frozen inputs and byte copies are unchanged at four stages. Both
owned fixtures were removed and verified absent by ID and name, no pricing
schemas remained, and protected image references were unchanged.

The managed PostgreSQL receipt is
`682603a80dc106fe97cf4a8a86878df36e1d12204bcc4da91f9e8b8d11282a6b`;
the original four-row TRX is
`66e6567bf285b7abb8a2f4119598efd9a65cfdd243e9c27dda7b75a615e1946e`.
Independent source, TRX-identity, raw-command, ownership, and cleanup replay is
`0f3237331fda6a56715bc19e1d0bbcadef15c37c58d7a0308fb47ad3030923f8`;
it verifies all 34 joined commands and their 68 captured output streams.

Removing this translation failure does not establish generated-query execution,
complete native repository-suite qualification, or production readiness.
Remaining owner failures still require fixes or verified replacements, and
production remains JIT under ADR 0006.

## Complete model-routing queries with nested typed includes

The exact applied V2 variant rewrites all nine public methods in
`EfModelProviderMappingRuntimeStore` as complete query expressions, with 23
local scalar aliases at the original evaluation boundaries. Five graph readers
inline no tracking and typed `Include` paths for `Provider`,
`ModelProviderTypeAssociation.ModelCost`, and
`ModelProviderTypeAssociation.Model.Series`. The paths retain exactly the
original five navigation edges, repeated association traversal, optional cost,
and required provider/association/model/series graph. All six remaining helpers,
99 mapper assignments (94 scalar and five navigation assignments), and 12 policy
projection fields are unchanged. Guards, culture-sensitive alias lowercasing,
clock sampling, factory cancellation, count-before-offset evaluation, pagination,
cardinality, ordering, materialization, and awaited disposal are preserved.

A first complete-owner candidate was measured and rejected without applying it.
It removed the original failures but introduced five `ThenInclude` replacement
signatures, ten renderings; totals changed from 486/225 to 474/219
renderings/signatures. Pinned EF 10.0.12 source at
`95017c711e6afc1085133d440e42b4bd78155701` explains the observed generator
failure: the operator source check expects one generic type argument, while
`ThenInclude` has two. Nested typed `Include` member paths use the supported
`IQueryable<TEntity>` source and retain the complete navigation union. This
failure family remains tracked in
[#1662](https://github.com/nickna/Conduit/issues/1662); the rejected candidate and
its original compiler captures remain retained.

The accepted fresh Configuration-owner comparison starts from `188834cfd` with
isolated EF runtime/Design/Tasks/CLI 10.0.12 and Npgsql provider 10.0.3. Both
managed build controls pass. Both precompiler commands still exit 1, but all 64
after owner callsites and 11 terminal targets have zero focused failures, with
no added diagnostics or unrelated signature/count changes. Totals change from
486 renderings across 225 signatures to 464 across 214, removing 22 renderings
across 11 signatures. Twenty removed renderings across ten signatures are
uniquely attributable to this owner. The shared association bucket initially
matches two owners and contains four renderings across two signatures; the
controlled change leaves two renderings across one signature for the unchanged
legacy owner. Only that measured two-rendering reduction is attributed to this
change. The baseline token is `008b6bd82fc7281403c1172ab3a5c87237fd0445e2b63933f2747e69f13a3a03`;
the retained legacy diagnostic is not claimed removed. These are compiler
callsite and diagnostic counts, not executed SQL statement counts.

The comparison seals 848 references, including all 582 prior accepted references
and 720 rejected V1 references. Its receipt is
`99f4df3bc9277b61846c04bf2e0fc354c320d576a84e0a054505cb834f28602c`.
Independent original-source/token/span/type/SARIF/compiler-input/log replay is
`ff0e48a3d641fe1f67b421be23b1232a53bcfc087c04aa60692e813f7482fe11`.
The source-application receipt is
`83ff38aa989a5f1b0b7f59b065ba0e9a871e5752121282803c9095645a0d470a`.
The compiler comparison produced no generated files or native process and
executed no database queries. The central package configuration is unchanged:
EF Core/Relational/SQLite 10.0.12, EF Design 10.0.10, and Npgsql/provider 10.0.3;
the experiment aligns the isolated tooling without a framework bump.

The exact repaired test source and applied V2 variant pass all seven selected real PostgreSQL 16.15 rows without failures or skips. The selected
main parity class declares seven rows: one existing fact and three Boolean
theories with both EF and typed Npgsql cases. The passing rows cover complete
graph/policy/cost scalar fidelity, optional nulls, unknown enums, UTC/JSON/decimal
values, detached provider/dictionary snapshots, disabled graphs, misses,
case-sensitive policy/identifier matching, alias priority ordering, 103-row
pagination and provider isolation, cost eligibility/ranking, and cancellation
for all nine reads before execution, during two-slot pool acquisition, and
during observed SQL waits including count and item phases for both page reads.
All seven source-declared and discovered identities join to distinct Passed
TRX results and definitions. All nine reads recover using the same store and
two-slot pool, and both slots are independently reusable after cancellation.

The first real PostgreSQL attempt joined with exit 1: the existing fact passed,
and all six new theory rows failed. Four failures came from seed rows violating
the unique `(ModelAlias, ProviderId)` index. Two cancellation-observer failures
came from matching table names beyond PostgreSQL's 1,024-byte activity-query
capture. The original failed receipt is
`84440361a242407bec329ae404bca4f2e311c4b0c01f45e4efa6b404090fee8f`;
its seven-result TRX is
`4a8ef8f93f86e34f366b401cebc8096b0a20d8ab2144f89e6e027c1cf833cc7b`.
Independent failed-run replay is
`bf582db914f1c5dc0dd14a8bca8e8706e4ad0e0f74cc6fdc48078358d0ef06d0`.
All 251 frozen inputs and copies passed four checks, and all 35 joined commands
and 70 streams remain retained. Both owned fixtures were removed and absent by
ID and name, with protected images unchanged. Post-test schema-count queries
were not executed, so the failed attempt does not establish zero remaining
schemas before fixture removal. No passing runtime result is inferred from it.

Only the new test setup and observation logic were repaired. Snapshot seeds use
distinct raw-case alias/provider pairs while retaining case-insensitive alias
reads and independent provider-dictionary mutation checks. Selection seeds use
101 distinct fixed-width aliases and an additional provider's `page-route-099`
tie, preserving 103 total rows, 102 provider rows, and the alias/ID ordering
boundary at the 100-row cap. The SQL observer now joins activity, ungranted
`pg_locks` relation OIDs, the fully quoted owned schema/table, exact application
and process IDs, and the specific blocker. Both page count and Providers item
phases remain observed before manual cancellation. The seven case identities,
94 scalar/five navigation assertions, 12 policy fields, and recovery logic are
preserved; production sources are unchanged by this test repair.

The current repaired main-test source is
`f4e0a868b2b832509ada254f05997c8cfbaadb2e5d067fe83542bb160658b009`;
`d2437cf47b69b16cd1cfa09da0cef27144927915d2fc5b4e058fe38f80926a85`
identifies the retained failed-run test preimage and historical compiler-capture test gate. Independent source review
is `fe9e2b540a8a37b50188f6266d5137606c15edfc9668483b16b52edae9711fb1`,
and exact test application is
`42dd8fe9f524171021f13676353fb880a3b80d2acb4b70262338a770266d97bc`.
The source-only review and application receipts remain separate from the
fresh actual seven-row runtime run and its independent behavior replay.

Managed PostgreSQL receipt: `0551305542ae29378ed13af75c279e8596642465fc4c1514c764883b479d2de4`.
Original seven-row TRX: `e653b53c55b7ce8fe2d359e122766b6232a8b7d4905feeafca9f7d1e374683e8`.
Independent source/TRX/raw-command/fixture-ownership/cleanup replay:
`ce1bf60ee5021715c185a4bb91386d502066f873e8065f17cb00bb75f9781a04`.
All 251 frozen inputs and their byte copies are unchanged at four stages,
producing 1,004 source/copy joins. The 40 joined commands retain all 80 captured
streams. Actual PostgreSQL is 16.15 with the unchanged default 100-connection
capacity, and actual Redis is 7.4.2. All five schema-prefix checks return zero.
Both owned fixtures were removed and verified absent by ID and name, and
protected image references are unchanged. Independent replay verifies the
original commands, exact seven-row TRX identities, private capture protection,
fixture ownership, and cleanup without rerunning tests or fixtures.

Source-observed follow-ups remain open in
[#1668](https://github.com/nickna/Conduit/issues/1668) for typed Npgsql page-count
connection retention and
[#1669](https://github.com/nickna/Conduit/issues/1669) for eligibility-clock
placement. The EF clock remains sampled before context acquisition; typed
Npgsql samples after acquiring its connection. Wide timing margins do not prove
the two sampling boundaries equivalent, and the two-slot cancellation cases do
not establish one-slot page success. No actual runtime failure is demonstrated
by these source observations. Existing #1666 and #1667 remain open.

Removing these diagnostics does not establish generated-query execution,
complete native repository-suite qualification, or production readiness.
#1373 and #1375 remain open, and production remains JIT under ADR 0006. The
historical `b051389580a358fe7699e45f439d2c3fbc5ddce3` CI baseline does not qualify
the Unit137 commit.

### Unit 138: complete Gateway metrics owner

The complete EfGatewayMetricsStore rewrite preserves six reads, 49 calls, eight SQL terminals, all 18 scalar fields and three collections. Sixteen scalar aliases retain each query's original evaluation, factory token and await-using disposal boundary; `0m` and an explicit `(double?)` retain the decimal comparison and existing conditional Average types. Filters, groups, aggregates, inclusive/half-open bounds, clock reads, ordering/limits and second-query boundaries remain intact.

The first candidate is retained as REJECT: 464/214 became 450/207 after a conditional-expression replacement added one signature/two renderings. V1 was never applied or run against PostgreSQL. V2 adds nine bytes to the existing Average branch; retained Roslyn 5.0 semantic inspection and pinned EF source explain the nullable-lift hypothesis, while actual SDK Roslyn 5.9 supplies the acceptance evidence. [#1672](https://github.com/nickna/Conduit/issues/1672) remains a focused compiler follow-up.

The accepted aligned EF 10.0.12 comparison measures **464 raw/214 signatures → 448/206**. All eight original signatures/sixteen renderings are removed; all 49 calls and eight terminals have zero focused failures. There are no added signatures, unrelated deltas or ambiguity. Both EF passes still exit 1 and emit zero generated artifacts; 1,274 calls and 56 global compiler sources remain measured.

Primary `a50e9889…` and independent replay `d5f374f2…` seal 1,230 references, including all 1,106 rejected R1 references, prior accepted evidence and complete ranking inputs. Applied owner `7693c69b…` and test `9e337d8c…` bind source application `5d293fca…`; exact original→V1→V2 reversals remain retained.

Actual managed PostgreSQL: **11/11 selected rows passed, 0 failed, 0 skipped**. The unchanged Fact and ten EF/typed theory rows exercise complete field/group/empty/boundary oracles and all six reads' pre-cancel, pool recovery and observed SQL cancellation, including both statements of the active-entity and queue reads.

The runtime receipts bind all 284 source inputs and their copies at four ordered stages, exact eleven-row discovery/TRX identities, private joined command streams, PostgreSQL/Redis fixtures, protected images, six empty schema prefixes and removal of both owned IDs/names. Primary runtime: `660803f9ec6cde19bde47d64b011bfede18211c37a0bd561fdc0dbb2a83cf061`; independent file replay: `972e3603467ead0071f4ea3b66077ac9c283ea5eca9cd6067dc5b672840a1d3b`.

The independently authored source/oracle and harness reviews strengthen the source proof; the runtime replay reader was authored by the coverage/harness producer. Its file replay checks actual commands, TRX, sources and cleanup rather than claiming an independently implemented test oracle or an additional execution.

Existing typed validation differences and the native-profile EF test guard remain nonblocking in [#1670](https://github.com/nickna/Conduit/issues/1670)/[#1671](https://github.com/nickna/Conduit/issues/1671). These follow-ups and #1672/#1673 remain open and provide no scope waiver.

At parent d312dfab, [native run 38316762573](https://github.com/nickna/Conduit/actions/runs/38316762573) failed “Collector left a live owned descendant.” after the 204-case promotion gate; later native publish/process/image steps were skipped. Regular CI 38316762657 passed 20 jobs. [#1673](https://github.com/nickna/Conduit/issues/1673) requires collector cleanup repair and a fresh native workflow; the retained console establishes no particular race. This parent result does not qualify the new commit.

Compiler and managed PostgreSQL acceptance do not establish generated-query execution, the complete native repository suite, current-head artifacts, registry qualification or production readiness. #1373/#1375 remain open; production remains JIT under ADR 0006.
### Unit 139: bounded Linux descendant completion

The Linux helper now captures descendant records before terminating a still-live original root, validates start identity and ancestry, and waits for each retained record within the original shared five-second cleanup budget. It accepts a matching zombie, refreshed managed exit, or a latched matching PID/positive start token whose exact stat and PID-directory probes both report typed absence. Permission, malformed metadata, a present directory, unexpected IO and PID reuse remain failures. Only the original root receives `Kill(true)`; private output, child environment, public failure classifications and command bounds remain intact. The collector invokes a new durable metadata regression after its existing helper imports without changing the original 101-case counter.

Validation accepted **31/31 pure metadata cases, 5/5 focused Linux cases and 101/101 full original collector cases**. The maintained 31-case test extracts the applied helper source with disclosed fake Process, IO and scheduling seams; it establishes no Linux process timing, SafeHandle lifetime, signal behavior or cleanup duration. Its initial output recorder falsely failed after omitting PowerShell's information stream; that FAIL remains alongside the separately captured all-streams PASS. Independent replay infers all 31 passes from the strict frozen test and footer because individual per-case event rows were not retained.

The focused and full Linux runs use the reviewed R7 helper in frozen copies with original assertions and budgets and no recorder instrumentation. Independent replay binds all 42 copied sources, 11 joined commands/22 streams, 24 private files, exact owned fixture/image identities, literal source/wrapper/private bind paths, owner-only capture protection and verified ID/name removal. Full primary `26528fc5…`, independent replay `5f092b34…`, maintained pure replay `f5ef823e…` and source application `74c6c67d…`/independent application replay `6b5d5d16…` bind helper `454a332d…`, collector `5c53b9f0…` and metadata test `a818f374…`.

Rejected evidence remains intact. R5 completed its direct timeout case, then failed the first nested deadline assertion with the failing conjunct uncaptured. A separate diagnostic R5 run failed the direct timeout before any selected case completed: typed missing stat metadata reached the retained-descendant wait catch and set cleanup failure while the reap token remained uncanceled. Refreshed `HasExited=false` is a source-flow inference, not a directly logged getter. These diagnostic timing effects and local failures do not establish the original CI caller or cause. Earlier record-only reductions retain five passing cases each but incomplete diagnostics: the first outer receipt was PASS with diagnostics later rejected; the second outer receipt was FAIL because required identities were absent.

One unchanged local collector run also passed 101 cases; its outer cleanup-recognizer FAIL is preserved with a separate exact-ID/name absence supplement. At source `70c22b54`, regular CI passed all 20 jobs and native CI repeated the live-owned-process assertion after the 204-case promotion gate, skipping later native steps. Local PowerShell 7.6.6/.NET 10.0.12 on WSL2 does not establish an identical hosted CI runtime. [#1673](https://github.com/nickna/Conduit/issues/1673) still requires a fresh native workflow at the new source.

Already-exited roots and children forked after the snapshot remain outside the captured wait set. Unix SafeHandle is a managed wait wrapper rather than a PID lifetime pin; [#1674](https://github.com/nickna/Conduit/issues/1674) remains an open, nonblocking ownership follow-up. #1373/#1375 remain open: all remaining repository migrations and complete native coverage are still required. Collector acceptance does not establish generated-query execution, the complete native repository suite, current-head native artifacts, registry qualification or production readiness. Production remains JIT under ADR 0006; this records the current state and grants no scope waiver.

### Unit 140: complete GlobalSettingRepository owner

The complete owner retains eight public methods, two private helpers, twenty calls, six asynchronous query terminals and ten mutation targets. Sixteen scalar aliases and six passive concrete-context aliases preserve validation, ordering, factory cancellation, await-using disposal, logging, clocks and caller-entity mutation boundaries. The typed-Npgsql production implementation remains unchanged.

The first candidate is retained as REJECT: global 448 raw/206 signatures stayed unchanged after five Dynamic LINQ replacement signatures/ten renderings. Pinned QueryLocator source excludes a parameter reference and recognizes a local reference; this explains the qualified source hypothesis for V2's query-receiver aliases. The accepted actual comparison is **448/206 → 438/201**: all twenty owner calls and sixteen targets have zero focused failures, with no added or unrelated diagnostics. The 1,274 global calls, 56 compiler sources and 1,479 sealed references include all 1,354 rejected R1 references. Both EF passes still exit 1 and generate zero artifacts; primary `c239360c…` and independent replay `ebc934a2…` establish focused compiler acceptance.

The first managed attempt failed compilation because the test used unsupported MaximumPoolSize/MinimumPoolSize identifiers. It retained three joined commands/six streams, zero fixtures and no test execution. Both observed 289-input stages were unchanged; the summary remains false because the required four-stage sequence was incomplete. R3 uses the installed MaxPoolSize/MinPoolSize APIs. A subsequent source audit predicted PostgreSQL application-name truncation; R4 uses a unique 37-byte ASCII name with the complete GUID and a 63-character guard, retaining the exact application/PID/relation/ordinal predicates. No long-name runtime failure was reproduced.

The root's repaired actual run passed **12/12 selected TRX rows, zero failed and zero skipped** on fresh PostgreSQL 16.15 and Redis 7.4.2. It retained 47 joined commands/94 private streams, all 289 sources and copies at four ordered stages, 3,737 seal entries over 1,890 unique paths, eleven empty schema prefixes, protected image IDs and exact removal of both owned container IDs/names. Runtime `137bd633…` and file replay `5e15e9e1…` bind the current owner `bd9e4a36…`, test `1d9c4eec…` and distinct source/repair applications.

The retained Fact and eleven additional rows cover independent six-field persistence oracles, all eight methods, validation, caller mutations, factory-token/time/logging/disposal boundaries, pre-cancellation, occupied-pool recovery and observed server waits. Completion of twelve EF and nine typed SQL phases, eighteen pre-cancellation paths and thirty-six occupied-pool paths across capacities one and two is inferred from the strict frozen source and Passed rows; separate subcase event rows were not recorded. The runtime reader was authored by the coverage/harness producer; independent test, harness and reader source reviews are bound separately, and the replay is not another execution or an independently implemented test oracle.

At the Unit 140 review, characterized adapter differences were filed in [#1675](https://github.com/nickna/Conduit/issues/1675) and [#1676](https://github.com/nickna/Conduit/issues/1676): pre-cancel caller timestamps, supplied create IDs, zero-ID updates and missing positive-ID updates differed. Upsert client/server clocks, two-statement versus atomic SQL and repository logging differences were source observations; that review had not reproduced a concurrent-upsert race. The later #1675/#1676 qualifications below reconcile mutation, Upsert and identity behavior. These follow-ups provide no waiver for the semantic reconciliation required by complete native coverage.

The retained report preimage and runtime copy preserve the original `7f500a24…` prefix. Runtime comparisons of that former current report path remain pre-append evidence; the appended document requires its new hash and later commit bindings. Generated-query execution, the complete native repository suite, current-head native artifacts, registry qualification and production readiness remain unestablished. #1373/#1375 and all remaining repository migrations/native coverage are still required. Production remains JIT under ADR 0006; this records the current state and grants no scope waiver.

### Unit 141: complete ProviderKeyCredentialValidator owner

The complete four-method owner retains all six calls and four asynchronous query terminals. Four ID aliases and four terminal cancellation-token aliases preserve validation, exact results and warnings, factory-token forwarding and await-using disposal; the original factory still receives the caller's token before each terminal-token copy. No production entity or typed repository behavior changes.

The first actual SDK candidate remains REJECT: global 438 raw/201 signatures stayed unchanged, replacing three original signatures/eight renderings with three terminal cancellation-parameter signatures/eight renderings. V2 is accepted at **438/201 → 430/198**, with all six owner calls and four targets at zero focused failures and no added or unrelated diagnostic replacements. The controlled shared Count token loses the validator's ParameterSymbol family; the unchanged repository CountByProviderIdAsync still contributes one Dynamic LINQ family/two renderings. Both EF passes still exit 1 with zero generated artifacts. Primary `4b7a4f6e…` and root independent replay `724d268c…` establish focused compiler acceptance, not complete repository or native qualification.

File-only reader and sealer recording failures remain retained alongside their corrected source-bound replays. The reader's initial expression-parse error has no retained exact failing query; its repaired AST handling is a qualified source mechanism. These failures did not execute another SDK comparison or establish candidate runtime failure, and they remain separate from the rejected R1 compiler candidate.

The root's actual run passed **10/10 selected TRX rows, zero failed and zero skipped** after concrete SDK compilation and exact discovery completed before fixture creation. Fresh PostgreSQL 16.15 and Redis 7.4.2 retained 45 joined commands/90 private streams, all 292 current sources and copies at four ordered stages, 1,774 seal entries over 1,765 unique paths, nine empty schema prefixes, protected image identities and exact removal of both owned container IDs/names. Runtime `59d8a320…` and independent file replay `0490c68e…` bind owner `9b6993cf…`, new test `51612fab…` and source/test application `77599a56…`. The file reader is independent of the test and harness authors and authored the variant source; its replay binds actual evidence without claiming an independent review of its own variant design or another execution.

The ten selected rows cover the exact 0/31/32/33 credential limit, enabled/primary/missing flag combinations, result and structured warning fields, unchanged stored entities, constructor validation, original factory tokens, awaited asynchronous disposal and exception identity. An isolated legacy-row case first proves the production check violation, then explicitly characterizes the otherwise invalid disabled-primary pair under a NOT VALID copy of the same constraint; ordinary fixtures retain their production constraints. Completion of four factory-wait cancellations, four pre-cancellation paths, eight occupied-pool paths across capacities one and two, and four observed server-wait cancellations with recovery is inferred from the strict frozen source and accepted Passed rows; separate subcase event rows were not recorded. EF factories receive schema-owned externally disposed NpgsqlDataSource instances.

At parent `6c35039d`, [native run 38341453259](https://github.com/nickna/Conduit/actions/runs/38341453259/job/115113138287) executed 300 selected shared persistence rows: 284 passed, 16 failed, zero skipped, with PostgreSQL 53300. Regular CI passed all twenty jobs. [#1677](https://github.com/nickna/Conduit/issues/1677) remains open; a source-only lifetime audit identifies distinct process-wide legacy pools keyed by schema connection strings as a mechanism to measure, without proving the CI failure's cause. The next complete pool-lifetime unit must retain unchanged-suite connection evidence and fixture ownership before further owner migrations, without increasing the database budget, skipping coverage or clearing unrelated pools.

The exact `7ab1c7ab…` report prefix is retained separately; runtime comparisons of its old current path remain pre-append facts and require the new report hash after materialization. Generated-query execution, the complete native repository suite, current-head native artifacts, registry qualification and production readiness remain unestablished. #1373/#1375 and all remaining repository migrations/native coverage are still required. Production remains JIT under ADR 0006; this grants no scope waiver.

### Unit 142: owned fixture pools for the shared parity suite

Seventeen test/fixture files now use explicitly owned schema data sources across EF setup, repositories, helpers and intentionally occupied pool holders. Each adapter reuses its owned source; mixed EF/typed cases retain distinct sources, while single-adapter cancellation cases keep their occupied holder on that adapter's source. Independent management and oracle connections remain outside the deliberately occupied adapter pools. Sources dispose before GUID-owned schema cleanup, with setup's primary exception preserved and cleanup aggregation handled as applicable. Production persistence implementations are unchanged.

The unchanged selected CI filter executed all **310 rows: 293 passed, 17 failed, zero skipped**, with PostgreSQL 53300 in every failed row. The repaired baseline retained 85 valid samples and reached 100 client backends including the observer, 99 excluding it; observed peak test-role states included 98 idle plus one active, or 99 idle. Before the suite and at the three post-join/removal boundaries, only the observer remained. Earlier sampler and counter/cleanup recording failures remain preserved with exact owned recovery; the sampler's failing method was not captured. Baseline primary bd4fb534… and file replay 469cec2c… retain the failed suite separately from complete inventory and cleanup.

The controlled comparison passed **313/313 rows, zero failed and zero skipped**, preserving every original 310 name and test ID and adding exactly three Billing rows. With the same filter, ordinary ordering, 600-second suite budget, PostgreSQL max_connections=100 and observer, 87 valid samples reached 10 client backends including the observer, nine excluding it. Maximum observed sample gaps were 2,052 ms before and 1,832 ms after; both runs ended with one observer and zero test-role backends. These sampled peaks are lower bounds with one diagnostic slot and timing effects; blank application names do not establish schema or canonical pool/source-instance attribution. Postfix primary 168174cc… and accepted file replay 0b499f43… bind the comparison.

The three new Passed Billing rows assert backend disappearance after successful source disposal and an EF query failure, and preservation of the primary setup exception while both sources close. Their assertions are bound through reviewed source and TRX, rather than separate subphase event counters. File replay verifies each run's 38 joined commands/76 streams, 82 protected original files, four ordered 428-input checks, exact image/mount/fixture ownership and removal of both owned IDs/names. The 17 former-current baseline paths resolve through retained original copies and application preimages; the exact current cf3e679b… report prefix and runtime copy are retained before this append and require its new hash after publication.

The 313-row result was measured on 6695b390 with 17 uncommitted fixture changes; later remote integration requires separate verification. The local host uses the current JIT cleanup probe DLL; Linux CI uses the native probe, so this comparison grants no current-head native qualification. [#1677](https://github.com/nickna/Conduit/issues/1677) still requires fresh native workflow and merge CI evidence. #1373/#1375 and all remaining repository migrations/native coverage remain required. Production remains JIT under ADR 0006; generated-query execution, complete native repository coverage, registry qualification and production readiness remain unestablished.

### #1634 follow-up: fixture ownership and independent cleanup

The 2026-10-16 audit confirms Unit 142's explicit source ownership in both
billing fixture families, function discovery, and async-task parity. EF borrows
the owned source through `UseNpgsql(dataSource)`; closing a context alone is not
treated as closing its pool. The remaining async-task management operations now
use unpooled, bounded connections. Its two schema drops receive independent
deadlines, and their cleanup failures preserve an earlier work failure. No global
pool clearing or PostgreSQL capacity increase is used. See
[database pool ownership](database-pool-ownership.md#persistence-test-fixtures).

New discovery and async-task regressions complement the existing billing
success/query/setup-failure cases. They verify owned backend PID and schema
disappearance inside the live test process and preserve an unrelated idle source.
Discovery observes a blocked production EF query before cancellation; the
async-task cancellation case covers pre-canceled typed work after both pools have
been used. A real table lock also proves the first async-task drop can expire
without skipping the second or replacing the primary failure.

On an independently owned PostgreSQL **16.15** server with unchanged
`max_connections=100`, the current shared CI persistence selector passed the same
**394/394 tests twice, zero failures or skips**, using Windows Release/JIT and the
managed cleanup probe. Each run retained 49 live `pg_stat_activity` samples, with peaks
of 18 and 7 client backends respectively, including the observer. These sampled
peaks are lower bounds and do not establish the cause of historical R2 failures.
Both runs finished with zero owned schemas. The retained `issue-1634-shared-1.trx`,
`issue-1634-shared-2.trx`, `issue-1634-pg16-samples.jsonl`, and tested-input hashes
record this local verification; published-native execution remains a separate
hosted check.

### Unit 143: incoming integration after fixture-pool repair

After Unit 142 commit 2a528784, incoming 48119ec9 was integrated with six reviewed source fixes while preserving the tested fixture bytes. The chat runtime factory now forwards both optional pricing services through its existing constructors, and native-only endpoint registration resolves them; the single public repository constructor remains intact. Configuration tests retain explicit flags and validation ordering while aligning omitted options with intended global defaults. Twelve runtime-factory pricing cases cover the nonstreaming cost path. Security tests require exact 401/429 outcomes and cache-read counts during the existing memory-fallback/legacy-cache-outage scenario; worker tests require the leader/lifecycle adapters to borrow the registered singleton instances.

The corrected core run used the exact CI filter Component!=ReleaseMigration and five connection variables against owned PostgreSQL 16.15/Redis 7.4.2: **5,233 discovered, 5,230 passed, zero failed and three intentionally disabled rows**. All twelve added pricing cases and both Gateway host-composition rows passed. The first unfiltered attempt remains rejected evidence: dotnet exited zero, but its 5,167 Passed/74 NotExecuted rows disagreed with a recorded skipped count of zero and infrastructure cases lacked the required environment. The separate merged shared run passed **313/313 rows with zero failures or skips**, preserving every prior name/test ID, with four ordered checks of 428 selected direct inputs plus six applied bindings. Its 102 valid metadata samples had zero observer errors and a sampled peak of six client backends including the observer, five excluding it; sampled peaks remain lower bounds with one diagnostic slot and timing effects. The shared replay binds 38 joined commands/76 streams, 82 protected files and exact removal of both owned fixture IDs/names with protected images unchanged. Core primary 97456010/inner7d303ff0, shared primary14f5dbc8 and accepted file replays 97992fb3 / 459289d4 bind these results.

These runs measured Windows Release/JIT source at 2a528784 with MERGE_HEAD48119ec9 and the six uncommitted fixes; coverage was not collected and Linux/native equivalence is not claimed. The exact 67,502-byte d691afc9 report prefix and its runtime copy are retained before this append; old current-path checks remain pre-append facts. The earlier 430 renderings/198 signatures are historical until a fresh compiler inventory measures the merged source. #1677 still requires fresh native/merge CI evidence. #1373/#1375, all remaining repository migrations and complete native coverage remain required; production remains JIT under ADR 0006, with no scope waiver, registry qualification or production promotion.

The refreshed clean `9f6ad048` Configuration baseline measures 436 error renderings / 201 signatures and 1,282 calls in 58 source files. The complete EfMediaRuntimeStore now uses a borrowed concrete ConduitDbContext, complete include-deleted and optional-group query branches, scalar/terminal-token locals, and explicitly typed long coalesce projections. Its constructor previously accepted IConfigurationDbContext; all three tracked direct callers already supply ConduitDbContext, and scoped DI resolves that same concrete context. No adapter-owned disposal is introduced. All twelve methods, both seventeen-field mappings, delegation, replay/conflict recovery, query filters, policy ordering and default-policy await ordering retain their behavior.

R1 remains REJECT at 424/196: guarded nullable Value evaluation and two integer/long literal failures replaced the eight original media signatures. R2 remains REJECT at 422/195: a nonnullable ID alias and 0L literals clear the first forms but expose two nullable-long anonymous-projection mismatches. Two explicit outer long casts produce accepted R3 at **418/193**, with all 55 owner calls / 11 terminals at zero focused failures, no added or unrelated diagnostics, and all 1,232 unrelated call bindings preserved. Primary `02bb91c0…` and root replay `c52d8b6e…` retain both rejected attempts. The whole precompiler still exits 1 and generates zero query artifacts. Follow-up [#1678](https://github.com/nickna/Conduit/issues/1678) tracks reductions/upstream disposition; #1667 already owns the literal-constant family.

Actual Windows Release verification passes **326 shared PostgreSQL persistence cases** (the original 313 names/test IDs plus thirteen new media rows), zero failures/skips, and **62 existing media consumer/dependency-boundary cases**, zero failures/skips. New cases exercise provider-observed first/later-query cancellation, no tracking, borrowed-context/scoped transaction visibility and disposal, inactive/missing/default policy selection, null-size/tombstone aggregation, generated identity and durable replay/concurrent-winner failure paths. Shared receipt `d15a6f44…` has 81 valid observer samples, zero observer errors and a sampled peak of six connections including the observer / five excluding it; all 38 commands join, both owned fixtures are removed and protected images remain unchanged. Consumer receipt `97f7a9e9…` is independently replayed by `7fdeef95…`. These are managed behavior and focused compiler results, not generated-query execution or complete published-native repository qualification.

At head `9f6ad048`, regular hosted CI passes all twenty jobs. Native run [38357642205](https://github.com/nickna/Conduit/actions/runs/38357642205) passes publishing, PostgreSQL/converter proofs, shared persistence parity and the two-host proof, then fails the final Gateway boundary probe's zero-LLM-cost agentic response assertion (exit 134). Its exact failed log is retained as `376964f3…`; a separate next unit will align that assertion with the fixture's already-required configured token settlement. No native workflow success or promotion is claimed. Complete repository migration/native coverage and operational rollout remain required under #1373, #1375 and #1368.

## Required-member and decimal-literal reductions (#1667)

Four isolated cases in `tools/EfQueryPrecompilerRepro/Variants` now reduce the
replacement failures described above. Fresh Linux directories use SDK 10.0.401,
EF runtime/Relational/Design/Tasks/CLI 10.0.12 and Npgsql/provider 10.0.3. All four
initial managed builds pass. Query generation and generated-code compilation
remain separate gates:

| Variant | Generation exit | Generated managed build |
|---|---:|---|
| `RequiredProjection` | 1 | Skipped: `VisitMemberInit` expects object creation but receives an invocation |
| `RequiredProjectionAfterMaterialization` | 0 | 0; one generated query source |
| `DecimalImplicitZero` | 1 | Skipped: `Expression.Constant` rejects argument types in `VisitLiteralExpression` |
| `DecimalExplicitZero` | 0 | 0; one generated query source |

The required-member control projects every scalar in its minimal key/group
model before constructing the original required init-only DTO graph. The decimal
pair uses a scalar SELECT with integer `0` versus decimal `0m`, isolating literal
translation from the separate bulk-update setter failures. Unsealed entities
and local context/scalar aliases avoid the unrelated known failures. The two
generated builds retain five and four CS9270/CS8669 warnings respectively.

The matrix and logs are retained under
`artifacts/issues-1469-1696/issue-1667/run-532425a80a474ab0adaa4aec888ea8ee/`;
`matrix.json` SHA-256 is
`19b92f7d6a448355486bae23177df04e796580ebd8018db498d899a7403e81d9`.
Independent replay verifies 77 source, generated-file, package and log bindings;
all fourteen child stages and their streams joined. The actual Design package
identifies source commit `95017c711e6afc1085133d440e42b4bd78155701`.

The existing production scalar snapshots and explicit decimal literals remain
unchanged from branch base `15e187483`. Five current managed virtual-key runtime
parity rows pass against PostgreSQL, including complete snapshots and
cancellation/reuse. This reduction adds no production workaround and does not
replace the earlier owner compiler comparison. The minimal methods were not
executed, no database connection was made by the reductions, and no native EF
publication or generated-query runtime qualification is claimed.

Bounded all-state upstream searches found no exact duplicates. The reduced
required-member failure is filed as [EF #39302](https://github.com/dotnet/efcore/issues/39302),
and the decimal-literal failure as [EF #39303](https://github.com/dotnet/efcore/issues/39303).
[EF #38012](https://github.com/dotnet/efcore/issues/38012) is related conversion
work with a distinct custom-type binary-operator failure. Pinned source supports
the unsafe-constructor/member-initializer and boxed-literal/converted-type leads;
neither an upstream fix nor a tested current-main failure is asserted.

## Nullable conditional lifting reduction (#1672)

`NullableConditionalImplicit` and `NullableConditionalExplicit` in
`tools/EfQueryPrecompilerRepro/Variants` reduce the conditional failure from
Unit 138. The only difference is the nine-byte `(double?)` cast on the existing
true `Average` branch. Both preserve `Any`, both completed-task filters, the
type/time predicates, grouped state/count, nullable result and null arm. The
minimal entity is unsealed, and context/scalar aliases avoid separate known
translator failures.

The fresh Linux SDK 10.0.401 matrix uses EF runtime/Relational/Design/Tasks/CLI
10.0.12 and Npgsql/provider 10.0.3. Both initial builds pass. The implicit
conditional fails generation at `Expression.Condition` through
`VisitConditionalExpression`, producing no file. The explicit cast generates
one file and its managed build passes with seven CS9270/CS8669 warnings.
All nine child stages and their streams join; source copies remain unchanged.
The retained matrix at
`artifacts/issues-1469-1696/issue-1672/run-7028379e23184631b1db3e5c177c4c0a/`
has SHA-256
`c650e59a54363e692fd55fab5e0e60cbfe9677bae218bb19c7dbc338478d4af6`.

A separate fresh Windows SDK 10.0.401 comparison uses the complete current
metrics owner and an immutable copy of the current production model closure.
Both inventories contain six methods, 49 query calls and eight terminals,
with identical call signatures and multiplicities. The existing cast passes
initial build, generation and generated managed build, producing one file.
Deleting only the cast passes initial build but produces one owner-attributed
conditional failure signature printed twice, with no generated file. The
successful generated owner build retains 47 warnings: 39 CS9270, seven CS8669
and one NU1510. All ten child stages and their streams join. The comparison
receipt at `issue-1672/owner-b000dbcf25f64646a9aa74f2ac6cb3aa/comparison.json`
has SHA-256
`f39910e56d5cf7c5f18c145bd37f4e4bdff7b0dcbcfced26c9af7438f649c101`.
Earlier harness-only setup failures are retained separately and excluded.

The current production owner already contains the cast accepted in `70c22b547`;
its SHA-256 remains
`2760819a623e44d2a47e13dd3dce39880205c70c1a68a848a6931f4aa1f0a65d`.
The same source and model assembly are bound to #1671's 14 passing managed
PostgreSQL cases, including complete metrics fields, boundaries and cancellation.
No production query changed and no runtime suite was repeated for this reduction.
Neither compiler experiment connects to a database, executes generated queries
or publishes a native executable. Full Configuration/application compilation,
generated-query runtime coverage and #1373/#1375 qualification remain separate.

Fourteen bounded all-state upstream searches and a final exact-signature recheck
found no exact duplicate. The sanitized standalone report is filed as
[EF #39304](https://github.com/dotnet/efcore/issues/39304), with exact public body
and source readback. The actual package nuspecs identify commit
`95017c711e6afc1085133d440e42b4bd78155701`; its
[conditional visitor](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/efcore/src/EFCore.Design/Query/Internal/CSharpToLinqTranslator.cs#L348)
visits branches independently, while its
[cast visitor](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/efcore/src/EFCore.Design/Query/Internal/CSharpToLinqTranslator.cs#L339)
adds a conversion. This supports the implicit nullable-lift lead, separately
from #1667's literal/`Expression.Constant` failure. The inspected current-main
source has the same visitor bodies; no current-main compiler run or upstream
fix is asserted. The duplicate search was not exhaustive.

## Collector descendant cleanup verification (#1673)

The repair in `f1e97a167` is already an ancestor of this branch. The bounded
process helper and live-owned-descendant assertion retain that repair: capture
verified descendants of a live root, signal only the original root, and wait
for captured identities and streams within the shared five-second cleanup
budget. Start-token/ancestry mismatch, foreign processes and invalid metadata
remain failures. Matching zombie state and strictly verified latched absence
retain their existing completion rules. The originating historical CI case
and particular race remain unknown. The already-exited-root/late-fork limit
continues separately in #1674.

Official GitHub records now confirm that
[fixing run 38331227243](https://github.com/nickna/Conduit/actions/runs/38331227243/job/115081177180)
passed the collector step at the exact fixing commit: 101 collector cases and
31 metadata boundaries. Native publication and the selected persistence and
converter steps also passed. The workflow then failed the separate shared
PostgreSQL parity step: 275 of 289 passed, 14 failed with SQLSTATE `53300`, zero
skipped. Later release/two-host/protocol steps, runtime/symbol uploads and the
image job were skipped. That failure remains part of the #1677 history.

The later [native run 38939591595](https://github.com/nickna/Conduit/actions/runs/38939591595)
at this branch's starting commit `15e187483` completed all three jobs
successfully. Its collector, shared parity, two-host and protocol steps passed.
Those statuses qualify only their recorded source and selected workflow scope;
artifact bytes were not downloaded or independently qualified for this review.

At the #1673 review, the collector, test and attempt-evidence bytes exactly matched #1642's
retained Windows and Linux runs, each passing 144 collector cases plus all
31 metadata boundaries with exit zero and whitespace-only stderr. The
transitive collector inputs remain unchanged, and all 12 promotion inputs
match the retained 204-case runs. Current source hashes, immutable logs and
Git ancestry were checked; no unchanged suite was repeated and no cleanup
assertion or budget was weakened. The official hosted-status receipt has
SHA-256 `55d1b5abed249fc777da41f79d2059d58365d97fc98c607eed7be5f886e22303`.
The final PR must still run the current branch's native workflow and address
its failures. Neither ancestor CI nor retained local checks qualify later
branch changes, the complete native repository suite or production readiness.

## Collector completion after root exit and late forks (#1674)

Two synchronized Linux regressions reproduce the snapshot limit in the unchanged
helper. In one, the root exits after its child's PID, parent, start token and
inherited output pipes are latched. In the other, a single disclosed callback
after the actual descendant snapshot lets the captured child fork a grandchild
and exit; the grandchild is independently identified and reparented before root
termination continues. Both returned an ordinary command timeout after about
five seconds while the independently observed descendant still held both output
pipes. An unrelated control actor remained alive in each case. Cooperative
fixture teardown verified completion of all known actors, and the fresh container's exact ID and
name were absent afterward.

The inspected Linux `6.18.54.1` target has `setsid` from util-linux `2.39.3`,
but a process group alone does not prevent descendants from creating another
session. The launcher also has a
[conditional fork](https://raw.githubusercontent.com/util-linux/util-linux/v2.39.3/sys-utils/setsid.c)
whose ownership would need handling. The exposed cgroup v2 mount is read-only;
no writable delegation was established. A pidfd design must acquire identity at
launch before the original process can be reaped. The
[documented PID-recycling conditions](https://man7.org/linux/man-pages/man2/pidfd_open.2.html)
also describe atomic creation with `CLONE_PIDFD` as an alternative to a launch
handshake.
The documented
[pidfd group-signaling flag](https://man7.org/linux/man-pages/man2/pidfd_send_signal.2.html)
is available since Linux 6.9, but syscall permission under the target's seccomp
policy was not probed. None of these
observations establishes a safe launch-time containment implementation.

The bounded helper instead requires actual output-pipe EOF before treating
stream cleanup as complete. It cancels and observes each outstanding work read
before starting a fresh read, uses the original shared five-second cleanup
deadline, and discards further private output with a separate per-stream limit
of 4,194,304 UTF-16 characters. Cancellation, read failure and reader disposal do
not count as EOF. Missing EOF produces the existing closed cleanup-failure
classification. Existing process identity checks and termination targets remain
unchanged.

The maintained lifecycle test runs in Linux CI after .NET setup and
builds its dependency-free actor in an isolated private directory. Its default
self-build path passed both timelines with the exact cleanup-failure message
after about ten seconds, zero compiler warnings or errors, unrelated control
actors still alive, and all cooperative teardown checks complete. The private
build directory is removed, so its recorded DLL hash cannot be independently
rechecked afterward. Independent replay accepted 99 retained source, log,
timeline and cleanup checks; the workflow policy suite passed all 89 tests.

Fresh full runs passed **144 collector cases plus 31 metadata cases on each
platform**: Windows in 1,088.6 seconds and Linux in 976.3 seconds. Both roots and
output tasks joined with actual EOF and zero-byte stderr; all 42 frozen inputs
matched before, after and on independent reread. The Linux fixture's ten Docker
commands joined, and its exact container ID and name were absent after removal.
Final file-only verification passed 600 checks across 56 retained files, receipt
SHA-256 `cda60636582cdd7c2ef5ac6eb319fb96eeaedcc90491a7bc708ddb2a12322e65`.

An earlier Linux preflight rejected a verification-runner hash mismatch before
either test workload started; that attempt and its successful teardown remain
preserved. The runner correction changed only freeze setup. Exact reconstruction
and AST comparison established identical execution code for the already-loaded
Windows runner and the separately rebound Linux runner; neither full suite was
repeated. The final PR still requires current-branch CI.

This proves redirected endpoint completion or reports cleanup failure for the
covered timelines. It does not prove containment of every descendant, including
one that closes its inherited pipes and escapes the captured tree. It also does
not identify the historical #1673 CI cause or qualify native service artifacts.

## Global-setting mutation and Upsert boundaries (#1675)

The original three-provider PostgreSQL baseline corrected an assumption in Unit
140's checking factory. The actual EF `AddDbContextFactory` implementation
[returns a synchronously constructed context without checking its token](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/efcore/src/EFCore/Internal/DbContextFactory.cs).
Consequently both production-style EF and typed Npgsql changed caller timestamps
on pre-cancelled writes; only the checking test factory preserved them. All nine
observations retained unchanged database rows and recovered the same single-slot
backend. The original SQL counter recorded zero starts but lacked positive
calibration; the final caller tests calibrate it with an actual settings SELECT.
The retained baseline receipt has SHA-256
`f568d3c8c9ddc47d2564e0d9091899e95e0d87e0fd9175a4a1b183722fa4a3d3`.

Create and Update now explicitly check cancellation after validation and before
caller mutation. EF keeps context acquisition before that check. After admission,
timestamps are sampled before physical connection acquisition; later pool
cancellation, observed SQL cancellation or write failure retains those fields.
Default Create uses one client UTC instant for both timestamps; supplied
CreatedAt values remain intact. This intentionally extends the checking
factory's accepted pre-cancel behavior to the production factory.

A second unchanged-Upsert baseline passed all 20 controlled PostgreSQL cases.
It reproduced the typed atomic statement overwriting a committed absent-row
winner, overwriting unchanged snapshot fields, and retargeting a replacement key.
Its timestamps equalled the independently observed PostgreSQL transaction start;
EF bound client UTC parameters after its blocked read completed. All 295 frozen
inputs stayed stable, all 20 owned schemas were absent afterward, and the child
and output streams joined with exit zero and empty stderr. Receipt SHA-256:
`76018291c94171c2f873c64eebee9e8d4df6dccafc95c472c77ff300bb2ccba9`.

Typed Upsert now follows EF's completed read, client UTC sample and write without
a transaction spanning both statements. It uses one connection, a plain INSERT
for an absent row, and the original primary key for an existing row. Snapshot
comparisons preserve concurrent values of unchanged fields. An absent-row winner
causes SQLSTATE `23505`; a disappeared original causes a concurrency failure.
EF retains `DbUpdateException`/`DbUpdateConcurrencyException`, while the EF-free
typed adapter uses `PostgresException`/BCL `DBConcurrencyException`. No retry,
replacement-key lookup or new transaction is introduced. The rare no-change
branch, including an identical newly sampled timestamp, is source-qualified;
runtime coverage does not inject a clock to force it.

EF's existing slow-operation logger still includes factory time and reports
successful delegates before context disposal. The typed adapter has no matching
repository logger. No logging failure was established. At the #1675 review,
direct Create identifiers and Update zero/missing identifiers remained separately
scoped to #1676, qualified below.

Final managed verification passed **41/41 tests with zero skips**: the retained
12-row owner suite, nine caller-boundary rows and 20 controlled Upsert rows.
The SQL counters were positively calibrated; caller snapshots, independent
six-field database rows, exact waiter/blocker/relation identity and same-backend
recovery were checked. All 296 inputs stayed stable during the run, all 29 new
schema-absence markers joined their tests, and the three owned schema families
were empty before and afterward. Receipt SHA-256:
`a7c2f1d197170faad72e8967f39e05dab71fb0ee8efc3c217c48159824fe616f`.
Three existing managed nullable warnings remain scoped to #1690.

A fresh Linux x64 native publication passed with **zero warnings**, followed by
the complete ELF driver in 13.2 seconds. The new helper covered five successes,
five pre-cancellations, five occupied-pool cancellations, seven observed SQL
cancellation phases, four failed writes, seven controlled races and two actual
client-parameter/server-transaction clock observations. All 183 native inputs and
the publish closure stayed stable, all three restore assets were EF-free, owned
schemas were absent, and every child and output stream joined with empty stderr.
Receipt SHA-256:
`220c694f462dec9c2ebf26ce9de7181a5f14a02444380a7c451fc85a327b97b5`.
An earlier successful native run exposed two new helper nullable warnings; its
evidence is retained. The final correction added only two null-forgiving
annotations, mechanically reconstructing the managed-tested helper bytes, and
was followed by the fresh native publication and full run above. This qualifies
the standalone typed leaf, without establishing native EF or application
readiness. Production remains JIT.

## Global-setting identifiers and missing updates (#1676)

The retained #1675 owner suite had already measured the original three identity
differences: EF preserved explicit Create IDs, inserted on zero-ID Update, and
failed missing positive-ID updates; typed Npgsql generated a replacement ID or
returned false. Those passing assertion branches and their binaries were
rechecked without repeating the unchanged suite.

Four additional unchanged-EF PostgreSQL facts passed with 297 stable inputs and
no leftover owned schemas. Create and Update retained both `-7` and
`int.MinValue`; a missing `-900` produced `DbUpdateConcurrencyException` after
one statement. Zero-ID Update preserved supplied CreatedAt or the exact default
`DateTime.MinValue` with `Unspecified` Kind. An independent reader and raw
PostgreSQL text confirmed the latter was `-infinity`. Explicit ID 1 left the
sequence at `(last_value=1, is_called=false)`; the first generated insert failed
with `23505`, consumed 1, and the next insert succeeded at 2. Duplicate ID/key
failures retained caller IDs, admitted timestamps and database rows. Baseline
receipt SHA-256:
`82cc8ddd770b47fc8e45ce8b74536a2c788a6888a31d284a7fcdf97ab784c93d`.

The model's generated-key sentinel is zero. EF treats supplied
[negative keys as real values unless explicitly marked temporary](https://learn.microsoft.com/en-us/ef/core/change-tracking/miscellaneous#temporary-values).
The pinned Npgsql converter accepts
[MinValue as the infinity sentinel](https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/Internal/Converters/Temporal/DateTimeConverterResolver.cs#L41).
PostgreSQL documents that
[aborted writes do not reclaim sequence values](https://www.postgresql.org/docs/16/functions-sequence.html).
The implementation preserves these measured behaviors without changing the
sequence or promising gapless identifiers.

Typed Create and zero-ID Update share an insert helper on their already-owned
connection. Every nonzero Create ID is supplied explicitly; zero requests the
database-generated ID. Only Create fills a default CreatedAt. Update samples
UpdatedAt once and preserves CreatedAt, including its default, before deciding
whether to insert or update. The caller ID is assigned after receiving a
successful insert result. A missing nonzero Update now throws BCL
`DBConcurrencyException`; EF keeps its existing `DbUpdateConcurrencyException`.
Constraint failures retain their provider-specific wrappers. EF production code
and Upsert remain unchanged by #1676.

Final managed verification passed **55/55 tests**, with 55 independently
discovered rows, zero skips and no new warnings. The 14 paired identity rows
join the retained 41 caller/Upsert/owner rows. Exact caller fields and timestamp
Kinds, independent persisted fields, actual sequence states, real constraint
failures, caller tokens and same-backend recovery were checked. All 43 new
schema-absence events joined their tests, and all owned schema prefixes were
empty before and afterward. The stability catalog covered 769 files, including
conditionally excluded owner sources; this does not claim every catalog entry
was selected by the compiler. Managed receipt SHA-256:
`4a0c8da997316edb0aea8ee366288a2c182bb9bbe00079f42b651f1239d47382`.
The three existing nullable warnings remain scoped to #1690.

Fresh Linux x64 publication passed with **zero warnings**, then the complete
native ELF passed in 16.9 seconds. The identity extension covered six success
cases, eight pre-cancellations, eight occupied-pool cancellations, eight observed
SQL cancellations, ten failed writes and three sequence scenarios. Its closed
sequence observations reproduce unchanged explicit-ID state and consumption on
generated collision. The previous mutation, clock and metrics checks also
passed. All 183 native inputs and the publish closure stayed stable; three
restore assets were EF-free, schemas were absent, output and child tasks joined,
and the owned builder was idle afterward. Native receipt SHA-256:
`83773f4118daca7d933531bec98822948ea61ed6d51457b1da1173f4c39ec6f4`.
This qualifies the standalone typed leaf and the selected managed contract,
without establishing native EF or application readiness. Production remains JIT.

## Shared persistence pool qualification (#1677)

The current source retains the seventeen schema-owned fixture pool repairs from
[2a528784](https://github.com/nickna/Conduit/commit/2a5287841ebceb9421883f7ef8040bfa8b55a467)
and the independent cleanup regressions added for #1634. In the repaired fixtures,
EF borrows the owned data source through `UseNpgsql(dataSource)`; consumers finish
before their sources dispose and their GUID schemas are removed. The audit distinguishes older
private-key `ClearPool` helpers, unpooled management connections and common
base-string pools. This verification introduced no capacity increase, global
pool clearing, reduced assertions or additional serialization.

Fresh captures retain both original failed hosted jobs: 275/289 and 284/300
passed, with 14 and 16 failures, SQLSTATE 53300 errors and zero skips. The second job's
new raw-log representation has a different checksum from the historical ticket
capture; the exact cited bytes were not recovered. Later full hosted workflows
at 09a02d43 and 15e187483 passed their then-current 332 and 375 shared rows.
Unit 142's 310/313-row pressure comparison remains committed historical reporting;
its original primary receipts were not located during this audit. These facts
establish chronology and source ownership without retrospectively proving the
original hosted failures' cause.

At 52f89fe63, the complete unchanged 21-clause CI selector passed **504/504 tests,
zero failures or skips**, in 125.2 seconds on Linux Release/JIT, including six
published-native cleanup subprocess cases. Discovery matched all 504 names,
110 more than the earlier #1634 inventory. The run reused the exact retained
85.4-second managed build, six assembly hashes and dependency metadata; the
three existing nullable warnings remain scoped to #1690. All 2,703 build/test
catalog inputs and three private runners stayed stable. This catalog includes
conditionally excluded files and does not claim that every entry was compiled.
The native cleanup cases used the retained #1676 ELF, rather than a new publish.

The independently owned PostgreSQL 16.15 server retained
`max_connections=100`, zero general reserved slots and three superuser reserved
slots. A persistent unpooled observer collected 1,209 samples at a requested
100 ms interval: sampled peak **10 client connections including the observer**,
nine other clients and nine in the test database. Maximum sample gap was
159.7 ms; these sampled peaks are lower bounds with observer overhead. Actual
`conduit` schema inventories were empty before and afterward. An independent
post-observer query found only its own connection, no test clients or schemas,
and no previous observer PID. All child processes and output streams joined.
Primary receipt SHA-256:
`350970d86afb9edda71bcf94b34eac54348f206b8795aa0079107640150417a8`.
Independent replay SHA-256:
`50551ead89e2dcd72aba1b0c94148d04c464158f602e44d47046ced89b9e4cb1`.

Two rejected local attempts remain preserved. The first executed 504 rows with
seven failures: five directly exposed unreachable IPv6 connections, and two
HTTP 503 responses omitted their original exceptions. The IPv4 attempt executed
all 504 with one connection timeout before the intended injected exception.
Both HTTP rows passed in that attempt; their original causes remain unestablished.
The first attempt's one leftover converter schema was archived and only that
proved-owned GUID schema was removed. The second ended with no owned schemas or
test backends. [#1704](https://github.com/nickna/Conduit/issues/1704) tracks the
fixture's discarded exception diagnostics. The passing run used the existing
owned Docker bridge directly, a separate transport context that does not
diagnose either historical incident.

[NativeAOT run 38990109216](https://github.com/nickna/Conduit/actions/runs/38990109216)
at exact 52f89fe63 passed **all three jobs**: Linux publish/process smoke and both
Admin and Gateway native images. The hosted shared suite also passed
**504/504, zero failures or skips**. Its official test artifact matches every
local display name and stable test ID, including the six native cleanup cases.
Independent hosted-artifact replay SHA-256:
`6032d379323ed4839c8313d7eb61f402e1c91ed396e0b363c11ad8b554494df0`.
The workflow passes its configured native contract and image gates; native EF
query execution and complete native application coverage remain unestablished.
Production remains JIT.

## Guarded nullable and coalesce compiler reductions (#1678)

Four opt-in variants in the
[standalone diagnostic project](../../tools/EfQueryPrecompilerRepro/README.md#guarded-nullable-id-and-nullable-long-coalesce-1678)
reduce the two media-owner compiler failures. The nullable-ID pair retains both
complete `HasValue` branches and their three-field projections. Captured
`.Value` fails design-time parameter evaluation; capturing a nonnullable
`GetValueOrDefault()` scalar generates and compiles successfully. The coalesce
pair retains the join, filters, anonymous projection, grouping, count, sum and
terminal token. Already-typed `0L` still produces a nullable-long argument for a
nonnullable anonymous member at `Expression.New`; adding only the six-byte
outer `(long)` cast generates and compiles successfully.

All four initial managed builds pass on Linux SDK 10.0.401 with EF runtime,
Relational, Design, Tasks and CLI 10.0.12 and Npgsql/provider 10.0.3. The guarded
original retains one CS8629 warning. Each failed generation exits 1 with no
interceptor; each control exits 0, emits one file and passes its generated
managed build, retaining eight and seven CS9270/CS8669 warnings respectively.
Both nine-stage matrices preserve six inputs, package identities, source copies,
generated hashes and joined processes/streams. Their full receipts and commands
are recorded in the project README.

The sanitized reports are [EF #39308](https://github.com/dotnet/efcore/issues/39308)
and [EF #39309](https://github.com/dotnet/efcore/issues/39309), following 24 bounded
all-state issue/PR searches. Further inspection found that open
[PR #39305](https://github.com/dotnet/efcore/pull/39305) has a proposed null-instance
gate that may also cover the nullable-ID case. That patch was not executed and
has no explicit guarded nullable test; its effect on this reproducer remains
unestablished. The coalesce failure remains separate from the recorded literal,
conditional and earlier runtime funcletizer fixes. Inspected current-main source
was not built or executed.

Production media queries already contain both workarounds and are unchanged.
Their current source matches the just-completed #1677 suites, including
14 passing ordinary managed media parity cases. These reductions execute no
query methods, open no database connection and publish no native executable.
Generated-interceptor execution and complete native EF/application coverage
remain unestablished. Production remains JIT.
