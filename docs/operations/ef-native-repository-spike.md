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
