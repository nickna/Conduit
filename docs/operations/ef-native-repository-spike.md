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

Existing coverage cancels before execution and during pool acquisition. Adding
observed SQL-in-flight cancellation cases for all three reads is tracked as a
non-blocking coverage improvement in
[#1665](https://github.com/nickna/Conduit/issues/1665); no current cancellation
failure has been demonstrated.

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

Characterized adapter differences remain open in [#1675](https://github.com/nickna/Conduit/issues/1675) and [#1676](https://github.com/nickna/Conduit/issues/1676): pre-cancel caller timestamps, supplied create IDs, zero-ID updates and missing positive-ID updates differ. Upsert client/server clocks, two-statement versus atomic SQL and repository logging differences remain qualified source observations; no concurrent-upsert race was reproduced. These follow-ups provide no waiver for the semantic reconciliation required by complete native coverage.

The retained report preimage and runtime copy preserve the original `7f500a24…` prefix. Runtime comparisons of that former current report path remain pre-append evidence; the appended document requires its new hash and later commit bindings. Generated-query execution, the complete native repository suite, current-head native artifacts, registry qualification and production readiness remain unestablished. #1373/#1375 and all remaining repository migrations/native coverage are still required. Production remains JIT under ADR 0006; this records the current state and grants no scope waiver.