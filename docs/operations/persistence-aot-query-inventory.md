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

## Incremental extraction status

[ADR 0009](../decisions/0009-staged-native-persistence-refactor.md) adopts operation-specific dual backends rather than a big-bang rewrite.
Global settings, IP-filter access policy, and the provider/provider-credential
consistency boundary are extracted: their models and repository contracts live in
`ConduitLLM.Persistence.Abstractions`, JIT Gateway and Admin registrations remain EF,
and typed-Npgsql implementations are exercised by PostgreSQL contract tests and the
published NativeAOT persistence probe. Native Gateway builds now replace all four
repository descriptors after the ordinary service graph is assembled. The parity
surface includes global/per-key
scoping, audit and nullable mappings, optimistic concurrency, JSONB settings, bounded
decimals, graph hydration, foreign-key cascades, primary-key rotation, transaction
rollback, and deterministic lock ordering in the typed adapter. These slices are not
evidence that the remaining query inventory is native-ready, and their EF reference
implementations remain query owners until a native host can select alternate backends
for every operation it needs.

Phase 5 adds a narrower request-time boundary rather than moving the VirtualKey EF
navigation graph: `IVirtualKeyRuntimeStore` exposes hydrated authentication/rate-limit
snapshots, timestamp touches, and atomic balance/ledger adjustments. Its EF adapter uses
fixed projections and updates, while its typed-Npgsql adapter is covered by the shared
PostgreSQL contract and published native probe. Native Gateway builds select this store;
the JIT Gateway retains its cached/direct EF-backed services. This selection is a bounded
data-plane step; the downstream routing, accounting, task, and media boundaries are
tracked separately below.

The next extracted boundary enumerates Gateway model-routing reads instead of exposing
the broad `ModelProviderMappingRepository` graph. The runtime contract includes alias,
ID, provider, and canonical-model lookups; deterministic pages; provider and canonical
capability metadata; series data; optional pricing; and route policy. A fixed-query EF
reference and typed-Npgsql adapter pass the same real-PostgreSQL contract, and the
typed adapter runs in the published persistence NativeAOT probe. Native Gateway now
selects a read-only compatibility adapter over this store and resolves persisted route
policy through it. Fixed-shape reads by cost ID and provider model identifier supply the
native billing path while JIT and Admin retain the full model-cost management service.
The two-Gateway native process gate proves authenticated model list, retrieval,
capability metadata, provider pricing, and routing to a separately hosted native provider.

Request accounting now has a fixed-shape write boundary as well. The backend-neutral
record covers provider/routing attribution, prompt-cache accounting, token counts,
billing method and timestamps, response metadata, and JSONB request metadata. Its EF
reference and typed-Npgsql implementations write equivalent rows in the shared real-
PostgreSQL contract, and the typed writer runs in the published NativeAOT persistence
probe. Native Gateway request middleware selects that writer. Gateway construction also
supplies `IVirtualKeyRuntimeStore` to the batched spend service, so key/group lookup,
idempotent balance-and-ledger writes, fallback charges, and cache invalidation hashes no
longer re-enter scoped EF services in the native runtime. Request-log reporting,
retention, and management queries are intentionally still owned by the JIT path.

The native provider process gate closes the corresponding transport checkpoint. It
exercises non-stream JSON, SSE chunks and terminal usage, customer-safe provider error
translation, and downstream cancellation propagation. Successful requests must produce
the exact request-log, virtual-key balance, lifetime-spend, and ledger values in real
PostgreSQL. Provider chat transport and accounting are therefore included in the native
capability contract.

The SignalR checkpoint reuses these extracted request-time seams rather than adding a
hub-specific persistence path. Published native clients connect to every hub family;
the virtual-key management hub sends status from the hydrated runtime snapshot; and
Redis supplies distributed admission, method counters, webhook tracking, cross-host
backplane delivery, and ephemeral-key validation for the public video hub. This removes
authenticated JSON hub execution from the native exclusion list.

Async-task request-time operations now use `IAsyncTaskRuntimeStore`. JIT and non-Gateway
hosts use an EF reference adapter, while native Gateway selects typed Npgsql for task
CRUD, claims, provider phases, leases, recovery, retry preparation, and retention. Both
implementations pass one real-PostgreSQL lifecycle contract; the published persistence
probe exercises the typed backend; and the two-Gateway gate proves cross-host read and
cancel with a durable row transition.

Media ownership and quota operations now use `IMediaRuntimeStore`. Its EF reference
adapter preserves the JIT behavior, while native Gateway selects fixed SQL for media
creation, storage-key/owner reads, access updates, active aggregates, and effective
group quota policy. The PostgreSQL parity contract covers tombstones, group filtering,
default-policy fallback, and assigned unlimited policies. The published persistence
probe exercises the Npgsql store, and the process gate carries a provider-generated PNG
through quota enforcement, pinned MinIO, durable ownership, and cross-host authenticated
download. Admin cleanup/reporting remains on the JIT repository surface.

## Exit criteria for revisiting the decision

1. EF Core and Npgsql document the selected NativeAOT path as production-supported.
2. Every dynamic row above is replaced or explicitly supported and tested.
3. A published native Admin and Gateway process passes the complete repository suite
   against PostgreSQL, including failure/retry fixtures.
4. Generated size, publish duration, startup, throughput, P95/P99 latency, and memory
   meet or improve the JIT baseline without contract drift.
