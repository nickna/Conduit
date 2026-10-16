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
Gateway and JIT Admin builds retain EF; native Admin now selects the proven settings
and IP-filter slices described below. Replacing the descriptors as one host-level set
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
model-cost management service. The native process gate covers authenticated alias
listing at `GET /v1/models`, retrieval at `GET /v1/models/{model}`, effective
capability metadata at `GET /v1/conduit/models/{modelId}/metadata`, and request-time
provider model pricing through the typed routing graph. The enriched model catalog
at `GET /v1/conduit/discovery/models` and UI parameters at
`GET /v1/conduit/discovery/models/{model}/parameters` retain their EF/JIT boundary.
Native requests to those two resources return an explicit 501 before resolving the
EF-backed discovery handler; authentication remains required.

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

Function discovery is the next read-only boundary.
`IFunctionDiscoveryRuntimeStore` lists enabled configurations with exact optional
purpose/provider filters and reads an enabled configuration by ID. It materializes
scalar configurations without cost/execution navigation graphs. EF and typed Npgsql
share a real-PostgreSQL contract covering nulls, JSONB, unknown numeric enum values,
detached reads and cancellation with a single connection slot. The standalone
persistence probe includes these operations. The published native persistence check
passed in run 38122760993 before Gateway runtime selection. Gateway's function
catalog and parameter loaders now use this store; their two-host process gate checks
authentication, filters and structured cached payloads. The enriched model catalog
and UI parameters retain their EF/JIT boundary and return 501 in the native profile.
Function CRUD, credential resolution,
cost selection and execution workers still require their own operation boundaries.

Execution configuration and credential reads follow as a separate boundary.
`IFunctionExecutionConfigurationStore` reads scalar configurations by ID or ID set,
including disabled rows, and returns every credential for a provider in the existing
primary/key-name database order. Null/empty ID sets retain their immediate-return
behavior even when cancelled. Credential scope selection and secret protection stay
with the current services; the persistence adapters preserve stored secrets unchanged.
The EF/Npgsql PostgreSQL contract compares legacy credential ordering, disabled and
unknown enum values, scoped/global selection, detached reads and cancellation while
the only connection slot is occupied. The published persistence probe includes this
boundary before runtime service wiring or native selection. Pricing and execution
record writes remain separate slices.

`FunctionClientFactory` now uses this proven store in native Gateway, selected by an
explicit scoped factory registration. Its original public repository constructor is
preserved for JIT Gateway and Admin. Tests compare both paths through actual Exa and
Tavily clients with fake HTTP handlers: credential scope, selected-secret decryption,
URLs, headers, timeouts and fresh reads are unchanged. Credential lookup still uses
the stored provider, while client selection uses the caller's provider; disabled
configurations retain their existing factory behavior. MCP allowlist rejection occurs
before transport creation. Host composition tests run in both JIT and native build
profiles and assert the native factory holds the exact singleton typed store. This
selects only client construction; inline execution, execution reads and broader
function workflows retain separate persistence boundaries.

`IFunctionPricingRuntimeStore` adds uncached scalar pricing selection at a supplied
UTC instant. Active mappings use the canonical `FunctionCostId`; cost eligibility
is applied before priority/effective-date ordering. Provider, purpose and configuration
enabled state do not filter billing selection. The PostgreSQL contract compares the
existing pricing service and both adapters, including invalid high-priority fallback,
canonical/shadow relationship isolation, exact date boundaries, all pricing fields,
detached reads, and cancellation of both pool and observed SQL lock waits. The native
persistence probe exercises the same bounded read before runtime selection. Caching,
Admin management/invalidation and execution writes remain with their existing services.

