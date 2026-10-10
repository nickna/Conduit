# ADR 0009: Refactor persistence through operation-specific dual backends

- Status: Accepted
- Date: 2026-08-27
- Decision owners: database/runtime maintainers
- Related: #1368, #1372, #1373, #1374
- Extends: ADR 0006

## Context

ADR 0006 correctly keeps production database work on JIT while EF Core's NativeAOT
query path remains experimental. The subsequent linker audit reduced the remaining
first-party diagnostics to 147 unique sites: 45 query-expression diagnostics and 102
diagnostics in the generated EF model. Making individual LINQ expressions static can
reduce the first group, but it cannot make the EF runtime a production-supported
NativeAOT dependency.

The persistence inventory contains 37 direct-query files and 344 query operators.
A big-bang replacement would duplicate model conversions, transaction behavior,
concurrency handling, retries, and PostgreSQL-specific semantics before parity could
be demonstrated. At the same time, retaining caller-supplied expressions and direct
service-level DbContext queries prevents a second implementation from being introduced
incrementally.

The existing repository APIs return materialized values rather than IQueryable. That
is a useful seam, but their assembly ownership and generic query helpers still bind the
surface to the EF-oriented Configuration project.

## Decision

Conduit will use a staged strangler refactor for runtime persistence:

1. Move one domain slice at a time into `ConduitLLM.Persistence.Abstractions`.
   Contracts expose complete named operations and materialized values. They do not
   accept IQueryable, expression trees, ordering delegates, or DbContext types.
2. Keep an EF Core implementation as the production default and behavioral reference
   while JIT images remain the supported rollback lane.
3. Add a typed-Npgsql implementation only for an extracted slice. Every alternate
   implementation must run the same PostgreSQL contract suite and a published
   NativeAOT process probe before it can be selected by a native host.
4. Keep EF migrations and schema ownership in the standalone JIT migrator. Runtime
   implementations share the same tables and persisted contracts; they do not own
   competing migration systems.
5. Migrate slices in dependency order: startup/authentication/configuration reads,
   billing and transactional writes, task leasing/media state, then reporting and
   retention workloads. Reporting may remain JIT until its aggregates have an explicit
   typed operation and measured native implementation.
6. Remove EF from a native service graph only after all operations required by that
   service's documented feature matrix have alternate implementations and native
   process coverage. This ADR does not authorize NativeAOT production promotion.

The first vertical slice is global settings. It is startup-critical, has a small fixed
query surface, and exercises ordered reads, key/id lookups, inserts, updates, upserts,
and deletes without requiring dynamic query composition.

The second vertical slice is IP-filter access policy. It is read on the request path,
has fixed global and virtual-key scopes, and extends the shared contract suite with
audit metadata, nullable columns, foreign-key scoping, and optimistic concurrency.
Provider and provider-key credentials are kept together as the next slice because
their primary-key selection and encryption behavior form one transactional boundary.

That provider/credential slice is now extracted as a single boundary. Provider reads
hydrate credential graphs, JSONB settings retain generated metadata, and the typed-
Npgsql credential writes use one provider-row lock order. The shared PostgreSQL
contract deliberately forces a failed disabled-key promotion and proves the old
primary survives the transaction rollback.

Native Gateway builds now select the typed-Npgsql global-setting, IP-filter, provider,
and provider-credential repositories after the normal service graph is assembled. JIT
Gateway and all Admin builds retain EF. Replacing the descriptors as one host-level set
ensures request authentication, security policy, and provider credential resolution do
not silently fall back to an earlier EF registration.

The next slice is the Gateway virtual-key runtime boundary. It hydrates a complete key
and group snapshot, preserves pending-spend balance checks, and owns atomic group balance
plus ledger writes. Native Gateway builds select its typed-Npgsql implementation only for
`IVirtualKeyRuntimeService`; JIT builds and Admin management CRUD retain the existing EF
services. This is the first service-host selection of an alternate adapter, but the
native feature matrix remains excluded until downstream provider, logging, task, and media
dependencies have equivalent boundaries and process coverage.

Model routing follows the same request-time pattern without moving the broad Admin
repository. `IModelProviderMappingRuntimeStore` materializes a complete route graph:
mapping and provider configuration, canonical and provider-specific capabilities,
series metadata, optional costs, deterministic paging, and per-alias route policy. Its
EF reference and fixed-query typed-Npgsql adapters share a real-PostgreSQL parity test,
and the typed adapter runs inside the published persistence NativeAOT probe. This slice
first lands independently of host selection. Native Gateway then selects a read-only
compatibility adapter over the runtime store and resolves persisted route policy through
the same boundary. Native request billing also resolves model costs by fixed ID or
provider identifier through this store. JIT and Admin retain the full EF repository and
model-cost management service. Authenticated model discovery and provider pricing are
covered by the native process gate.

Request accounting is the next downstream boundary. Request-time middleware now
depends on `IRequestLogRuntimeWriter`, which maps the existing DTO onto a complete,
backend-neutral `RequestLogRuntimeRecord`. JIT Gateway retains the batched EF writer;
native Gateway selects an immediate typed-Npgsql writer. The same change routes
`BatchSpendUpdateService` key/group lookup, atomic debit, idempotency, and cache-
invalidation hash lookup through `IVirtualKeyRuntimeStore` when the Gateway supplies
one. EF remains the fallback for legacy constructors and non-Gateway hosts. The write
contract has real-PostgreSQL EF/Npgsql parity and published NativeAOT process coverage.
Reporting, retention, and management queries over request logs remain JIT-only.

