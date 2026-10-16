# EF query-precompiler reductions

Opt-in, design-time reproductions for Conduit [#1660](https://github.com/nickna/Conduit/issues/1660)
and [#1662](https://github.com/nickna/Conduit/issues/1662).
This project is deliberately outside `Conduit.slnx`. It references no Conduit
service, uses an inert design-time factory at loopback port 1, and its entrypoint
does not call any query. Do not publish or execute its query methods to reproduce
these compiler failures.

The local package file aligns EF runtime, Relational, Design and Tasks at
10.0.12, with Npgsql/provider 10.0.3. It also retains the repository's patched
MSBuild and cryptography dependencies without changing the service dependency
graph. The measured commands used Windows x64 and SDK 10.0.401.

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
    dotnet build EfQueryPrecompilerRepro.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Initial build failed before query analysis.' }
    dotnet ef dbcontext optimize --project EfQueryPrecompilerRepro.csproj --startup-project EfQueryPrecompilerRepro.csproj --context $context --configuration Release --output-dir Generated --namespace EfQueryPrecompilerRepro.Generated --no-scaffold --precompile-queries --nativeaot --no-build
    $optimizeExit = $LASTEXITCODE
    Write-Host "Query generation exit: $optimizeExit"
    # For generation-success controls, compile the generated interceptors as a separate gate.
    if ($optimizeExit -eq 0) { dotnet build EfQueryPrecompilerRepro.csproj -c Release --no-restore }
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