`StoreBackedFunctionCostService` preserves the configuration-pricing cache cascade
over the scalar pricing port: the existing `FunctionCost:Config:{id}` key, generated
JSON payload, 15-minute memory/one-hour distributed TTLs, uncached nulls, Redis-failure
fallback and caller cancellation behavior. Cached payloads retain the existing direct
return semantics. Tests consume payloads emitted by the legacy service and prove
Admin update invalidation against the shared key. Published Linux run 38124571584
passes the typed pricing-store probe before runtime selection. Native Gateway now
selects this scoped adapter over the singleton typed pricing store; host composition
tests run both build profiles and exercise estimate/actual billing through the resolved
calculator. JIT Gateway and Admin retain the legacy service, and calculator constructors
are unchanged. Cost management methods are outside this adapter. The subsequent
[#1570](https://github.com/nickna/Conduit/issues/1570) correction replaces the inherited
list-only clear with a shared, non-expiring random generation marker. Both services
check the marker before every L1/L2 lookup and pin database results to that generation.
Explicit clears and cost mutations rotate the namespace, including list, ID and
configuration entries across hosts; unreachable payloads retain their 15-minute L1
and one-hour L2 expiration. Without L2, one process-wide generation serves memory
instances. Marker read failures bypass both tiers; failed marker writes make clear
fail visibly. A lost marker initializes a fresh random generation rather than
resurrecting an earlier namespace. An overlapping old read may return its snapshot
but cannot populate the new namespace. This correction changes the cache keys;
the earlier published run above does not establish its hosted native verification.

`IFunctionExecutionRuntimeWriter` adds scalar execution inserts and full snapshot
updates guarded by ID and the caller's original version. It preserves all 24 fields,
empty-ID generation and unchecked successful integer version increments. Navigation
graphs are explicitly outside this new port. At extraction, the adapters preserved
the legacy repository's caller-version increments even after cancellation or a
concurrency miss; that inherited behavior was the subject of
[#1569](https://github.com/nickna/Conduit/issues/1569).
Real PostgreSQL tests compare the actual repository with both adapters, including
three-way concurrent writes, invalid JSON/FK/duplicate failures, occupied pools and
observed SQL lock cancellation. The native persistence probe independently reads all
24 fields and checks scalar writes, CAS, failure atomicity and single-slot recovery.
Published Linux run 38126684370 proved the extracted writer before Gateway selection
under that historical contract; it does not verify the subsequent #1569 correction.
Lease/retry/retention operations and audit writes remain separate boundaries.

The #1569 correction changes all three writers: a false update or an exception,
including cancellation, restores the caller's original `Version`. An in-flight
attempt may temporarily increment it, successful increments retain unchecked
overflow, and existing empty-ID generation remains visible after failure. Regression
checks require repeated stale retries to stay false without changing the durable
winner, one concurrent version-8 winner with version-7 losers, and original versions
after invalid JSON, missing rows, pre-cancellation, occupied-pool cancellation and
observed SQL-wait cancellation. The native probe carries the same changed contract.
These regression changes do not by themselves establish a new hosted native proof.

`IFunctionExecutionRuntimeReader` follows with exact-ID and virtual-key execution
reads. It includes all 24 execution scalars and the 15 configuration scalars through
the required configuration relationship. Lists retain their unbounded size,
`RequestedAt` descending order with unspecified ties, and disabled configurations.
Each no-tracking row owns a separate configuration instance whose `Executions`
collection points back to that exact row; audits and cost mappings remain unloaded.
The real-PostgreSQL suite compares the actual legacy repository and both adapters,
including 105-row lists, detached mutations, graph identity, cancelled pool/observed
SQL waits, and successful reuse. Included graphs retain the scalar writer's rejection
before caller mutation. Published Linux run 38128502143 proves this reader before
runtime selection; no broader lease, audit, worker or Admin support is implied.

`FunctionExecutionService.CreateWithRuntimePersistence` prepares the inline service
to consume the configuration/credential store, scalar writer and included-snapshot
reader together. Native Gateway now selects the named scoped factory and singleton
Npgsql writer/reader after their published persistence proofs. Its original public
repository constructor and JIT/Admin registrations remain unchanged. The path
preserves provider invocation, credential selection,
generated request/cost JSON, optional MCP pricing/tool arguments, cancellation and
post-provider billable failure behavior. Paired tests exercise both constructions;
read and list results retain the existing public return types. Complete host
composition verifies exact port/calculator/client identities and empty legacy
repository fields on native, while JIT/Admin retain their scoped repositories.
The subsequent [#1572](https://github.com/nickna/Conduit/issues/1572) policy treats an
inline result-write concurrency miss as an explicit failed, billable service result.
It retains the actual provider response, usage and incurred cost, adds the execution
ID and a reconciliation diagnostic, and leaves the competing durable snapshot
unchanged. It does not reload and replace the winner or call the provider again.
An error-record concurrency miss is logged and included in any returned failed
result; an original provider exception remains the exception thrown to the caller.
Unkeyed billing remains the caller's responsibility, while keyed claims retain
their existing durable result/settlement flow. The returned snapshot can therefore
differ from the winning stored execution, and operators must reconcile that ID
before dispatching a replacement operation. Earlier hosted proofs below predate
this policy correction.
Published Linux run 38133326127 proves the bounded inline Exa path on two native
Gateways: successful and failed execution, opposite-host status retrieval,
ownership denial and settled accounting. Configured non-streaming Exa agentic chat
is qualified below. Execution-list HTTP, stateful/agentic MCP and function
audit/retention remain separate workload proofs.
The inherited ignored concurrency-update result remains tracked separately in
[#1572](https://github.com/nickna/Conduit/issues/1572).

`FunctionsEndpoints.CreateWithRuntimePersistence` follows with the HTTP handler's
single configuration preflight read. Native Gateway constructs it explicitly over
the already-proven configuration singleton; the original public constructor and JIT
registration remain unchanged. Missing/disabled configuration, authentication,
parameter validation, execution forwarding, direct-cost evidence and response/error
contracts retain their existing behavior. Paired handler tests use production JSON
metadata, and the native two-host probe exercises requests rejected before provider
invocation. The preflight cases alone do not establish native execution or chat
workflow support; the complete selected Exa workload below supplies that proof.
Inherited validation of JSON-bound primitive parameters is preserved and tracked in
[#1573](https://github.com/nickna/Conduit/issues/1573).

`FunctionDiscoveryService.CreateWithRuntimePersistence` reuses the proven scalar
configuration store for Core's tool and routing-map reads. Native Gateway selects
the named factory explicitly; the sole public repository constructor and JIT
registration remain unchanged. Tools retain sorted distinct IDs, optional caching,
the cache loader's cancellation token, minimum configured TTL, missing/disabled
validation, generated schemas and MCP expansion. Routing maps retain their original
ID input and existing validation/error behavior. Paired tests cover both service
constructions, while complete host composition verifies the exact scoped service
and shared store identity. This removes Core discovery's configuration-query
boundary. ChatEndpoints' separate preflight is selected below, and the published
configured Exa workflow proves these Core tool and routing-map reads.

`ChatEndpoints.CreateWithRuntimePersistence` prepares the separate chat preflight
over that same scalar configuration store. The sole public 14-parameter repository
constructor, its guards and optional defaults remain unchanged. The prepared path
forwards the original configuration-ID list and cancellation token without sorting
or deduplication. Validation retains missing IDs before disabled configurations,
then explicit iteration limits, and preserves the existing error/cancellation
contract and accounting order. Empty IDs retain the caller's fast path.
Native Gateway selects the named scoped factory over the proven configuration
singleton; JIT retains the repository constructor. Host composition verifies both
scope identities and all optional billing, usage and error-translation dependencies.
The native HTTP gate checks both chat aliases on both Gateways using an owned key,
including authentication, missing/disabled IDs before iteration limits, unchanged
provider counters and pristine accounting through several spend flush windows.
Guarded preflight does not establish a successful agentic workload.
Paired HTTP tests preserve explicit-null global defaults and the inherited omitted
flag initializers; the latter bypass the documented global defaults and are tracked
in [#1576](https://github.com/nickna/Conduit/issues/1576).

The published process proof carries a non-streaming OpenAI-compatible chat through one
configured Exa call and a final LLM reply on both aliases and both Gateway hosts.
It uses the selected configuration, discovery, credential, pricing and execution
stores without provider-hosted tool usage. Four chats verify generated schemas,
matching tool results, complete persisted executions, opposite-host ownership,
aggregate token usage and settled combined charges. Managed native-profile HTTP
verification passes; the
[published Linux process job at d13e7deb](https://github.com/nickna/Conduit/actions/runs/38137532017/job/114465334583)
also passed this mandatory probe with zero first-party linker diagnostics.
Cost-summary, iteration and chat-log attribution quirks remain unchanged and are
tracked in #1577, #1578 and #1579. Provider-hosted tool pricing, stateful/agentic
MCP, agentic SSE, parallel/dependent calls and audit/retention require separate
workload proofs.

Stateless inline MCP has a separate published proof in
[Linux run 38147134135](https://github.com/nickna/Conduit/actions/runs/38147134135).
Both native Gateways execute configured tools with production-protected scoped
credentials, modern request metadata, generated structured/Unicode results,
all 24 persisted fields, opposite-host ownership and exact configured accounting.
That proof does not qualify stateful sessions, agentic MCP or broader server
interoperability.

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
selected by a native Gateway build. Native Admin selects typed global-setting and
IP-filter repositories, with typed version reads for the IP branch. Authenticated
settings CRUD/validation is proven in
[Linux run 38147134135](https://github.com/nickna/Conduit/actions/runs/38147134135),
and IP CRUD/ETags/source-socket enforcement in
[Linux run 38152552344](https://github.com/nickna/Conduit/actions/runs/38152552344).
Native Admin and Gateway also select the typed billing-audit writer/host; Gateway
timers and graceful drains are proven, while controlled Admin HTTP billing enqueue
remains outside that proof. The later local unit 96 process gate proves healthy
ordinary setting-change propagation to both Gateway caches, with explicit source
and generation limits in the feature matrices. Model routing is
selected behind a read-only
compatibility adapter, while request-log writes use a narrow runtime writer and
authenticated hubs share the virtual-key runtime snapshot. JIT Gateway and JIT
Admin registrations retain their existing EF-backed services, as do the remaining
unextracted Admin management registrations.
Rollback is therefore the JIT artifact, whose request-time and management contracts
continue to resolve to the legacy services.

The selected native profile omits five Gateway and seven Admin EF-backed hosted
workers and advertises their exclusions through runtime capabilities. JIT retains
those workers. Their removal from startup is not proof of native reporting,
retention, reconciliation or broad management execution. The
[Gateway](../operations/gateway-native-aot-feature-matrix.md) and
[Admin](../operations/native-aot-admin-feature-matrix.md) matrices define the
supported operations and the evidence required before restoring excluded workloads.

Dependency extraction follows required consumers. Configuration no longer references
the Functions implementation; function entities and ports live in the neutral
persistence leaf with compatibility forwarding. Admin still requires Functions for
its credential-test client factory and AWS through the media adapter for storage
and cleanup consumers. Keeping these explicit dependencies does not qualify those
remaining EF management APIs. Package ownership and architecture checks keep unused
SignalR, MessagePack, tokenizer and build tooling out of Admin and unused AWS
Bedrock SDKs out of Providers. Resolved dependency guards keep MCP implementation
packages out of Configuration, EF/Npgsql/build tooling out of Functions, and the
optional MessagePack client protocol out of the native SignalR adapter. No wholesale
removal of a required consumer is authorized by this ADR.

## Controlled performance checkpoint

The controlled [Admin](../operations/native-aot-admin-controlled-benchmark.md) and
[Gateway](../operations/native-aot-gateway-controlled-benchmark.md) comparisons
record mixed results. Native Admin setting-read median throughput is 53.53% lower.
Native Gateway JSON throughput is 9.08% higher, while complete-SSE throughput is
4.18% lower and median per-run SSE p99 latency is 7.70% higher. Startup and sampled
memory improve in both comparisons.

These are whole-profile measurements with explicit source and fixture roles, not
causal persistence microbenchmarks. Selected typed-Npgsql slices and the remaining
EF implementations continue to coexist under this accepted decision. Production
remains on JIT and native promotion is deferred; the existing parity, canary,
rollback and approval gates remain unchanged. Phase #1373 assesses the staged
representative persistence contract, including the complete persisted-converter
inventory; it does not require native enablement of every excluded JIT domain.
The query inventory records the evidence required before any later expansion.
Final committed-source CI and acceptance reconciliation remain required;
operational acceptance remains in #1375.

## Consequences

- Persistence migration becomes a sequence of reviewable vertical slices rather than
  a package replacement.
- During migration, EF and typed-Npgsql implementations coexist and must share contract
  tests. This adds short-term code but bounds semantic drift.
- Generic repository convenience APIs may remain for unextracted slices, but new
  consumers must not add expression-based operations to them.
- ADR 0006's production-JIT constraint, canary requirements, and forward-only migration
  rollback policy remain in force.
