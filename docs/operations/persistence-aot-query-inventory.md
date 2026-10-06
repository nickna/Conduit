# NativeAOT persistence query inventory

Audit date: 2026-08-11. Owner: database/runtime maintainers. Scope: Gateway,
Admin, `ConduitLLM.Configuration`, and `ConduitLLM.Functions`.

The audit found 37 first-party files that directly compose EF queries and 344
query-operator occurrences across 24 repository implementations. Repository
interfaces return materialized values (`Task<T>`, lists, pages, or scalars); none
expose `IQueryable`. An architecture test now protects that boundary.

## Query-shape classification

| Shape | Representative owners | Native status / action |
|---|---|---|
| Fixed key lookup and complete predicate | `GlobalSettingRepository`, `IpFilterRepository`, `ProviderRepository` | Candidate for EF query precompilation after EF NativeAOT is production-supported. |
| Fixed include/order graphs | `ModelRepository`, `VirtualKeyRepository`, `ModelProviderMappingRepository` | Must be enumerated individually; generated code size and split-query behavior require measurement. |
| Caller-composed expression/order delegates | `RepositoryBase.GetAllAsync`, `CredentialValidatorBase`, `BatchAuditServiceBase` | Unsupported risk: expression and `IQueryable` composition occurs at runtime. Replace with named typed operations before native adoption. |
| Conditional filters/includes | `ModelCostRepository`, `FunctionCredentialRepository`, media cleanup services | Unsupported risk: query shape varies by request. Rewrite as complete named expressions or typed SQL. |
| Aggregation/reporting projections | `RequestLogRepository`, `HealthMonitoringEndpoints` | High translation risk (`GroupBy`, intervals, dynamic ranges). Keep JIT until native process fixtures cover every projection. |
| Raw PostgreSQL SQL | `RequestLogRepository`, `BundledModelCatalogImporter` | Provider-specific but statically visible; preserve parameterization and validate independently. |
| Migration/model inspection | standalone `ConduitLLM.Migrator` only | Explicitly excluded from both web-service runtime paths. |

## PostgreSQL mappings requiring parity

- JSONB dictionaries and audit payloads, including custom `ValueConverter` and
  `ValueComparer` logic in `ProviderEntityConfiguration`.
- Enum-to-string and enum-to-int conversions.
- UUID, UTC `timestamptz`, bounded decimals used by pricing/billing, nullable
  columns, unique indexes, and optimistic concurrency predicates.
- Transactions and advisory locks, execution-strategy retries, raw SQL, and
  Wolverine's separate PostgreSQL schemas.

`scripts/aot/persistence-native-smoke.ps1` publishes and launches a separate
NativeAOT process against `DATABASE_URL`. It covers typed Npgsql reads/writes,
JSONB/UUID/decimal/timestamp mappings, rollback, optimistic concurrency, connection
retry, and a database-generated `40001` retry. This proves the test harness and a
typed-Npgsql fallback seam; it does not claim parity for the 37-file EF workload.

## Exit criteria for revisiting the decision

1. EF Core and Npgsql document the selected NativeAOT path as production-supported.
2. Every dynamic row above is replaced or explicitly supported and tested.
3. A published native Admin and Gateway process passes the complete repository suite
   against PostgreSQL, including failure/retry fixtures.
4. Generated size, publish duration, startup, throughput, P95/P99 latency, and memory
   meet or improve the JIT baseline without contract drift.
