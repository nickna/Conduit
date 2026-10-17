# EF query-precompiler reductions

Opt-in, design-time reproductions for Conduit [#1660](https://github.com/nickna/Conduit/issues/1660),
[#1662](https://github.com/nickna/Conduit/issues/1662),
[#1667](https://github.com/nickna/Conduit/issues/1667),
[#1672](https://github.com/nickna/Conduit/issues/1672),
[#1678](https://github.com/nickna/Conduit/issues/1678),
[#1695](https://github.com/nickna/Conduit/issues/1695), and
[#1696](https://github.com/nickna/Conduit/issues/1696).
This project is deliberately outside `Conduit.slnx`. It references no Conduit
service, uses an inert design-time factory at loopback port 1, and its entrypoint
does not call any query. Do not publish or execute its query methods to reproduce
these compiler failures.

The local package file aligns EF runtime, Relational, Design and Tasks at
10.0.12, with Npgsql/provider 10.0.3. It also retains the repository's patched
MSBuild and cryptography dependencies without changing the service dependency
graph. The #1660/#1662 commands used Windows x64; the #1667/#1672/#1678 matrices used Linux.
Both used SDK 10.0.401.

## Repeat a case

Run from the repository root with PowerShell 7. Use a fresh directory for each
case so generated interceptors from another source location cannot enter a run.
Restore the repository's pinned `dotnet-ef` 10.0.12 tool first.

```powershell
dotnet tool restore
$variant = 'BoolLiteral'
$context = 'EfQueryPrecompilerRepro.ReproContext'
$scratch = Join-Path $PWD "artifacts/ef-query-repro/$([Guid]::NewGuid().ToString('N'))/$variant"
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$source = Join-Path $PWD 'tools/EfQueryPrecompilerRepro'
foreach ($name in @('EfQueryPrecompilerRepro.csproj', 'Directory.Packages.props', 'Program.cs', 'ReproContext.cs')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination $scratch
}
Copy-Item -LiteralPath (Join-Path $source 'Variants') -Destination $scratch -Recurse
# Pin the measured SDK; install it first if it is unavailable.
'{"sdk":{"version":"10.0.401","rollForward":"disable"}}' | Set-Content (Join-Path $scratch 'global.json')
$previousVariant = $env:ReproVariant
Push-Location $scratch
try {
    # The environment property also reaches EF's MSBuildWorkspace, unlike a build-only -p switch.
    $env:ReproVariant = $variant
    dotnet build EfQueryPrecompilerRepro.csproj -c Release -m:1 --tl:off
    if ($LASTEXITCODE -ne 0) { throw 'Initial build failed before query analysis.' }
    dotnet ef dbcontext optimize --project EfQueryPrecompilerRepro.csproj --startup-project EfQueryPrecompilerRepro.csproj --context $context --configuration Release --output-dir Generated --namespace EfQueryPrecompilerRepro.Generated --no-scaffold --precompile-queries --nativeaot --no-build
    $optimizeExit = $LASTEXITCODE
    Write-Host "Query generation exit: $optimizeExit"
    # For generation-success controls, compile the generated interceptors as a separate gate.
    if ($optimizeExit -eq 0) { dotnet build EfQueryPrecompilerRepro.csproj -c Release --no-restore -m:1 --tl:off }
} finally {
    Pop-Location
    $env:ReproVariant = $previousVariant
}
```

The context is aliased into a local before querying, matching the production
call site. QueryLocator in this EF version rejects a context method parameter
as dynamic before reaching the failure of interest. Earlier parameter-based
attempts were retained but are excluded from the reduction conclusions.

## Measured matrix

All seven initial builds exited 0 with zero warnings/errors. Generation results
are separate from compilation of generated code and from query execution.

| Variant | Query generation exit | Result |
|---|---:|---|
| `ExpiredKeys` | 1 | Original predicate and both value setters: `EvaluateOperand` NRE |
| `BoolOnly` | 1 | Original predicate, boolean value setter: same NRE |
| `TimestampOnly` | 1 | Original predicate, captured timestamp setter: same NRE |
| `BoolLiteral` | 1 | No predicate, `SetProperty(vk => vk.IsEnabled, false)`: same NRE |
| `BoolExpression` | 0 | No predicate, boolean value lambda; generated managed build exits 0 |
| `ExpiredKeysValueLambdas` | 0 | Original predicate and both value lambdas; generated managed build exits 0 |
| `ReadPredicate` | 0 | Original predicate with `ToListAsync`; generated managed build exits 1, separate sealed-entity `CS0030` |

The two successful generated update controls retain experimental/obsolete and
nullable compiler warnings. They establish neither NativeAOT publication nor
database execution. The sealed-entity cast in the read control matches
[EF #37957](https://github.com/dotnet/efcore/issues/37957); it is not the setter NRE.

Original logs, process exits, resolved package assets and source hashes are
retained in `artifacts/issues-1660/run-c03b70932ea84752bbc797ca70e03879/`.
The matrix SHA-256 is
`129ab69280d32f9260c52602a3cbc61cef5c55c73cfd7b93a16a6e1fc037f43d`;
the source-manifest SHA-256 is
`e9cf49f97407dd27688aab527a4421c8bf990751bfcbee378035cea4a2fca940`.
An optional control interrupted by a host SDK update was retained separately;
the accepted repeat pins SDK 10.0.401. No database connection, mutation, query
execution, native publication, or repository-suite qualification occurred.

## Upstream review

The straight-line update is not the documented dynamic-query restriction.
EF 10.0.12's [generator](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore.Design/Query/Internal/PrecompiledQueryCodeGenerator.cs)
and [tests](https://github.com/dotnet/efcore/blob/v10.0.12/test/EFCore.Relational.Specification.Tests/Query/PrecompiledQueryRelationalTestBase.cs)
include `ExecuteUpdate` and captured setters.

Bounded all-state issue/PR searches covered `ExecuteUpdate precompiled`,
`ExecuteUpdate precompilation`, `ExecuteUpdate AOT`, `EvaluateOperand
NullReferenceException`, `CalculatePathsToEvaluatableRoots`, `VisitUnary`,
`funcletizer`, and the 28 `area-precompiled-queries` issues. No exact duplicate
was found. [#35494](https://github.com/dotnet/efcore/issues/35494) has a different
`ParameterSymbol`/`UnreachableException` signature; runtime issues
[#35656](https://github.com/dotnet/efcore/issues/35656),
[#35152](https://github.com/dotnet/efcore/issues/35152), and
[PR #37323](https://github.com/dotnet/efcore/pull/37323) also differ.
Filed the reduced setter failure as
[EF #39271](https://github.com/dotnet/efcore/issues/39271). The additional
sealed-entity compilation failure is tracked locally in
[Conduit #1695](https://github.com/nickna/Conduit/issues/1695), linked to the
existing upstream issue rather than filing a duplicate there.

The null-forgiven path in `ExpressionTreeFuncletizer.VisitUnary.EvaluateOperand`
is a source lead, not a proven root cause. A successful value-lambda control is
a diagnostic result, not a general workaround guarantee.

## Sealed-entity generated compilation tracker (#1695)

A fresh Linux repeat on 2026-10-17 UTC retained SDK 10.0.401, aligned EF
runtime/Relational/Design/Tasks/tool 10.0.12 and Npgsql/provider 10.0.3. The unchanged
`ReadPredicate` control produced these distinct stages:

| Stage | Exit | Observation |
|---|---:|---|
| Initial Release build | 0 | Zero warnings; the entity remains sealed |
| Query precompilation | 0 | One interceptor source generated |
| Generated-code Release build | 1 | `CS0030` casting sealed `VirtualKey` to `IInjectableService` at generated line 116; four warnings also retained |

The fresh matrix is retained under
`artifacts/issues-1469-1696/issue-1695/run-e49759debacc42e598a1641751f36d94/`;
its SHA-256 is `7c93558906865cccd695a756c7901e9ae5b30d999271425ed86453b7c222c890`.
The interceptor SHA-256 is
`929c495f7160e9afa24adece4af385f7f3bda86118d7be693b836a555994732b`.
All five selected inputs stayed unchanged. No database connection, query
execution or native publication occurred.

The same-day official review found [EF #37957](https://github.com/dotnet/efcore/issues/37957)
open in Backlog, with no identified linked merged fix or supported fixed version.
The latest stable release inspected was 10.0.13; it was not built or tested here.
Retain this independent post-generation failure in the
[failure ledger](../../docs/operations/ef-query-precompiler-failure-inventory.md#independent-generated-code-compilation-controls).
When a compatible upstream-fixed version becomes available, repeat both query
generation and generated-code compilation in a fresh directory, retaining the
10.0.12 baseline. Generation success alone cannot close the tracker. Do not remove
production `sealed` modifiers or suppress diagnostics; repository dependencies
and the broader #1373 qualification remain unchanged.

## Additional expression reductions (#1662)

Use the same fresh-directory commands, setting `$variant` and `$context` before
copying/building. Each selected file contains its own minimal model and inert
factory; the common executable and dependency pins remain unchanged.

| Variant prefix | `$context` |
|---|---|
| `ProviderSync` | `EfQueryPrecompilerRepro.SyncContext` |
| `ProviderTools` | `EfQueryPrecompilerRepro.ToolsContext` |
| `Discovery` | `EfQueryPrecompilerRepro.DiscoveryContext` |
| `Billing` | `EfQueryPrecompilerRepro.BillingContext` |

All 13 accepted initial builds exited 0. The generation/compilation outcomes are:

| Variant | Generation exit | Generated managed build | Observation |
|---|---:|---:|---|
| `ProviderSyncIncludes` | 1 | Not run | Original retrieved-object property capture, including navigation chains: wrapped NRE |
| `ProviderSyncCaptured` | 1 | Not run | Same property capture without includes: wrapped NRE |
| `ProviderSyncObjectLocal` | 1 | Not run | Locally initialized non-null object, no prior query: wrapped NRE |
| `ProviderSyncScalarControl` | 0 | 0 | Copy mapping ID into a scalar local before querying |
| `ProviderToolsForeachField` | 1 | Not run | Original context field and foreach DTO members: wrapped NRE |
| `ProviderToolsForeachLocal` | 1 | Not run | Local context and foreach DTO members: wrapped NRE |
| `ProviderToolsForeachScalars` | 0 | 0 | Copy DTO properties into scalar locals before querying |
| `DiscoveryChain` | 1 | Not run | Original two `ThenInclude` chain: non-IQueryable first-parameter exception |
| `DiscoveryOneThenInclude` | 1 | Not run | A single `ThenInclude` suffices |
| `DiscoveryIncludeOnly` | 0 | 1 | Include-only generation succeeds; dotted local identifier causes `CS1003`/`CS1002` |
| `BillingCheckpoint` | 1 | Not run | Unqualified class constant: unknown `CheckpointId` identifier |
| `BillingLocalNoToken` | 0 | 0 | Constant copied to local; optional token omitted |
| `BillingLiteralNoToken` | 0 | 0 | Literal substituted; optional token omitted |

The two wrapped NRE forms reduce to captured custom-object property access,
including a source-initialized non-null object. They are filed together as
[EF #39272](https://github.com/dotnet/efcore/issues/39272). The translator's fake
null for a flowing-in reference local is a source lead; it does not establish
that an application object is null at runtime. The scalar controls preserve
the queries' intent for diagnosis, without changing production queries.

The constant and `ThenInclude` failures are filed separately as
[EF #39273](https://github.com/dotnet/efcore/issues/39273) and
[EF #39274](https://github.com/dotnet/efcore/issues/39274). Source review identifies
the missing constant-field handling and the generator's one-type-argument guard
as possible causes. Bounded all-state exact-signature, `const`/`ThenInclude`
precompile/NativeAOT, `IIncludableQueryable`, and translator searches found no
exact duplicates. The official limitations do not explicitly exclude these
fixed query forms. No EF patch or intentional-restriction disposition is claimed.

The Include-only generated variable
`efQueryPrecompilerRepro.DiscoveryMappingPrimaryKeyProperties` exactly matches
[EF #37972](https://github.com/dotnet/efcore/issues/37972), fixed upstream by
[PR #39208](https://github.com/dotnet/efcore/pull/39208). The tested package still
exhibits it; [Conduit #1696](https://github.com/nickna/Conduit/issues/1696) tracks
that additional failure without duplicating the upstream report.

Original captures and per-process/source/package hashes are retained under
`artifacts/issues-1662/`. The aggregate `matrix.json` SHA-256 is
`54e8f976cfbe83922a5717f58dcacb9590c66ddb6dd1195e6b6210850b1db6e3`.
Only an extra final blank line was removed from each of the three ProviderTools
sources after capture; original source hashes remain in the ledger. The
`committed-matrix.json` additionally records those normalized file hashes;
its SHA-256 is `30cae313b4b559b8f85635d7c2bf4f787b0670dfb6ee45ee88391641e11ca984`.
The accepted matrix excludes earlier wrong-configuration attempts that did not
reach analysis, and a discovery attempt stopped earlier at known #35494 on the
model method parameter. The accepted discovery cases copy that parameter to a
local. Billing controls first retaining a method-parameter cancellation token
stopped at #35494; the final controls explicitly omit that optional argument
to isolate the constant. The original billing failure retains the token.

Repeat commands use EF's `--configuration Release`: its `-c` alias selects the
context, unlike `dotnet build -c`. This corrects the earlier README abbreviation;
the #1660 accepted original logs reached the recorded query signatures. The
new runs preserve explicit argument lists and actual child working directories.
Successful generated builds retain experimental/nullable warnings. No sensitive
data logging, database connection, query execution, native publication, or
repository-suite qualification occurred in these reductions. #1373 remains open.

## Dotted local-name generated compilation tracker (#1696)

A fresh Linux repeat on 2026-10-17 UTC selected `DiscoveryIncludeOnly` with
`EfQueryPrecompilerRepro.DiscoveryContext`, retaining SDK 10.0.401, EF 10.0.12 and
Npgsql/provider 10.0.3. Initial Release build and query generation each exited 0;
one interceptor was emitted. Its generated-code Release build exited 1 with
`CS1003`/`CS1002` at line 90, containing the invalid local identifier
`efQueryPrecompilerRepro.DiscoveryMappingPrimaryKeyProperties`. This is separate
from the `ThenInclude` generation failure and the sealed-entity compilation
failure. No model or production entity was renamed.

The fresh matrix is retained under
`artifacts/issues-1469-1696/issue-1696/run-bff2ec75d6774b77b0c311ec2f75498c/`;
its SHA-256 is `69d363f82c4ec6fd2597e2bf7fce712f8ec56333c0312785aec6a5b86557bb3d`.
The interceptor SHA-256 is
`cd68a98f7c6b4bc8253a2910cf6e78c6981272f5698ea016a284248822290573`.
All five selected inputs stayed unchanged. No database connection, query
execution or native publication occurred.

[EF #37972](https://github.com/dotnet/efcore/issues/37972) is closed by
[PR #39208](https://github.com/dotnet/efcore/pull/39208), merged into `main` on
2026-10-09 at `b62c52f21921e1114c0144c2f77c18395b29cf5e`. The 2026-10-17 UTC review
found its `ShortName()` fix on the 12.0.0-alpha main line, with issue milestone
`12.0-preview1`. The inspected
[10.0.13 tag](https://github.com/dotnet/efcore/blob/v10.0.13/src/EFCore/Query/ShapedQueryCompilingExpressionVisitor.cs)
and `release/10.0` source still use
the namespace-qualified name; no backport was identified. That source inspection
does not test either a newer package or the fixed main-line code.

Track compatible package availability separately from merge status. When an
upstream-fixed supported dependency becomes available, repeat generation and
generated-code compilation in a fresh directory alongside the retained 10.0.12
baseline. Inspect the emitted identifier and keep the original failure stage in
the [independent ledger](../../docs/operations/ef-query-precompiler-failure-inventory.md#independent-generated-code-compilation-controls).
Do not suppress compiler errors or change repository package pins to declare
this diagnostic control, or the wider #1373 workload, qualified.

## Required-member and decimal-literal reductions (#1667)

These four isolated cases reduce [Conduit #1667](https://github.com/nickna/Conduit/issues/1667).
Use the fresh-directory commands above with the following variant/context pairs.
Both pairs alias context and scalar parameters into locals and omit optional
query cancellation-token arguments to avoid the separate #35494 failure. Their
EF entities are unsealed to avoid #37957 before the expression being tested.

| Variants | `$context` |
|---|---|
| `RequiredProjection`, `RequiredProjectionAfterMaterialization` | `EfQueryPrecompilerRepro.RequiredProjectionContext` |
| `DecimalImplicitZero`, `DecimalExplicitZero` | `EfQueryPrecompilerRepro.DecimalLiteralContext` |

The accepted run used Linux, SDK 10.0.401, .NET 10.0.12, PowerShell 7.6.6,
EF runtime/Relational/Design/Tasks/CLI 10.0.12, and Npgsql/provider 10.0.3.
Every initial managed build exited 0. Each source was unchanged across its run.

| Variant | Generation exit | Generated managed build | Observation |
|---|---:|---:|---|
| `RequiredProjection` | 1 | Not run | Required init-only `Group` DTO projection: `VisitMemberInit` expects `ObjectCreationExpressionSyntax` but receives `InvocationExpressionSyntax` |
| `RequiredProjectionAfterMaterialization` | 0 | 0 | Materialize the same four scalars anonymously; construct the complete required DTO after `await` |
| `DecimalImplicitZero` | 1 | Not run | Decimal conditional with integer `0`: `Argument types do not match` in `Expression.Constant` / `VisitLiteralExpression` |
| `DecimalExplicitZero` | 0 | 0 | Same query with both zero literals written as `0m` |

Each successful control produced one interceptor file. The required-member and
decimal generated managed builds retain five and four CS9270/CS8669 warnings,
respectively. The decimal projection alone reproduces the literal failure, so
no bulk-update setter is needed to reach it.

The pinned translator's [constructor branch](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/efcore/src/EFCore.Design/Query/Internal/LinqToCSharpSyntaxTranslator.cs#L2079)
uses an unsafe-accessor invocation for a required-member type whose constructor
lacks `SetsRequiredMembers`; its [member-initializer translation](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/efcore/src/EFCore.Design/Query/Internal/LinqToCSharpSyntaxTranslator.cs#L2476)
expects object creation. The [literal translation](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/efcore/src/EFCore.Design/Query/Internal/CSharpToLinqTranslator.cs#L760)
passes the token value and semantic converted type to `Expression.Constant`.
These source paths support the reduced signatures; they do not establish a
general EF fix or workaround guarantee.

The required-member and decimal-literal failures are filed separately as
[EF #39302](https://github.com/dotnet/efcore/issues/39302) and
[EF #39303](https://github.com/dotnet/efcore/issues/39303). A bounded all-state
review of 24 exact-signature, required-member, literal-conversion and precompiled
query searches found no exact duplicate. Related
[EF #38012](https://github.com/dotnet/efcore/issues/38012) concerns custom
`Name`/string conversion in a binary expression, rather than this boxed integer
literal with decimal converted type. This search does not establish an
intentional restriction or an upstream fix.

The capture is retained under
`artifacts/issues-1469-1696/issue-1667/run-532425a80a474ab0adaa4aec888ea8ee/`.
Its `matrix.json` SHA-256 is
`19b92f7d6a448355486bae23177df04e796580ebd8018db498d899a7403e81d9`.
The pre-run `variant-source-manifest.json` in the parent issue directory has
SHA-256 `f1489fdc4cf9d50130bac7b56e1d55f18b73fd50b93757cc5953c5d36c9b5a81`.
The ledger retains per-stage argument lists, child/stream joins, source hashes,
resolved package identities, generated-file hashes, and stdout/stderr hashes.
No query method ran, database connection opened, or native publication occurred.
The design-time `--nativeaot` flag does not qualify native runtime execution.

Production `EfVirtualKeyRuntimeStore` already uses complete anonymous scalar
materialization followed by DTO construction and explicit decimal `0m`; this
reduction changes no production query. The separate
`existing-workaround-parity.json` receipt records five passing managed
PostgreSQL parity rows from `issue-1666-r3.trx`, including complete snapshots,
touch isolation, cancellation, and connection reuse. Those existing production
tests execute neither these generated interceptors nor a native EF runtime.

## Nullable conditional lifting (#1672)

Use the fresh-directory commands with `$context` set to
`EfQueryPrecompilerRepro.NullableConditionalContext` and select either
`NullableConditionalImplicit` or `NullableConditionalExplicit`. Both files
contain the same minimal unsealed task entity and three-field generation-metric
record. They retain the type/time filters, grouped state/count, `Any`, both
completed-task filters, duration `Average` and null arm. The sole nine-byte
difference is `(double?)` on the true branch. Local context/scalar aliases and
an omitted optional token avoid unrelated translator failures.

The Linux run used SDK 10.0.401, EF runtime/Relational/Design/Tasks/CLI 10.0.12
and Npgsql/provider 10.0.3. Both initial builds exited zero.

| Variant | Generation exit | Generated managed build | Observation |
|---|---:|---|---|
| `NullableConditionalImplicit` | 1 | Skipped; no generated file | `Argument types do not match` at `Expression.Condition` / `VisitConditionalExpression` |
| `NullableConditionalExplicit` | 0 | 0; one generated file | Explicit nullable conversion preserves the complete grouped projection |

The successful generated build retains seven warnings: five CS9270 and two
CS8669. The actual runtime, Design, Tasks and CLI nuspecs identify source commit
`95017c711e6afc1085133d440e42b4bd78155701`. Its
[conditional visitor](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/efcore/src/EFCore.Design/Query/Internal/CSharpToLinqTranslator.cs#L348)
visits both branches independently; the
[cast visitor](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/efcore/src/EFCore.Design/Query/Internal/CSharpToLinqTranslator.cs#L339)
constructs a conversion. This supports the missing implicit nullable-lift lead.
It is separate from #1667's decimal literal failure at `Expression.Constant`.

The sanitized standalone report is filed as
[EF #39304](https://github.com/dotnet/efcore/issues/39304). Fourteen bounded
all-state searches and a final exact-signature recheck found no exact duplicate.
Related runtime conditional issues and precompiler literal/custom-type
conversion failures were reviewed separately. The search was not exhaustive;
the inspected current-main visitor source was not compiled or executed.

The retained capture is
`artifacts/issues-1469-1696/issue-1672/run-7028379e23184631b1db3e5c177c4c0a/`;
the matrix SHA-256 is
`c650e59a54363e692fd55fab5e0e60cbfe9677bae218bb19c7dbc338478d4af6`.
All nine compiler stages and their streams joined, with source copies unchanged.
Independent readback checks the copied sources, generated control and actual
package assets. No query method ran, database connection opened or native
executable was published or executed in this reduction.

A separate fresh Windows SDK 10.0.401 comparison includes the complete current
`EfGatewayMetricsStore` source and the current production model assembly. Both
variants retain six methods, 49 query calls and eight terminals. The existing
cast generates one complete-owner interceptor file that compiles; deleting only
the cast produces one conditional failure signature, printed twice, and no
generated file. The generated owner build retains 47 CS9270/CS8669/NU1510
warnings. Its comparison receipt SHA-256 is
`f39910e56d5cf7c5f18c145bd37f4e4bdff7b0dcbcfced26c9af7438f649c101`.
Production already contains the cast accepted in `70c22b547`; this reduction
changes no production query. The unchanged source/model also matches #1671's
14 passing managed metrics cases against PostgreSQL. Those existing runtime
cases execute the ordinary adapters; the generated interceptors remain unexecuted.

## Guarded nullable ID and nullable-long coalesce (#1678)

Use the fresh-directory commands above with these variant/context pairs. Each
pair includes its own unsealed entity model and inert design-time factory.

| Variants | `$context` |
|---|---|
| `GuardedNullableIdValue`, `GuardedNullableIdScalar` | `EfQueryPrecompilerRepro.GuardedNullableIdContext` |
| `NullableLongCoalesce`, `NullableLongCoalesceExplicit` | `EfQueryPrecompilerRepro.MediaLongContext` |

The guarded-ID pair preserves the complete `HasValue` ternary, filtered and
unfiltered queries, and the same three-field anonymous projections. The original
captures an unchanged nullable alias and accesses `.Value` inside the filtered
query. The control captures `GetValueOrDefault()` into a nonnullable scalar and
retains the original guard. Neither variant passes an optional query token.

The coalesce pair preserves the media/key join, group filter, ignored deletion
filter, anonymous `SizeBytes`, grouping, count, sum and explicitly aliased
terminal token. Both use `0L`; the sole six-byte difference is the outer `(long)`
cast. This separates it from #1667's integer-literal conversion and #1672's
nullable conditional lifting.

Both Linux matrices used SDK 10.0.401, .NET 10.0.12, EF runtime/Relational/
Design/Tasks/CLI 10.0.12 and Npgsql/provider 10.0.3. All initial builds exited 0.

| Variant | Generation exit | Generated managed build | Observation |
|---|---:|---|---|
| `GuardedNullableIdValue` | 1 | Skipped; no generated file | Wrapped parameter-evaluation failure, inner `Nullable object must have a value`, through `ExpressionTreeFuncletizer.EvaluateCore` |
| `GuardedNullableIdScalar` | 0 | 0; one generated file | Nonnullable capture retains the complete guarded query |
| `NullableLongCoalesce` | 1 | Skipped; no generated file | Nullable `Int64` argument does not match nonnullable anonymous member at `Expression.New` / `VisitAnonymousObjectCreationExpression` |
| `NullableLongCoalesceExplicit` | 0 | 0; one generated file | Explicit outer conversion preserves the complete aggregate query |

The guarded original's initial build retains one CS8629 warning; the other
initial builds have none. The generated controls retain eight and seven
CS9270/CS8669 warnings, respectively. Each matrix joins nine bounded compiler
stages and their streams, six stable source inputs, actual package assets and
the generated control's hash. The retained matrices are:

- `artifacts/issues-1469-1696/issue-1678-id/run-57184abf66e14edf9c46aa26f5796cf2/matrix.json`,
  SHA-256 `d84ae14da8941c79f988166f268c3d992889dd7ef92005f6d389d84b1556c898`.
- `artifacts/issues-1469-1696/issue-1678-long/run-1974983702f7459a87b75f601a13291e/matrix.json`,
  SHA-256 `94b9a7418c4a34532e93e69c662f503d7776f95f51c77697865def76c3526104`.

Pinned source supports two leads. The translator's
[dummy captured-field value](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/efcore/src/EFCore.Design/Query/Internal/CSharpToLinqTranslator.cs#L1314)
and the funcletizer's
[member evaluation](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/efcore/src/EFCore/Query/Internal/ExpressionTreeFuncletizer.cs#L2238)
are consistent with evaluating the empty nullable during design time. The
[binary visitor](https://github.com/dotnet/dotnet/blob/95017c711e6afc1085133d440e42b4bd78155701/src/efcore/src/EFCore.Design/Query/Internal/CSharpToLinqTranslator.cs#L258)
lifts the coalesce RHS to the nullable LHS type before anonymous construction
uses semantic member types. These are source leads, without a demonstrated
upstream patch. Current-main source was inspected at immutable 2e8882748, without
building or executing it.

The separate reports are
[EF #39308](https://github.com/dotnet/efcore/issues/39308) and
[EF #39309](https://github.com/dotnet/efcore/issues/39309). Twenty-four bounded
all-state issue/PR searches found no exact report duplicate. Further review of
[proposed fix #39305](https://github.com/dotnet/efcore/pull/39305), which addresses
#39272, found source-level overlap with the nullable-ID failure: its null-instance
gate can also apply to an empty boxed nullable. The patch is open/unmerged at
inspection, lacks an explicit `Nullable<T>.Value`/`HasValue` test, and was not
executed here. It is a related candidate fix. #39309 differs from #39303's
literal and #39304's conditional failures, and from the earlier runtime
funcletizer coalesce fix #35122. The search was not exhaustive.

Production `EfMediaRuntimeStore` already has both source workarounds. Its current
source matches the complete #1677 local and hosted shared suites, including
14 passing media parity cases. The separate `existing-workaround-parity.json`
receipt has SHA-256
`05a1cffc7419697c1fe88646e1e9d417f61f18a438ab1ef7dc538cb840003313`.
Those cases execute the ordinary managed adapters. The reductions execute no
query method, open no database connection and publish no native executable;
generated-interceptor runtime execution remains unestablished. #1373 stays open.
