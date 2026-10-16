# EF query-precompiler reductions

Opt-in, design-time reproductions for Conduit [#1660](https://github.com/nickna/Conduit/issues/1660).
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
    dotnet ef dbcontext optimize --project EfQueryPrecompilerRepro.csproj --startup-project EfQueryPrecompilerRepro.csproj --context EfQueryPrecompilerRepro.ReproContext -c Release --output-dir Generated --namespace EfQueryPrecompilerRepro.Generated --no-scaffold --precompile-queries --nativeaot --no-build
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
