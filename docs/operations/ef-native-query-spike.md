# EF NativeAOT query-precompilation spike

Measured on 2026-10-12 for [#1373](https://github.com/nickna/Conduit/issues/1373).
The five query-owning project passes failed. Neither attempted repository-suite
NativeAOT publish produced a runnable suite, so **zero repository cases executed
natively**. This preserves [ADR 0006](../decisions/0006-native-aot-persistence.md)'s
production-JIT decision. It does not complete #1373's full native repository-suite
scope or qualify a production persistence workload.

## Source and isolation

The experiment used a `git archive` of
`b051389580a358fe7699e45f439d2c3fbc5ddce3`, tree
`00b81a90bbcc62c9b8683b1f8e7ef163c874b4cc`, extracted to an ignored scratch
directory. All experimental package, project, startup-factory, and dispatcher
changes were made there. Runtime service projects retain their existing tooling
isolation and selected native contracts.

The toolchain was .NET SDK 10.0.401, EF runtime/Relational/Design/Tasks and
`dotnet-ef` 10.0.12, with Npgsql EF provider 10.0.3. Native publish attempts ran
on Windows for `win-x64`. Design was aligned from the
source baseline's 10.0.10 only in the scratch copy. The actual Configuration
`project.assets.json` and child working directory were checked before accepting
these results. Twelve earlier captures used an ambient child working directory;
they were retained and explicitly invalidated, and do not substantiate this spike.

The existing [lexical inventory](persistence-aot-query-inventory.md) has 72
candidate files and 1,355 operator occurrences across these five projects. These
are review candidates, not unique SQL queries or a complete semantic inventory.
Each owner was passed separately to EF's project query analyzer; loading a project
as a startup dependency does not establish analysis of all its query call sites.

## Observed results

Every command below exited 1 and its process was joined. Durations are elapsed
wall time from the bounded command receipt, rounded to two decimal places.

| Operation | Seconds | Observed failure stage |
|---|---:|---|
| Configuration query precompiler | 15.81 | Dynamic query rejection and internal parameter-translation exceptions |
| Core query precompiler | 10.43 | Dynamic query rejection |
| Providers query precompiler | 10.58 | Internal parameter-translation exceptions |
| Admin query precompiler | 21.63 | Dynamic query rejection, internal parameter-translation exceptions, and an `ExecuteUpdateAsync` expression-processing null reference |
| Gateway query precompiler | 16.05 | Dynamic query rejection and internal parameter-translation exceptions |
| Core repository dispatcher, native publish | 79.12 | Fatal `CS2012` output-file lock; EF query-precompilation diagnostics were also emitted |
| PostgreSQL repository dispatcher, serial native publish | 80.41 | Fatal `CS0246`: original parity tests reference EF adapters excluded by `CONDUIT_NATIVE_AOT` |

The internal `ParameterSymbol`/`UnreachableException` signature matches
[upstream EF issue 35494](https://github.com/dotnet/efcore/issues/35494).
Repeated diagnostic renderings were not counted as distinct unsupported queries.
`MSB3026` retry messages and IL warning occurrences were not counted as fatal
compiler errors or successful query coverage. The PostgreSQL publish did not
reach database execution and does not establish a PostgreSQL translation failure.

Both generated dispatchers subsequently compiled as managed programs with
`PublishAot=false`: Core in 56.57 seconds and PostgreSQL in 14.26 seconds, exit 0.
These controls check direct call bindings and original assertion-package use;
they are not test execution or native qualification.

The dispatcher plan contains 224 explicit cases: 88 Core cases (75 SQLite,
3 mock-based, 10 pure) and 136 PostgreSQL cases. It preserves literal inline-data
expansion, a fresh test instance per row, fixture lifetime, and disposal. Each of
the 224 cases is recorded as `NOT_EXECUTED` following its suite's publish failure.
The plan covers selected repository/parity test folders, not every production
query owner or the complete repository suite. No real PostgreSQL or Redis fixture
was started for these failed publishes.

There is no accepted generated-query size, native suite executable size, startup,
throughput, latency, or memory measurement for this EF workload. A failed publish
cannot supply those measurements. Historical typed-Npgsql and selected-service
benchmarks retain their own source, workload, and support-contract limits.

## Repeating the query-precompiler experiment

Use a fresh scratch checkout or archive of the recorded commit. Keep these
experimental changes confined to that copy:

| Owner | Project path |
|---|---|
| Configuration | `Shared/ConduitLLM.Configuration/ConduitLLM.Configuration.csproj` |
| Core | `Shared/ConduitLLM.Core/ConduitLLM.Core.csproj` |
| Providers | `Shared/ConduitLLM.Providers/ConduitLLM.Providers.csproj` |
| Admin | `Services/ConduitLLM.Admin/ConduitLLM.Admin.csproj` |
| Gateway | `Services/ConduitLLM.Gateway/ConduitLLM.Gateway.csproj` |

1. Align EF Design and Tasks with runtime 10.0.12; retain the pinned `dotnet-ef`
   10.0.12 and Npgsql provider 10.0.3. Add a private EF Tasks reference to each of
   Configuration, Core, Providers, Admin, and Gateway.
2. Enable `GenerateRuntimeConfigurationFiles` and append
   `Microsoft.EntityFrameworkCore.GeneratedInterceptors` to
   `InterceptorsNamespaces` in the scratch build properties.
3. Create `tools/ConduitLLM.EfQueryOwnerStartup/ConduitLLM.EfQueryOwnerStartup.csproj`
   as an inert executable startup project referencing all five owners and
   EF Design. Its `IDesignTimeDbContextFactory<ConduitDbContext>` must delegate to
   `new ConfigurationDbContextFactory().CreateDbContext(args)`. Its ordinary
   entrypoint must exit without starting either service. Build it in Release with
   `ConduitEfTooling=true` and `OpenApiGenerateDocumentsOnBuild=false`.
4. Set `CONDUIT_EF_COMPILED_MODEL=true` and an inert, noncredential database
   connection such as loopback port 1. No runtime database fixture is implied by
   this design-time pass. Restore and build before using `--no-build`.
5. Run the following command once for each actual owning project. Configuration
   can use itself as startup; the other four use the inert startup executable.
   Capture original stdout, stderr, exit code, elapsed time, package assets,
   source identity, and generated-file inventory. Set the child process's actual
   working directory to the scratch root; PowerShell `Set-Location` alone is
   insufficient for a custom `ProcessStartInfo` wrapper.

```powershell
dotnet ef dbcontext optimize `
  --project <owning-project.csproj> `
  --startup-project <inert-startup.csproj> `
  --context ConduitLLM.Configuration.ConduitDbContext `
  --configuration Release `
  --output-dir ExperimentalCompiledQueries `
  --namespace ConduitLLM.Configuration.Data.CompiledModels `
  --no-scaffold --precompile-queries --nativeaot --no-build
```

`--no-scaffold` separates query generation from the existing checked-in compiled
model. Configuration's recorded output directory was `Data/CompiledQueriesSpike`;
the other four used `ExperimentalCompiledQueries`. A compiled-model regeneration without `--precompile-queries` does not
repeat this experiment. EF Tasks' publish-time stages must be retained when
testing its native publish pipeline; disabling them would change the claim.
See the official [NativeAOT query documentation](https://learn.microsoft.com/en-us/ef/core/performance/nativeaot-and-precompiled-queries)
and [MSBuild integration documentation](https://learn.microsoft.com/en-us/ef/core/cli/msbuild).

## Remaining qualification work

Resolve the analyzer failures or select and prove a typed replacement operation
by operation. Complete the semantic query and repository-suite inventory, then
publish a runner that actually executes that complete inventory against real
PostgreSQL with per-case results and failure-stage accounting. Existing xUnit
runner discovery or a successful native link alone is insufficient.

The compiled-model switch currently omits the `MediaRecord.DeletedAt` and
`VirtualKeyGroupTransaction.SoftDelete` query filters. Deleted-row behavior needs
explicit parity oracles before a full EF native workload can be accepted. Do not
silently remove filters, excluded test cases, or unsupported query shapes to
obtain a passing result.

Measure the complete proved workload's size, publish duration, startup,
throughput, latency, and memory, and update the promotion decision with its
limitations. Keep #1373 open while this scope remains unproven; the selected
Admin/Gateway evidence does not substitute for it.

## Evidence identifiers

Original command captures, invalidated attempts, package assets, the immutable
case plan, and independent reviews remain in the ignored epic evidence archive.
The following SHA-256 identifiers bind the principal receipts; they are not a
substitute for the original logs or executable evidence.

| Receipt | SHA-256 |
|---|---|
| Frozen source | `fdf5aabe5fdffeede99c48c3179b4970950cf07fa4d6a2c6d77c584c2ffa9792` |
| Invalid ambient-directory attempts | `4308a6d101ef4ca4628d38fc40e2f450ddb44ecba419c3655835539ebdce9f8f` |
| Configuration precompiler | `fcda8305e9404cfcfabf5dd6d6dd2c7132ec365dad2a7cb1db52632a6340c15b` |
| Other four owner passes | `db383f85853a02e6139c7efcf6fc4f4be2ac7514a64f285860400ed3c7b5a190` |
| Core native publish | `21c31382aa7b766eea18151587fba4784a19ff10fa501f3a9434d6c239d34e6f` |
| PostgreSQL native publish | `b8a6eb7a1b838d2ea08114bcf28c1e13fc439749c1205f41fd2c65aca35a0e7c` |
| Core managed compile control | `09e91f18e892050668435ced769b5fa992607062c03f42da69b69318bfd4903b` |
| PostgreSQL managed compile control | `e8139342569464173796a4dfa67beabac6af3283940a517e41bda5b2969114e4` |
| 224-case plan | `e267105dcf6f5db94334c5fb847f74addde8765281565d4f1775c0a0a24de126` |
| Independent dispatcher source review | `230bb1220567d5d4fab330c9aa69b1b809813b29134dfce13a281542935d3f5a` |
| 224-case native outcome ledger | `1e65139f32a592d22bc9cbb29b0ffca33a2996e29d7fcb8f529f3b16e0419a9b` |
| Independent outcome and original-hash verification | `e840650f53c56e18d6750aff7f6c40e3d9814e663799253fb92696457c71915e` |
