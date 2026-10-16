# NativeAOT persistence query inventory

Lexical scan: 2026-10-11, rechecked after unit 98 on the #1368 implementation branch.
The 72-file/1,355-site inventory below is unchanged. The selected native profile
passes all three jobs in
[Linux run 38175668349](https://github.com/nickna/Conduit/actions/runs/38175668349)
at `e0f9f9904df187460ab338bd86619d912f653d60`: 219 selected persistence and integration
cases without skips, native publish/OpenAPI, the complete host gate and both
packaged entrypoints, with zero first-party linker diagnostics and owned cleanup.
That run predates the separately identified local cache convergence proof.
Owner: database/runtime maintainers. Related: #1368, #1373, #1374,
[ADR 0006](../decisions/0006-native-aot-persistence.md), and
[ADR 0009](../decisions/0009-staged-native-persistence-refactor.md).

Production Gateway and Admin remain JIT. The native Gateway selects typed Npgsql
for extracted persistence slices; unextracted EF operations still exist in
both service graphs. Publishing successfully or emitting zero first-party warnings
does not establish that those operations execute natively.
The [Gateway matrix](gateway-native-aot-feature-matrix.md) and
[Admin matrix](native-aot-admin-feature-matrix.md) define the selected contracts and
native worker omissions. JIT retains the full worker profile.

The original 2026-08-11 audit reported 37 direct-query files and 344 query-operator
occurrences. Those are historical figures, not the current remaining-work count.
The current scan below also includes Core and Providers, where persistence callers
now live, and uses an explicit lexical definition. Its counts cannot be compared
directly with the original audit's counts.

## Reproducible source inventory

Run this PowerShell from the repository root with `rg` installed. It selects C#
files importing EF Core, excludes migrations/generated models/build output, and
counts the listed query-call tokens. It emits each candidate owner before totals.

```powershell
$auditRoots = @('Services/ConduitLLM.Gateway', 'Services/ConduitLLM.Admin', 'Shared')
$auditPattern = '\.(?:Where|Select|SelectMany|Include|ThenInclude|OrderBy|OrderByDescending|ThenBy|ThenByDescending|GroupBy|Join|GroupJoin|Skip|Take|Distinct|AsNoTracking|AsSplitQuery|AsQueryable|ToListAsync|ToArrayAsync|ToDictionaryAsync|FirstAsync|FirstOrDefaultAsync|SingleAsync|SingleOrDefaultAsync|AnyAsync|CountAsync|LongCountAsync|SumAsync|AverageAsync|MaxAsync|MinAsync|ExecuteUpdateAsync|ExecuteDeleteAsync|FromSqlRaw|FromSqlInterpolated|ExecuteSqlRawAsync|ExecuteSqlInterpolatedAsync)\s*\('
$auditCandidates = @(rg -l 'using Microsoft\.EntityFrameworkCore(?:\.[^;]+)?;' $auditRoots `
    -g '*.cs' -g '!**/Migrations/**' -g '!**/CompiledModels/**' -g '!**/obj/**' -g '!**/bin/**')