The next promotion checkpoint proves the ordinary provider data plane rather than adding
another broad repository. A separate native OpenAI-compatible provider process validates
resolved credentials and exercises non-stream JSON, SSE chunks and final usage, translated
provider errors, and downstream cancellation propagation. Successful requests are priced
through the fixed-shape model-cost lookup, persisted through the request-log writer, and
settled through the typed virtual-key store. Exact token, cost, balance, lifetime-spend,
and ledger values are asserted in real PostgreSQL. These paths are therefore included in
the native capability contract.

Authenticated JSON SignalR is the next downstream checkpoint. Real clients connect to
all eight hub routes across two published native Gateways. The process gate proves
typed-store authentication and initial virtual-key status, atomic Redis admission at a
cross-host connection ceiling, method RPM/RPD counters, webhook subscription state,
backplane delivery, and the public hub's Redis ephemeral-key flow. Hub wire payloads
are named source-generated contracts, and connection-filter rejections propagate to
the transport.

Async-task persistence is the next extracted state boundary. `IAsyncTaskRuntimeStore`
defines backend-neutral task snapshots plus the exact lifecycle, claim, provider-phase,
lease, indeterminate-reconciliation, and retention operations used by Gateway. JIT uses
an EF adapter over the established repository, as do non-Gateway hosts; native Gateway
selects fixed SQL through typed Npgsql. The two implementations share a real-PostgreSQL
behavioral contract, the typed implementation runs in the published persistence probe,
and the two-host process gate reads and cancels a task across native Gateways before
verifying the durable row.

Media-record persistence is the next operation-specific boundary. `IMediaRuntimeStore`
covers durable ownership, storage-key lookup, access statistics, active storage
aggregates, and quota snapshots without exposing EF navigation graphs. JIT and Admin
use the EF reference adapter; native Gateway selects fixed SQL through typed Npgsql.
Both implementations pass the same real-PostgreSQL contract, including tombstones,
group filtering, and assigned unlimited-policy semantics. The published persistence
probe exercises the typed implementation, and the two-Gateway process gate carries a
provider-generated PNG through quota enforcement, MinIO storage, durable ownership,
cross-host metadata, and authenticated download. S3-compatible media workflows are
therefore included in the native capability contract.

Gateway operational metrics are the final extracted reporting slice in this phase.
`IGatewayMetricsStore` exposes only the bounded model usage, provider cost, active
entity, task queue, generation-duration, and top-key-spend aggregates consumed by the
Prometheus collectors. JIT Gateway uses an EF reference adapter; native Gateway uses
fixed typed-Npgsql queries. Both implementations pass the same real-PostgreSQL
aggregate contract. Broader Admin reporting and retention queries remain on EF and
outside ADR 0006's supported NativeAOT data plane.

## Options considered

1. **Wait for production-supported EF NativeAOT.** Rejected as the only plan: it
   preserves the safe JIT lane but makes no progress on application-owned dynamic
   query boundaries.
2. **Rewrite every repository with typed Npgsql now.** Rejected: the 37-file/344-site
   migration is too broad to prove parity or roll back safely as one change.
3. **Refactor all EF queries for precompilation first.** Rejected as the primary path:
   it cannot remove the generated-model diagnostics or change upstream support status.
4. **Extract operation-specific contracts and add alternate backends by slice.**
   Accepted: it preserves production behavior, creates measurable checkpoints, and
   permits EF removal from a future native graph without requiring an all-at-once
   application rewrite.

## Verification and promotion gates

Each slice must provide:

- an architecture test proving its abstraction assembly is free of EF, Npgsql,
  Functions, and service implementation references;
- a single behavioral contract exercised against EF and the alternate backend on real
  PostgreSQL;
- a published NativeAOT process test that executes the alternate implementation;
- unchanged JIT unit/integration behavior and public/persisted contracts;
- an explicit production registration change and rollback plan before traffic uses the
  alternate backend.

The extracted global-settings, IP-filter, provider/credential, virtual-key runtime,
model-routing, request-accounting, async-task, media, and Gateway-metrics slices are
selected only by a native Gateway build. Model routing is selected behind a read-only
compatibility adapter, while request-log writes use a narrow runtime writer and
authenticated hubs share the virtual-key runtime snapshot. JIT Gateway and Admin
registrations retain their existing EF-backed services.
Rollback is therefore the JIT artifact, whose request-time and management contracts
continue to resolve to the legacy services.

## Consequences

- Persistence migration becomes a sequence of reviewable vertical slices rather than
  a package replacement.
- During migration, EF and typed-Npgsql implementations coexist and must share contract
  tests. This adds short-term code but bounds semantic drift.
- Generic repository convenience APIs may remain for unextracted slices, but new
  consumers must not add expression-based operations to them.
- ADR 0006's production-JIT constraint, canary requirements, and forward-only migration
  rollback policy remain in force.
