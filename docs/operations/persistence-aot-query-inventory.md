# NativeAOT persistence query inventory

Revalidated: 2026-10-09 against `master` commit `e71f8d61` (merged #1489).
Owner: database/runtime maintainers. Related: #1368, #1373, #1374,
[ADR 0006](../decisions/0006-native-aot-persistence.md), and
[ADR 0009](../decisions/0009-staged-native-persistence-refactor.md).

Production Gateway and Admin remain JIT. The native Gateway selects typed Npgsql
for nine extracted persistence slices; unextracted EF operations still exist in
both service graphs. Publishing successfully or emitting zero first-party warnings
does not establish that those operations execute natively.

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
| ConduitLLM.Gateway | 4 | 47 |
| ConduitLLM.Configuration | 36 | 912 |
| ConduitLLM.Core | 2 | 11 |
| ConduitLLM.Providers | 1 | 12 |
| Total | 69 | 1,334 |

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

## Extracted slices and actual evidence

Contracts live in `Shared/ConduitLLM.Persistence.Abstractions`; typed implementations
live in `Shared/ConduitLLM.Persistence.Npgsql`. Native Gateway selection is centralized
in `Services/ConduitLLM.Gateway/Extensions/NativeRuntimePersistenceExtensions.cs`.
JIT Gateway and Admin retain EF-backed implementations. The nine shared tests are
under `Tests/ConduitLLM.IntegrationTests/Tests`; they run against real PostgreSQL in
the NativeAOT workflow.

| Slice / contract | Shared EF/Npgsql contract test | Published native evidence and limit |
|---|---|---|
| Global settings / `IGlobalSettingRepository` | `GlobalSettingRepositoryParityTests` | Persistence probe exercises ordered reads, key/id lookup, writes, upsert, and delete. |
| IP-filter policy / `IIpFilterRepository` | `IpFilterRepositoryParityTests` | Persistence probe exercises scoped CRUD and UTC/null columns; Gateway probe authenticates under global/per-key default-deny policy. Optimistic concurrency/FK cases have shared-contract evidence. |
| Providers and credentials / `IProviderRepository`, `IProviderKeyCredentialRepository` | `ProviderPersistenceParityTests` | Persistence probe exercises JSONB, credential graphs, primary rotation, and cascade; Gateway provider process checks resolved credentials. Failed-rotation rollback has shared-contract evidence. Admin credential validation remains a separate EF consumer. |
| Virtual-key runtime / `IVirtualKeyRuntimeStore` | `VirtualKeyRuntimePersistenceParityTests` | Persistence probe covers hydrated snapshots, concurrent/idempotent ledger debits; Gateway probe checks exact settled request costs, balances, and lifetime spend. Management CRUD remains EF. |
| Model routing / `IModelProviderMappingRuntimeStore` | `ModelProviderMappingRuntimePersistenceParityTests` | Persistence probe covers route graphs/policy; Gateway probe exercises `/v1/models`, model retrieval/metadata, and provider pricing. This does not cover the separate `/v1/discovery` EF projections. |
| Request-log writes / `IRequestLogRuntimeStore` | `RequestLogRuntimePersistenceParityTests` | Persistence probe checks persisted accounting mappings; Gateway probe checks exact token/cost rows. Request-log reporting and retention remain EF. |
| Async tasks / `IAsyncTaskRuntimeStore` | `AsyncTaskRuntimePersistenceParityTests` | Persistence probe exercises CRUD, claim/provider phases and archival; Gateway probe reads and cancels a durable task across hosts. Native lease recovery/retry and video/provider workloads require additional process tests. |
| Media ownership/quota / `IMediaRuntimeStore` | `MediaRuntimePersistenceParityTests` | Persistence probe covers ownership, tombstones, aggregates, and quota reads; Gateway probe generates, stores, reads, and downloads an identical PNG through S3-compatible storage. Assigned/unlimited-policy cases have shared-contract evidence. Admin cleanup/reconciliation remains EF. |
| Gateway operational aggregates / `IGatewayMetricsStore` | `GatewayMetricsPersistenceParityTests` | Native Gateway registers the Npgsql store, but the persistence probe does not exercise it. The Gateway probe only checks `/metrics` succeeds and contains `# HELP`; add assertions on seeded business/task aggregate values before claiming complete native aggregate proof. |

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

The owner paths below are relative to the repository root. Database/runtime owns
the persistence contracts; the named feature maintainers own their process fixtures.
All rows remain open unless their selected operations are explicitly covered above.

| Workload / accountable feature | Current owners | Next completion evidence |
|---|---|---|
| Gateway audit writes and retention | Configuration `Services/BatchAuditServiceBase.cs`, `FunctionCallAuditService.cs`, `PricingAuditService.cs`, `RequestLogService.cs`; Gateway `Extensions/AuditServicesExtensions.cs` | Extract complete append/read/retention operations, prove JSONB mapping and bulk deletion parity, then assert native background jobs complete without EF query failures. The native request-log writer does not replace these jobs. |
| Gateway billing reconciliation | Gateway `Services/BillingReconciliationService.cs` | Typed window/checkpoint operations; parity for finalized charges, ledger/provider evidence, thresholds and replay; native checkpoint and metric assertions. Settled chat spend does not prove reconciliation. |
| Discovery model/function catalog and warming | Gateway `Endpoints/DiscoveryEndpoints.cs`, `Services/DiscoveryCacheLoader.cs`, `DiscoveryCacheWarmingService.cs`; Core `Services/DiscoveryModelProjector.cs` | Extract discovery-specific filtered projections and pricing/function metadata; prove authenticated `/v1/discovery` responses and cache warming from native processes. Existing `/v1/models` proof is a different surface. |
| Provider tool/function workload | Gateway `Services/ToolCostCalculationService.cs`; Configuration `Repositories/FunctionConfigurationRepository.cs`, `FunctionCredentialRepository.cs`, `FunctionExecutionRepository.cs`, `FunctionCostRepository.cs`, `FunctionCostMappingRepository.cs` | Typed tool-cost lookup and fixed function operations; native tool/function dispatch, credentials, audit, accounting, retries and error contracts. Ordinary provider chat/image fixtures do not call these branches. |
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

## Readiness and runtime gaps to close next

The native process script sets `CONDUIT_MIGRATION_MODE=Skip` and tests liveness;
it does not prove `/health/ready` or the schema-readiness transition. The shared
`SchemaVersionProbe` creates an EF context to obtain a raw database connection.
Add native `Wait` tests for missing, stale, and current schema plus database
failure/recovery before removing `readiness-and-database-health` from exclusions.
Its single `SELECT CASE` references `__EFMigrationsHistory` in a subquery while
guarding with `to_regclass`; add a real-PostgreSQL missing-table regression test,
because relation lookup occurs before evaluating that guard.

The current Gateway capability endpoint explicitly excludes
`signalr-messagepack`, `ef-core-query-data-plane`,
`redis-virtual-key-authentication-cache`, and `readiness-and-database-health`.
All eight JSON hubs, Redis admission/method limits/backplane, and ephemeral keys
are covered by the native process probe; virtual-key authentication-cache
invalidation, full Admin management, provider families beyond the mock-compatible
chat/image subset, and video/audio/tool/function flows remain distinct work.

## Exit criteria

1. Every operation required by a service's proposed native support contract has a
   supported EF path or a fixed typed backend with shared PostgreSQL contracts and
   published native process evidence. EF production-support status remains a
   blocker for operations that still depend on its experimental native query path;
   it is not a prerequisite for independently verified typed Npgsql slices.
2. Native Admin and Gateway run the full selected API/background workload, including
   startup/readiness, transactions, concurrency, retries, retention, reconciliation,
   and failure recovery, without silent unsupported fallbacks.
3. Public, persisted, message, and cache fixtures remain compatible; JIT rollback
   retains the same schema and behavior. Schema mutation remains the standalone
   JIT `ConduitLLM.Migrator` concern.
4. Record generated size, publish duration, startup, throughput, P95/P99 latency,
   and memory against digest-pinned JIT images. Complete the operational evidence
   and ordered soaks in [native-aot-canary.md](native-aot-canary.md).
5. Approve a new promotion ADR before production-native use. This inventory and
   ADR 0009 do not supersede ADR 0006's production-JIT decision.