$auditRows = @($auditCandidates | Sort-Object | ForEach-Object {
    $auditText = Get-Content -LiteralPath $_ -Raw
    $auditSites = [regex]::Matches($auditText, $auditPattern).Count
    if ($auditSites -gt 0) {
        [pscustomobject]@{ Path = $_; Sites = $auditSites }
    }
})
$auditRows | Format-Table -AutoSize | Out-String | Write-Host
$auditRows | Group-Object { ($_.Path -split '[/\\]')[1] } | ForEach-Object {
    [pscustomobject]@{
        Project = $_.Name
        Files = $_.Count
        Sites = ($_.Group | Measure-Object -Property Sites -Sum).Sum
    }
} | Format-Table -AutoSize | Out-String | Write-Host
[pscustomobject]@{
    Files = $auditRows.Count
    Sites = ($auditRows | Measure-Object -Property Sites -Sum).Sum
} | Format-Table -AutoSize | Out-String | Write-Host
```

At the audited commit:

| Project | Candidate source files | Lexical query-call sites |
|---|---:|---:|
| ConduitLLM.Admin | 26 | 352 |
| ConduitLLM.Gateway | 4 | 39 |
| ConduitLLM.Configuration | 40 | 943 |
| ConduitLLM.Core | 1 | 9 |
| ConduitLLM.Providers | 1 | 12 |
| Total | 72 | 1,355 |

This is a review aid, not a count of unsupported SQL queries. It includes in-memory
LINQ in EF-importing files, EF reference adapters retained for JIT, and the
release-owned `SimpleMigrationService`. It excludes writes without a listed query
token, inherited operations, raw connection commands, and callers without an EF
import. In particular, `EfRequestLogRuntimeStore` and `EfAsyncTaskRuntimeStore`
still require inspection even when this scan does not select them. Trace the
registration and called operation before labeling a file a native blocker.

Repository interfaces return materialized values rather than `IQueryable`.
`IRepositoryBase` no longer accepts caller-supplied predicates/order delegates.
Dynamic `IQueryable` composition remains inside implementations and reporting
helpers, so an interface guard alone is not sufficient native evidence.

The [2026-10-12 isolated EF query-precompilation spike](ef-native-query-spike.md)
attempted all five owners with aligned EF 10.0.12 tooling. All five passes failed;
two repository-dispatcher native publishes failed before execution. Its 224
planned cases are explicitly unexecuted and do not complete #1373's full native
repository-suite scope. See that report for failure stages, source identity,
repeat steps, and the compiled-model soft-delete limitations.
The [observed failure index](ef-query-precompiler-failure-inventory.md) records
derived source matches and explicit ambiguities. It also identifies nine direct
repository tests outside the 224-case plan, preserving the full-suite coverage gap.

## Extracted slices and actual evidence

Contracts live in `Shared/ConduitLLM.Persistence.Abstractions`; typed implementations
live in `Shared/ConduitLLM.Persistence.Npgsql`. Core native Gateway store selection is centralized
in `Services/ConduitLLM.Gateway/Extensions/NativeRuntimePersistenceExtensions.cs`.
JIT Gateway and JIT Admin retain EF-backed implementations. Native Admin also selects
its typed global-setting and IP-filter repositories. IP-filter conditional requests
read versions through that same typed repository, including after mutations, without
resolving an EF context. Both native hosts select the typed billing-audit
batch writer through their billing-service registrations, with a per-host timer and
shutdown drain; other Admin management paths remain EF-backed. The shared contract tests are
under `Tests/ConduitLLM.IntegrationTests/Tests`; they run against real PostgreSQL in
the NativeAOT workflow.

| Slice / contract | Shared EF/Npgsql contract test | Published native evidence and limit |
|---|---|---|
| Global settings / `IGlobalSettingRepository` | `GlobalSettingRepositoryParityTests` | Persistence probe exercises ordered reads, key/id lookup, writes, upsert, and delete. [Linux run 38147134135](https://github.com/nickna/Conduit/actions/runs/38147134135) also passes authenticated Admin six-field CRUD, merge-patch/upsert semantics, pagination, protected validation and cache acknowledgements. Those acknowledgements alone do not establish propagation. The later unit 96 local native proof verifies ordinary changes reach both Gateway caches before manual reload, direct SQL remains stale, explicit reload consumes it, and deletion/reload restores defaults, with full gate and owned cleanup. Separate generations and unavailable Git provenance remain explicit in the feature matrices. |
| IP-filter policy / `IIpFilterRepository` | `IpFilterRepositoryParityTests`, `IpFilterVersionEndpointFilterTests` | Persistence probe exercises scoped CRUD and UTC/null columns; Gateway probe authenticates under global/per-key default-deny policy. The unit 76 local published Linux Admin HTTP gate and successful [Linux run 38152552344](https://github.com/nickna/Conduit/actions/runs/38152552344) pass all 12 SQL/11 DTO fields, paginated global/enabled/per-key reads, merge patches, strong ETags, 428/412, no-op/deletion/missing-resource behavior and database-rule enforcement using a real `127.0.0.2` source socket. Three GUID-owned names are cleaned again after producers stop; every unrelated IP/global-setting scalar remains unchanged. Optimistic concurrency/FK cases have shared-contract evidence. Other resource-version paths retain EF. |
| Providers and credentials / `IProviderRepository`, `IProviderKeyCredentialRepository` | `ProviderPersistenceParityTests` | Persistence probe exercises JSONB, credential graphs, primary rotation, and cascade; Gateway provider process checks resolved credentials. Failed-rotation rollback has shared-contract evidence. Admin credential validation remains a separate EF consumer. |
| Virtual-key runtime / `IVirtualKeyRuntimeStore` | `VirtualKeyRuntimePersistenceParityTests` | Persistence probe covers hydrated snapshots, concurrent/idempotent ledger debits; Gateway probe checks exact settled request costs, balances, and lifetime spend. Management CRUD remains EF. |
| Model routing / `IModelProviderMappingRuntimeStore` | `ModelProviderMappingRuntimePersistenceParityTests` | Persistence probe covers route graphs/policy; Gateway probe exercises `/v1/models`, model retrieval/metadata, and provider pricing. This does not cover the separate `/v1/conduit/discovery/models` EF projection or its warmer. |
| Request-log writes / `IRequestLogRuntimeStore` | `RequestLogRuntimePersistenceParityTests` | Persistence probe checks persisted accounting mappings; Gateway probe checks exact token/cost rows. Native accounting retains its typed writer/store. Unit 77 omits the separate legacy request-log queue/worker; reporting and retention remain EF-backed and unsupported. |
| Billing-audit batch writes / `IBillingAuditRuntimeWriter` | `BillingAuditPersistenceParityTests`, `BillingAuditServicePersistenceTests` | [Linux run 38149774358](https://github.com/nickna/Conduit/actions/runs/38149774358) passes the standalone probe's 17 scalars, generated defaults, ordered immutable results, 129-row atomic rollback/retry, constraints and SQL/pool cancellation with reuse. Its host gate checks independent subthreshold timers on two quiet Gateways, then a fresh timer marker and pending shutdown tail on each host in turn, owned graceful exits, exclusive final-flush witnesses and all 17 stored fields. Real PostgreSQL service cases also cover failed-write requeue/retry and shutdown cancellation/reuse. Native Admin uses the same hosted writer; no controlled Admin HTTP enqueue is claimed. Scheduled native retention is disabled; audit reads, retention and billing reconciliation remain separate. |
| Async tasks / `IAsyncTaskRuntimeStore` | `AsyncTaskRuntimePersistenceParityTests` | Persistence probe exercises CRUD, claim/provider phases and archival; Gateway probe reads and cancels a durable task across hosts. Native lease recovery/retry and video/provider workloads require additional process tests. |
| Media ownership/quota / `IMediaRuntimeStore` | `MediaRuntimePersistenceParityTests` | Persistence probe covers ownership, tombstones, aggregates, and quota reads; Gateway probe generates, stores, reads, and downloads an identical PNG through S3-compatible storage. Assigned/unlimited-policy cases have shared-contract evidence. Admin cleanup/reconciliation remains EF. |
| Gateway operational aggregates / `IGatewayMetricsStore` | `GatewayMetricsPersistenceParityTests` | Gateway process gate checks seeded $0.40/min cost rate, 0.20s mean latency and one active mapping on an elected metrics host, plus two pending notification tasks on each Gateway. [Linux run 38121134709](https://github.com/nickna/Conduit/actions/runs/38121134709) passes these assertions. Broader Admin reporting remains separate. |
| Function catalog / `IFunctionDiscoveryRuntimeStore` | `FunctionDiscoveryPersistenceParityTests` | Published persistence probe verifies scalar enabled-row/filter/schema reads. [Linux run 38124571584](https://github.com/nickna/Conduit/actions/runs/38124571584) also proves authenticated function catalog/parameter HTTP on both Gateways, including filters, private-field omission and error contracts. This is metadata discovery, not LLM tool discovery or execution. |
| Function execution configuration / `IFunctionExecutionConfigurationStore` | `FunctionExecutionConfigurationPersistenceParityTests` | [Linux run 38122760993](https://github.com/nickna/Conduit/actions/runs/38122760993) proves scalar configuration and opaque credential reads, ordering and pool cancellation/reuse. It predates the SQL-in-flight cancellation additions described below. Native Gateway selects the store for client construction, HTTP preflight and Core tool/route configuration reads; paired managed tests and host composition cover those selections. [Linux run 38128502143](https://github.com/nickna/Conduit/actions/runs/38128502143) also passes native execution authentication/configuration/required-parameter preflight on both Gateways. The published Exa process job linked below proves configured search dispatch and Core LLM tool discovery on both Gateways; other provider workflows remain separate. |
| Function pricing / `IFunctionPricingRuntimeStore` | `FunctionPricingPersistenceParityTests` | [Linux run 38124571584](https://github.com/nickna/Conduit/actions/runs/38124571584) proves canonical mapping/cost eligibility reads. Native Gateway selects the cached configuration-pricing adapter; JIT/Admin retain cost management. Selected Exa function accounting is qualified by the published process job linked below; Admin CRUD and other providers remain separate. |
| Scalar function execution writes / `IFunctionExecutionRuntimeWriter` | `FunctionExecutionPersistenceParityTests` | [Linux run 38126684370](https://github.com/nickna/Conduit/actions/runs/38126684370) proves native scalar create/update, persisted mappings and optimistic concurrency in the standalone probe. Native Gateway selects the writer in its prepared execution service; The published Exa process job linked below verifies execution writes and settled charges over HTTP. Leases, audits and workers are outside this port. |
| Function execution reads / `IFunctionExecutionRuntimeReader` | `FunctionExecutionPersistenceParityTests` | The successful publish/process job in [Linux run 38128502143](https://github.com/nickna/Conduit/actions/runs/38128502143) includes the native persistence probe's 39 scalar fields, included configuration/inverse graph and 105-row virtual-key list, plus the shared PostgreSQL parity suite. Native Gateway selects the reader in its prepared execution service after that proof; the published Exa process job linked below verifies opposite-host execution-by-ID reads and ownership. Execution-list HTTP remains separate. |
| Stateless inline MCP execution | `NativeMcpFunctionProviderTests`, shared function configuration/pricing/execution contracts | [Linux run 38147134135](https://github.com/nickna/Conduit/actions/runs/38147134135) passes actual configured MCP execution on both native Gateways, production-protected scoped credentials, modern request metadata, structured/Unicode results, all 24 persisted fields, opposite-host ownership and exact configured pricing/accounting. Stateful sessions, agentic MCP and broader server interoperability remain separate. |

The #1666 EF recovery additions provide ten deterministic PostgreSQL rows in
`VirtualKeyRuntimeRetryPersistenceTests` and
`VirtualKeyRuntimeUniqueConflictPersistenceTests`. A real `40001` after the
balance update and tracked ledger creation proves provider-strategy retry,
fresh attempt scalars/transactions, rollback and one committed debit. A real
`23505` proves disposal before fresh-context recovery, current-state return,
six conflicting replay identities and cancellation at recovery-query dispatch.
The tests also verify exact ledger/totals and single-slot connection reuse.
All 26 selected recovery and existing virtual-key/legacy replay cases passed
locally without skips. The NativeAOT workflow explicitly includes the two new
classes in its managed shared-contract gate; this is not EF native execution.
Immediate-retry cancellation is covered here. Cancellation during nonzero retry
backoff remains a separate defect, [#1703](https://github.com/nickna/Conduit/issues/1703).

The #1665 configuration-read additions retain the four original parity rows and
add six `SqlInFlightCancellationReleasesTheSingleConnectionSlot` rows: EF and
typed Npgsql implementations of `GetConfigurationAsync`, `GetConfigurationsAsync`
and `ListCredentialsForProviderAsync`. Each row requires its own real PostgreSQL
schema, an observed exact reader PID/relation lock wait behind its unpooled
blocker, bounded cancellation/reaping and single-slot recovery. Pre-cancelled
null/empty ID lists retain their no-query fast return.

`FunctionExecutionConfigurationPersistenceProbe` adds the three typed counterparts
to the standalone persistence driver, within its caller-owned GUID schema. It
also checks caller-token cancellation and obtains the sole pooled slot for
`SELECT 1` while the table lock is still held, then recovers all three reads after
unlock. Running that driver with `dotnet` is managed evidence. Executing these
additions as native code requires a fresh published Linux executable and its own
process/cleanup receipt; the historical process links above do not supply it.
Neither form executes the EF reference implementation natively or completes the
full repository-suite qualification.

Local verification on 2026-10-16 passed all ten managed PostgreSQL 16 parity rows
with zero skips. A fresh Linux x64 NativeAOT publish with `ConduitAotAudit=true`
had zero warnings. Its standalone executable
(`fe2e22ba6bcecba30d5ab9a2da56fb544cda1f553f63f9943f05fe6b8fe79e79`)
ran the full persistence driver, including the three typed SQL cancellation
cases, and exited zero with empty stderr. Independent before/after observations
found zero native-driver and configuration-test schemas. The retained execution
receipt SHA-256 is
`1f1fc45e62b10b65077fdcc75463cfeb9e002257d6302fc3dce49a581fe5b9e3`.

The published persistence probe is `Tests/ConduitLLM.PersistenceAotTests/Program.cs`,
launched by `scripts/aot/persistence-native-smoke.ps1`. Its isolated schema also
checks JSONB, UUID, decimal, UTC timestamps, rollback, optimistic concurrency,
connection retry, and a database-generated `40001` retry. These checks do not
represent the complete remaining EF workload.

`scripts/aot/gateway-native-parity.ps1` launches native Admin, two native Gateways,
and the native client/provider stub. Its CI storage target is digest-pinned S3Mock;
the script's default health path still allows a local MinIO target. Do not infer
MinIO-specific or vendor-wide compatibility from the CI storage result.

## Remaining query owners and required work

The listed domains are an inventory of excluded JIT workloads and future native
expansion work. [ADR 0009](../decisions/0009-staged-native-persistence-refactor.md)
permits proving selected operations incrementally without an immediate all-domain
rewrite. That staged evidence does not complete #1373's full native repository-suite
scope. Each newly selected operation still requires the parity and published-process
evidence below; excluded domains retain their JIT behavior and cannot be advertised
as native support. Final committed-source CI, acceptance reconciliation and all
promotion criteria remain required.

The owner paths below are relative to the repository root. Database/runtime owns
the persistence contracts; the named feature maintainers own their process fixtures.
All rows remain open unless their selected operations are explicitly covered above.
Unit 77 omits five Gateway and seven Admin EF-backed hosted workers from native
registrations, while retaining the JIT profile. These are explicit exclusions,
not successful execution of the unextracted operations. Gateway legacy request-log
and function-audit services are omitted entirely, as is optional pricing audit in
both hosts: retaining its queue would still permit an EF write at 100 events.
Native Admin also omits the pricing endpoint handler and runtime routes. Media
cleanup/guard singletons and manual deletion, approval, status and reconciliation
services remain registered for dependency compatibility but unsupported natively.
The matrices list every omission and the services that remain supported.

| Workload / accountable feature | Current owners | Next completion evidence |
|---|---|---|
| Gateway legacy audit queues, reporting and retention | Configuration `Services/BatchAuditServiceBase.cs`, `FunctionCallAuditService.cs`, `PricingAuditService.cs`, `RequestLogService.cs`; Gateway `Extensions/AuditServicesExtensions.cs` | The legacy services/workers are omitted natively. Extract complete append/read/retention operations, prove JSONB mapping and bulk deletion parity, then process-test before restoring them. Typed request-log and billing-audit writes remain selected independently. |
| Gateway billing reconciliation | Gateway `Services/BillingReconciliationService.cs` | Typed window/checkpoint operations; parity for finalized charges, ledger/provider evidence, thresholds and replay; native checkpoint and metric assertions. Settled chat spend does not prove reconciliation. |
| Discovery model catalog and warming | Gateway `Endpoints/DiscoveryEndpoints.cs`, `Services/DiscoveryCacheLoader.cs`, `DiscoveryCacheWarmingService.cs`; Core `Services/DiscoveryModelProjector.cs` | Extract the `/v1/conduit/discovery/models` filtered/pricing projection and prove its cache warmer from native processes. `/v1/models` and the separately proven function catalog are different surfaces. |
| Provider tool/function workload | Core `Services/FunctionDiscoveryService.cs`; Gateway `Endpoints/ChatEndpoints.Validation.cs`, `Services/ToolCostCalculationService.cs`; Configuration function repositories | Configuration, credential, pricing and scalar execution ports are proven above; Core discovery and execution-service selection have paired managed/composition tests. Native Chat selects the scalar preflight factory. Published Exa and stateless inline MCP proofs cover their selected execution and accounting paths. Provider-hosted tool-cost reads, stateful/agentic MCP, list HTTP, agentic SSE, parallel/dependent calls, audit and broader retry/error contracts remain unqualified. |
| Admin provider/configuration management | Admin `Services/AdminProviderSyncService.cs`, `OpenRouterDriftDetectionService.cs`; `Endpoints/ConfigurationEndpoints.cs`, `ProviderToolsEndpoints.cs`; Configuration `ProviderService.cs`, `Services/ProviderKeyCredentialValidator.cs`, `ModelCatalogs/BundledModelCatalogImporter.cs` | Fixed management/drift/import operations and native API/background fixtures, including transactions and credential business rules. Extracted provider repositories do not automatically migrate their direct DbContext callers. |
| Admin models and costs | Admin `Endpoints/ModelEndpoints.cs`, `ModelCostsEndpoints.cs`; `Services/AdminModelCostService.cs`, `ModelCostCanaryHostedService.cs`, `PricingConfigurationAuditHostedService.cs`; Configuration `Repositories/ModelRepository.cs`, `ModelCostRepository.cs`, `ModelAuthorRepository.cs`, `ModelSeriesRepository.cs`, `ModelProviderMappingRepository.cs` | Native CRUD, dynamic filter/include graphs, pricing promotion/canary, and model-catalog behavior; use complete named operations with persisted-contract parity. |
| Admin virtual keys, refunds and access policy | Admin `Services/AdminVirtualKeyService.cs`, `.Usage.cs`, `.ModelRateLimits.cs`, `RefundService.cs`; `Endpoints/VirtualKeyGroupsEndpoints.cs`; `Filters/VersionedResourceEndpointFilter.cs`; Configuration virtual-key/group/transaction/spend-history repositories | Native management, maintenance, refund/idempotency, conditional update/version handling and cross-host cache invalidation. Runtime authentication/debits do not cover the broad management graph. |
| Admin media retention, cleanup and reconciliation | Admin `Endpoints/MediaEndpoints.cs`, `MediaRetentionEndpoints.cs`; `Services/AdminMediaService.cs`, `MediaCleanupService.cs`, `MediaCleanupApprovalService.cs`, `MediaCleanupStatusService.cs`, `MediaReconciliationService.cs`, `MediaStorageConfigurationGuard.cs`; Configuration `Repositories/MediaRecordRepository.cs` | Native pruning/approval/purge, budget/rate limits, storage mutation and recovery/retention fixtures; preserve deletion/tombstone and quota semantics. Image upload/download does not prove these jobs. |
| Admin analytics, health and prompt-cache reporting | Admin `Endpoints/HealthMonitoringEndpoints.cs`, `PromptCachingEndpoints.cs`; `Services/AdminSystemInfoService.cs`; Configuration `Repositories/RequestLogRepository.cs`, `NotificationRepository.cs` | Native range/group/interval aggregate and database/system diagnostics fixtures; reporting services can call these query owners indirectly. |
| Generic/reference EF implementations | Configuration `Repositories/RepositoryBase.cs`, `AsyncTaskRepository.cs`, `Ef*RuntimeStore.cs`, `EfGatewayMetricsStore.cs`; `Services/BatchSpendUpdateService.cs` | Retain JIT reference behavior while proving every selected native operation bypasses EF; do not count supported EF/Npgsql slices as unimplemented merely because their reference code still has queries. Legacy/non-Gateway spend constructors retain an EF fallback. |
| Startup and fallback branches | Configuration `Data/MigrationWaitService.cs`; Core `Services/CoordinatedConnectionPoolWarmer.cs`; Providers `DatabaseAwareLLMClientFactory.cs` | Native `Wait` readiness and DB-failure/recovery tests; inspect each EF fallback's registration. Provider route-policy reads already use the runtime store when supplied, so the factory's EF fallback is not itself proof that ordinary native chat re-enters EF. |

The 25 allowlisted unsupported query-method exceptions are still current: 15 are
in Admin, three in `MediaRecordRepository`, and seven in `RequestLogRepository`.
`Tests/ConduitLLM.Tests/Architecture/AotDependencyBoundaryTests.cs` names every method
and rejects broad or changed exceptions. Generated compiled-model code has a
separate bounded set of 22 `IL3050` exceptions. Both sets are warning-management
evidence, not runtime support evidence.

## Readiness checkpoint and remaining runtime gaps

The native process gate now uses `CONDUIT_MIGRATION_MODE=Wait`. The typed Npgsql
schema-version probe reads the migration history directly, treating PostgreSQL's
missing-table code as an uninitialized schema; real-PostgreSQL tests cover that
case. Release-lock and mandatory Wolverine-storage preflights defer bus startup
until the standalone migrator has completed its work.

[Linux run 38121134709](https://github.com/nickna/Conduit/actions/runs/38121134709)
passed published-process tests for empty/prior/current/future schema states,
PostgreSQL/Redis outages and same-process readiness recovery with liveness200.
The runtime capability contract therefore includes `readiness-and-database-health`.
This does not prove the remaining EF-backed background-worker or Admin workload.

Both native capability endpoints now report
`persistence_status: typed-npgsql-selected-runtime-slices`. This records selected
runtime persistence, not query support for every registered management service.
The Gateway includes `function-metadata-discovery`
for the authenticated catalog and parameter routes, `inline-exa-search`,
`inline-stateless-mcp-tools`, `nonstreaming-configured-exa-agentic-chat` and
`billing-audit-batch-writes`. The selected search execution,
opposite-host execution-by-ID ownership, configuration preflight and four complete
LLM/function loops passed the
[published process job at d13e7deb](https://github.com/nickna/Conduit/actions/runs/38137532017/job/114465334583).
It explicitly excludes
`signalr-messagepack`, `ef-core-query-data-plane`,
`redis-virtual-key-authentication-cache`,
`inline-functions-beyond-exa-search-and-stateless-mcp`,
`agentic-workflows-beyond-nonstreaming-configured-exa-chat`,
`function-execution-list-http`, `function-audit-and-retention`,
`billing-reconciliation`, `request-log-reporting-and-retention`,
`pricing-rule-audit-and-retention`, `model-discovery-catalog-and-warming`,
`billing-audit-reads-and-retention` and `provider-hosted-tool-pricing`.
The five unextracted Gateway workers and seven Admin workers are omitted in
the unit 77 native profile. Its local published host gate verifies exact metadata
from Admin and both Gateways; actual native-profile composition verifies omitted
and retained worker identities alongside the full JIT worker graph.
All eight JSON hubs,
Redis admission/method limits/backplane, ephemeral keys and function metadata
are covered by the native process probe; authentication-cache invalidation,
full Admin management, provider families beyond the mock-compatible chat/image
subset, video/audio, stateful/agentic MCP, agentic SSE, provider-hosted tool pricing
and parallel/dependent calls remain distinct work.
[Linux run 38147134135](https://github.com/nickna/Conduit/actions/runs/38147134135)
passes stateless inline MCP and authenticated Admin settings CRUD/validation/
pagination with cache acknowledgements. The later unit 96 local native process
proof separately checks healthy cross-host cache consumption through authenticated
Gateway validation, stale direct-SQL values, explicit reload and default restoration;
see the feature matrices for its source/generation limits and remaining retry/retention
quirks. [Linux run 38149774358](https://github.com/nickna/Conduit/actions/runs/38149774358)
passes typed billing-audit timers and graceful final drains on both Gateways.
Admin selects that writer without a controlled HTTP enqueue claim. Admin's IP
CRUD/source-socket proof passes locally and in the public
[unit 76 run](https://github.com/nickna/Conduit/actions/runs/38152552344).
That run passes all three jobs, including 219 persistence tests with no skips,
zero first-party linker diagnostics and both packaged native image checks.
The new [Admin matrix](native-aot-admin-feature-matrix.md) lists its
exact included/excluded labels and retained unsupported management services.

## Exit criteria

1. Every operation required by a service's proposed native support contract has a
   supported EF path or a fixed typed backend with shared PostgreSQL contracts and
   published native process evidence. EF production-support status remains a
   blocker for operations that still depend on its experimental native query path;
   it is not a prerequisite for independently verified typed Npgsql slices.
2. Native Admin and Gateway run every advertised API/background workload, including
   its startup/readiness, transaction, concurrency, retry and failure cases, without
   silent unsupported fallbacks. Omitted workers and excluded retention/reporting/
   reconciliation remain outside the first-image contract; removing exclusions
   requires their own complete evidence.
3. Public, persisted, message, and cache fixtures remain compatible; JIT rollback
   retains the same schema and behavior. Schema mutation remains the standalone
   JIT `ConduitLLM.Migrator` concern.
4. Record generated size, publish duration, startup, throughput, P95/P99 latency,
   and memory against digest-pinned JIT images. Complete the operational evidence
   and ordered soaks in [native-aot-canary.md](native-aot-canary.md).
5. Approve a new promotion ADR before production-native use. This inventory and
   ADR 0009 do not supersede ADR 0006's production-JIT decision.
