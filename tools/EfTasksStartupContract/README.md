# EF Tasks startup parameter contract

Diagnostic follow-up for [Conduit #1663](https://github.com/nickna/Conduit/issues/1663).
This opt-in program sits outside the normal solution and loads the pinned
EF Tasks 10.0.12 assembly to inspect validation and command construction only.
It never calls the task's `Execute`, starts an EF child, loads a startup
assembly, connects to a database, or publishes native code.

Run from the repository root in PowerShell 7:

```powershell
$package = Join-Path $env:USERPROFILE '.nuget/packages/microsoft.entityframeworkcore.tasks/10.0.12'
$evidence = Join-Path $PWD "artifacts/issues-1663/contract-$([Guid]::NewGuid().ToString('N'))"
dotnet run --project tools/EfTasksStartupContract/EfTasksStartupContract.csproj -c Release -- $package $evidence
```

The measured Windows x64 run used SDK 10.0.402 and passed 15 checks, exit 0.
The build retains seven `EF1001` warnings because testing the pinned internal
task contract deliberately references `OperationTaskBase`; no warning is
suppressed. The evidence directory contains the rendered command and result.
The rendered command is inspected, never executed or advertised as a working
invocation. Dependency versions reuse the isolated query-reproducer pins.

## Supported arrangement and stale guidance

The official [MSBuild integration limitations](https://learn.microsoft.com/en-us/ef/core/cli/msbuild#limitations)
do **not** support selecting a different startup project. Support was removed by
[EF PR #34034](https://github.com/dotnet/efcore/pull/34034) in June 2024 to avoid
build dependency cycles. Maintainer explanations in
[#34935](https://github.com/dotnet/efcore/issues/34935) and
[#34988](https://github.com/dotnet/efcore/issues/34988) direct the context project
to be self-sufficient, with `IDesignTimeDbContextFactory<TContext>` in that
project when configuration normally comes from a host builder. A separate
library containing the context can follow that arrangement without selecting a
separate MSBuild startup project. This probe does not qualify every project graph.

The installed 10.0.12 package's `docs/PACKAGE.md` still recommends
`EFStartupProject` with a `.csproj` path and an extra Tasks reference in the
startup project. No mapping for that property exists in the package's
`buildTransitive` props/targets. This is stale guidance, not a supported setting.

The CLI is a separate interface: `dotnet ef` supports explicit `--project` and
`--startup-project`, with `--configuration Release` selecting the configuration.
Those options do not imply equivalent MSBuild property support.

## Observed internal 10.0.12 contract

The installed targets pass `EFStartupAssembly` to `OptimizeDbContext.StartupAssembly`
and `DbContextType` to its context selector. The source
[OperationTaskBase](https://github.com/dotnet/efcore/blob/v10.0.12/src/EFCore.Tasks/Tasks/Internal/OperationTaskBase.cs)
and the probe agree:

- An explicitly supplied startup selector must have a lowercase `.exe` suffix;
  `.dll`, `.csproj`, and `.EXE` are rejected. This is a suffix check, not proof
  that an executable exists or is usable.
- The rejection message still names `EFStartupProject`, although the targets
  consume `EFStartupAssembly`. The diagnostic is misleading.
- Command construction replaces the `.exe` suffix with `.dll` for the
  `--startup-assembly` argument; it does not execute the apphost directly.
- Sibling `<name>.deps.json` and `<name>.runtimeconfig.json` are conditionally
  passed when present. The validation method does not require or validate their
  contents, and the absent runtime configuration can use the runtime-version
  fallback. The probe uses presence-only `{}` fixtures.
- Omitting the selector is not rejected by this suffix validation. That fact
  does not prove a library alone has the runtime configuration needed by EF.

The old Windows spike supplied a `.dll` incorrectly, then reached query
precompilation after supplying `.exe`. Neither attempt produced a native suite;
that observation does not establish an officially supported workaround, Linux
failure, portability guarantee, or successful EF native query execution.

Original stdout and a source/package-target receipt are retained under
`artifacts/issues-1663/`. The `receipt.json` SHA-256 is
`9784459f24ed4209c1cbe41d390003c96aea3096ea67581d603693a7829b0aed`.

All-state searches for `EFStartupProject`, `EFStartupAssembly`, separate startup,
and Tasks package guidance found the removal history and no exact stale-package
documentation/diagnostic ticket. The upstream disposition is to remove the old
instructions and clarify the retained diagnostic/implementation without
advertising removed workflow support; filed as
[EF #39275](https://github.com/dotnet/efcore/issues/39275). No service dependency or runtime behavior
changes here. #1373's full native repository-suite scope remains open.
