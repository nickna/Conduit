# Gateway NativeAOT feature matrix

This page defines the support contract for the first Conduit Gateway NativeAOT image. The contract is intentionally narrower than the JIT image: only behavior exercised by the native process gate is supported. The JIT service keeps its existing behavior.

The running Gateway exposes the same information at `GET /health/runtime-capabilities`. Operators should use that endpoint, rather than inferring support from a successful publish.

Native metadata reports `persistence_status: typed-npgsql-selected-runtime-slices`.
This describes the selected runtime stores, not general EF query support. The
Admin contract is documented separately in
[native-aot-admin-feature-matrix.md](native-aot-admin-feature-matrix.md).
[Linux run 38175668349](https://github.com/nickna/Conduit/actions/runs/38175668349)
at `e0f9f9904df187460ab338bd86619d912f653d60` passes all three jobs, including 219
selected persistence and integration cases without skips, native publish/OpenAPI,
the complete host gate and both packaged entrypoints, with zero first-party linker
diagnostics. The host gate checks exact capability fields and feature sets from
Admin and both Gateways, the selected workloads and owned cleanup. Its clean-Git
image packages still report classic-store OCI association HOLD. Later local cache
convergence evidence is identified separately below. Production promotion remains deferred
under [ADR 0006](../decisions/0006-native-aot-persistence.md) and the
[canary runbook](native-aot-canary.md).

## Supported in the first native image

| Area | NativeAOT contract | Process-gate evidence |
|---|---|---|
| Host topology | One native Admin and two native Gateway processes start together. | Each process is launched and its liveness/capability endpoint returns 200. |
| Global settings cache | Setting-change events publish a cluster reload with their stable event ID; each process-local cache consumes it through Redis. | The unit 96 local Linux native proof reproduces the original SQL7/cache7-5 split, then proves both fresh native Gateways consume ordinary Admin creation 5→7 before any manual reload. Direct SQL9 remains cached as 7 until explicit reload; owned deletion/reload restores 5. All 12 authenticated validation requests reach the exact iteration-range error without provider dispatch or spend. The full process gate and owned cleanup pass with the original native Admin retained; generations and unavailable Git provenance are explicit. This proves healthy reload consumption, with failed-reload retry and completed-ID retention tracked in [#1623](https://github.com/nickna/Conduit/issues/1623) and [#1627](https://github.com/nickna/Conduit/issues/1627). |
| PostgreSQL / Wolverine | Wolverine 6.48.3 PostgreSQL persistence and transport initialize in every host. Static application assembly selection and bounded generic metadata avoid runtime-codegen startup failures. | Real PostgreSQL migration, two-host startup, queue assignment, representative static handler-adapter dispatch, and settled request spend. |
| Redis / SignalR | Redis supplies the SignalR backplane, distributed connection and method limits, webhook subscription state, ephemeral keys, and the data-protection key ring. | The probe observes both live connections, rejects a third at the shared ceiling, checks RPM/RPD keys and zero-count teardown, exercises Redis-backed webhook and ephemeral-key operations, and observes `Conduit-DataProtection-Keys`. |
| SignalR protocol surface | JSON is the only protocol. All eight Gateway hub routes accept real native clients; the public video hub authenticates each subscription with an ephemeral key. | The probe connects and invokes every hub, receives typed virtual-key status, and carries a notification between two native Gateway hosts through the Redis backplane. |
| Authentication boundary | Requests without a virtual key are rejected before the EF-backed data plane. | `GET /v1/models` returns 401 without credentials. |
| Typed request persistence | Virtual-key lookup plus global and per-key IP policy use typed Npgsql repositories in the native Gateway. | With default-deny IP filtering enabled, a seeded virtual key negotiates all eight hub routes on both native Gateway processes. |
| Admin IP-filter management and enforcement | Native Admin selects typed Npgsql for IP CRUD and conditional-request version reads. JIT retains EF. | The unit 76 local published Linux gate and successful public [Linux run 38152552344](https://github.com/nickna/Conduit/actions/runs/38152552344) pass all 12 stored and 11 DTO fields, paginated global/enabled/per-key lists, merge patches, strong ETags, 428/412 and missing/deleted resources. An actual socket bound to `127.0.0.2` changes from denied to allowed and back as owned database rules change; the private-IP bypass is disabled only for the owned Admin child. Unrelated IP and setting rows remain unchanged, and owned cleanup repeats after producer shutdown. See the [Admin matrix](native-aot-admin-feature-matrix.md). |
| Authenticated model discovery | `GET /v1/models`, `GET /v1/models/{model}`, and `GET /v1/conduit/models/{modelId}/metadata` use the fixed-query typed Npgsql model-routing graph. | A seeded alias is listed and retrieved on both native Gateways, and its canonical/provider capability graph is returned by the metadata endpoint. |
| Authenticated function metadata discovery | `GET /v1/conduit/discovery/functions` and `GET /v1/conduit/discovery/functions/{id}/parameters` use the typed configuration catalog store. The runtime advertises `function-metadata-discovery`. | [Linux process gate 38124571584](https://github.com/nickna/Conduit/actions/runs/38124571584) checks both native Gateways: authentication, enabled configurations, named/numeric filters, structured schemas/examples, unknown enum strings, private-field omission, repeated JSON shape, and missing/disabled parameter resources. These reads do not execute functions. |
| Inline Exa search | The runtime advertises `inline-exa-search` for configured search execution and owned execution-by-ID reads. | [Published Linux process job 114465334583](https://github.com/nickna/Conduit/actions/runs/38137532017/job/114465334583) checks both Gateways, successful searches and provider rejections, all 24 persisted execution fields, opposite-host ownership, configured pricing and settled success-only charges. |
| Stateless inline MCP tools | The runtime advertises `inline-stateless-mcp-tools` for configured tools on a stateless Streamable HTTP MCP server. | [Linux run 38147134135](https://github.com/nickna/Conduit/actions/runs/38147134135) executes one configured tool on each Gateway with a production-protected scoped credential, modern protocol metadata, structured/Unicode results, all 24 execution fields, opposite-host ownership and exact configured pricing/accounting. This does not qualify stateful MCP sessions, agentic MCP, every server implementation or provider-hosted tool pricing. |
| Configured Exa agentic chat | The runtime advertises `nonstreaming-configured-exa-agentic-chat` for one configured Exa search followed by a final OpenAI-compatible LLM reply. | The same published process job checks both chat aliases on both Gateways, generated schemas, matching rich tool results, aggregate usage, execution-by-ID ownership, all 28 chat-log fields and combined LLM/function settlement. |
| Provider chat transport | OpenAI-compatible non-stream HTTP, SSE streaming, customer-safe provider error translation, and downstream cancellation propagation are supported. | A separate native provider process validates the credential, returns JSON/SSE responses, emits a private 400 diagnostic that must be sanitized, and observes the upstream connection close after the probe disconnects. |
| Request accounting | Provider usage is priced through the fixed-shape model-cost lookup, persisted by the typed request-log writer, and settled by the typed virtual-key store. | Four successful chat requests (one non-streamed and three SSE variants) persist exact token/cost rows totaling $0.0003; the batch worker updates balance and lifetime spend and creates the matching debit ledger entry in real PostgreSQL. |
| Billing-audit writes | The runtime advertises `billing-audit-batch-writes`. Each native host owns a 100-event / 10-second queue backed by atomic typed Npgsql writes and a cancellation-aware shutdown drain. Scheduled EF retention is disabled. | [Linux run 38149774358](https://github.com/nickna/Conduit/actions/runs/38149774358) checks two quiet Gateway timers below threshold and all 17 stored fields. Each Gateway then passes a fresh marker through its timer before one pending tail and an owned graceful exit with an exclusive final-flush witness. The native persistence probe also covers immutable ordered results, generated defaults, 129-row atomic rollback/retry, constraints and SQL/pool cancellation. Real PostgreSQL service cases cover failed-write requeue/retry and shutdown cancellation with reuse. Admin selects the same hosted writer; a controlled Admin HTTP enqueue is not covered. Audit reads, retention, reconciliation and abrupt-termination durability remain separate. |
| Async-task persistence | Task status, cancellation, claims, provider phases, recovery, retry preparation, and retention use a fixed-shape runtime store. | A seeded task is read on one native Gateway, cancelled on the second, observed through the shared cache on the first, and verified durable in PostgreSQL. EF and typed Npgsql also pass the same real-PostgreSQL lifecycle contract. |
| S3 media workflows | Provider image generation, media quota lookup, S3-compatible upload/read, durable media ownership, and authenticated downloads use the native runtime stores. | A separate provider returns a base64 PNG; the primary Gateway stores it in S3-compatible storage and PostgreSQL, while the secondary Gateway proves typed ownership and returns identical bytes through authenticated metadata/download endpoints. Linux CI and the current local published gate use S3Mock; earlier local proof used MinIO. EF and typed Npgsql pass the same media/quota contract. |
| Operations | Liveness, Prometheus exposition, and middleware/telemetry startup are exercised. Gateway metrics select fixed-shape aggregates through EF on JIT and typed Npgsql on native. | The native gate requires a $0.40/min cost rate, 0.20s mean latency, and one active mapping from an elected business-metrics host, plus two pending notification tasks from each Gateway. EF/Npgsql aggregates also pass the same real-PostgreSQL contract. |
| Schema and dependency readiness | Wait-mode hosts stay live while withholding readiness until the release schema and Wolverine storage are available. Readiness reflects live PostgreSQL connectivity and Gateway Redis connectivity. | Empty/prior databases recover on the same native Admin and two Gateway processes after the standalone migrator; future schema history and PostgreSQL/Redis outages return the required 503 statuses and recover without restarts. [Linux process gate 38121134709](https://github.com/nickna/Conduit/actions/runs/38121134709) passed all these cases. |

SignalR MessagePack is deliberately excluded from NativeAOT publishes and the AOT
audit profile. Its registration code and protocol package reference are conditional,
and a build guard rejects the optional MessagePack client protocol package in the
resolved native SignalR graph. The Redis backplane still requires the MessagePack
library for its internal transport; client protocol exclusion does not remove that
dependency. Test projects own their JIT MessagePack compatibility dependencies explicitly. A
normal JIT build enables MessagePack by default and still honors
`SIGNALR_MESSAGEPACK_ENABLED=false`.

## Explicit exclusions

The following features are outside the native support contract and appear in `excluded_features`:

- `signalr-messagepack`
- `ef-core-query-data-plane`
- `redis-virtual-key-authentication-cache`
- `inline-functions-beyond-exa-search-and-stateless-mcp`
- `agentic-workflows-beyond-nonstreaming-configured-exa-chat`
- `function-execution-list-http`
- `function-audit-and-retention`
- `billing-reconciliation`
- `request-log-reporting-and-retention`
- `pricing-rule-audit-and-retention`
- `model-discovery-catalog-and-warming`
- `billing-audit-reads-and-retention`
- `provider-hosted-tool-pricing`

The excluded model catalog at `GET /v1/conduit/discovery/models` and UI parameters
at `GET /v1/conduit/discovery/models/{model}/parameters` return 501 with a generated
OpenAI error (`not_implemented`) in the native profile before resolving the
EF-backed discovery handler. Both routes retain virtual-key authentication;
unauthenticated requests return 401. JIT retains their catalog, cache and parameter
behavior. The supported `/v1/models` routes and capability metadata above remain
available, and function catalog/parameter discovery is unchanged.

Native builds omit these five EF-backed hosted workers. JIT retains their original
registrations and leader-election behavior:

| Worker | Native registration boundary |
|---|---|
| `RequestLogService` | `Extensions/AuditServicesExtensions.cs` omits the legacy service, its runtime-writer alias and hosted wrapper. Native request accounting retains `StoreBackedRequestLogRuntimeWriter` over the typed Npgsql store. |
| `FunctionCallAuditService` | The same extension omits the service and hosted wrapper. Function execution rows remain supported; function audit append/read/retention is separate. |
| `BillingReconciliationService` | `Extensions/BillingServicesExtensions.cs` omits the singleton and hosted wrapper while retaining options binding and validation. |
| `PricingAuditService` | The same extension omits both the service and hosted wrapper. `PricingRulesEvaluator` retains its optional dependency as null, preventing its 100-event queue threshold from entering EF without a timer. Pricing calculation services remain registered. |
| `DiscoveryCacheWarmingService` | `Program.CoreServices.cs` omits the model-catalog warmer. The discovery cache and proven function catalog/tool discovery remain registered. |

Paths in this table are under `Services/ConduitLLM.Gateway`. Typed billing-audit
flushing, business/task metrics, operational metrics, async/media services,
provider reprobe, heartbeats and Redis spend/webhook services remain registered.
None of the five omitted workers is a native critical leader-health dependency.
Request-log reporting and retention still exist in legacy EF implementations;
their omission does not remove typed request-time writes or spend settlement.
The MessagePack client protocol is also omitted at build time. Successful metadata
discovery or ordinary request spend does not establish other function paths or
reconciliation.

The native gate uses `CONDUIT_MIGRATION_MODE=Wait` after the standalone migrator
and requires `/health/ready` to return 200 from Admin and both Gateways. In its
disposable parity database, it inserts one uniquely named future migration-history
row, requires readiness 503 and liveness 200 from the same three processes, removes
only that row in bounded cleanup, and requires readiness 200 again. Owned loopback
TCP proxies then interrupt the hosts' Redis and PostgreSQL connections. Redis loss
must produce Gateway readiness 503 while Admin remains ready; PostgreSQL loss must
produce readiness 503 from all three hosts. Every liveness response must remain
200, and all three readiness responses must recover to 200 without host restarts.
The same gate also starts the three published native processes against empty and
prior-release databases, requires two live200/ready503 observations, runs the
standalone migrator and requires ready200 without restarting them. This proves the
schema/dependency health contract; it does not establish support for unextracted
background workers or full Admin management workloads. Authenticated model
discovery covers `/v1/models`, model retrieval, and model metadata. The separate
`/v1/conduit/discovery/models` projection and its cache warmer remain unproved.
Authenticated function catalog and parameter metadata are supported through the
two routes listed above. Core's LLM tool/route discovery now has paired managed
tests and explicit native composition through scalar configuration reads. Its
selected Exa workflow and ChatEndpoints' configuration preflight pass the published
process job linked above. [Linux process gate 38128502143](https://github.com/nickna/Conduit/actions/runs/38128502143)
also passes the execution endpoint's authentication, missing/disabled configuration
and missing-required-parameter preflight on both Gateways. Those cases return before
provider execution. Execution-list HTTP, stateful or agentic MCP, other inline providers, provider-hosted
tool pricing, agentic SSE, parallel/dependent calls, function audit/retention, and
billing reconciliation remain outside the native support contract. The supported
Exa execution-by-ID reads include opposite-host ownership and missing/authentication
errors. Connecting a video hub or cancelling a seeded task also
does not prove provider video/audio generation or durable worker recovery. See the
[current query inventory](persistence-aot-query-inventory.md) and
[integration checkpoint](native-aot-audit.md#integration-checkpoint-2026-10-11) for
the current evidence and limits.

EF Core 10 can generate the compiled `ConduitDbContext` model, but its NativeAOT query precompiler rejects Conduit's repository abstractions as dynamic LINQ. Extracted request-time operations now bypass those queries for global settings, IP filters, provider/credential reads, virtual keys, model discovery/routing metadata, function metadata and scalar configuration/credential/pricing reads, model-cost reads, request-log writes, virtual-key spend settlement, async tasks, media ownership/quota reads and writes, and the bounded aggregates used by Gateway operational metrics. Broad Admin request-log reporting and retention remain unextracted. OpenAI-compatible provider chat HTTP/SSE, translated errors, cancellation, request accounting, function metadata discovery, inline Exa search, stateless inline MCP tools, configured non-streaming Exa agentic chat, billing-audit writes, async-task persistence, S3-compatible image workflows, authenticated JSON SignalR, Redis backplane/rate limiting, and public ephemeral-key subscriptions are process-tested. JIT retains the full worker profile and existing service behavior.

Gateway request-time consumers now depend on `IVirtualKeyRuntimeService`, while
Admin-style key management remains behind the broader `IVirtualKeyService`. Both
contracts resolve to the same scoped implementation in JIT builds. Native builds now
resolve the request-time contract to `StoreBackedVirtualKeyRuntimeService`, while the
management contract remains on the legacy service. The native process gate proves this
request-time path through authenticated HTTP and SignalR execution. The management hub
reuses the hydrated runtime snapshot for its initial status message rather than
re-entering the broad EF repository.

The persistence side of that seam is also explicit: `IVirtualKeyRuntimeStore` returns
backend-neutral key/group snapshots and performs atomic balance-plus-ledger updates.
Its fixed-query EF reference adapter and typed Npgsql adapter pass the same PostgreSQL
contract, including concurrent writers, idempotent redelivery, and conflict detection,
and the Npgsql adapter runs from the published native persistence probe. The native
Gateway now selects that adapter for virtual-key lookup, hydrated group limits, balance
validation, and direct spend fallback. The same native-only registration replaces the
global-setting, IP-filter, provider, and provider-credential repositories with their
already parity-tested typed-Npgsql implementations. A complete model-routing runtime
graph now has fixed-query EF and typed-Npgsql stores plus real-PostgreSQL and native
probe coverage. Native Gateway registers a read-only adapter over that store while JIT
and Admin retain the full EF repository; persisted route policy is read through the same
store. Native request billing also resolves costs by fixed ID or provider model
identifier through that graph; JIT retains the full model-cost management service and
cache decorator. Request-time logging now uses a narrow runtime writer: JIT retains the batched
EF implementation, while native builds select the parity-tested typed-Npgsql writer.
The Gateway's batch-spend service is also supplied with the selected virtual-key store,
covering key/group lookup, idempotent ledger debits, fallback charges, and invalidation
hashes without scoped EF resolution. Authenticated hub connections use that same
runtime service, while Redis owns cross-host delivery, admission/method limits,
webhook subscriptions, and ephemeral-key validation. Request-log queries/retention
remain EF-backed. Async-task runtime operations now use
`IAsyncTaskRuntimeStore`: JIT and non-Gateway hosts resolve a narrow EF reference
adapter, while native Gateway resolves a typed-Npgsql implementation with fixed SQL
for lifecycle CRUD, claims, provider phases, leases, indeterminate reconciliation,
and retention. Both implementations pass the same real-PostgreSQL contract. The Npgsql
implementation runs inside the published persistence probe, and the two-host native
gate proves authenticated read/cancel behavior plus the durable row transition. The
media runtime follows the same pattern: JIT uses an EF adapter, while native Gateway
uses fixed-shape Npgsql for ownership, access statistics, active storage aggregates,
and quota reporting. Its shared PostgreSQL contract covers tombstones and assigned-
policy fallback semantics. The native process gate then carries a provider-generated
PNG through quota enforcement, S3-compatible storage, durable ownership, and cross-host authenticated
download. The remaining exclusions therefore continue to describe the still-
unextracted data plane.

The compiled model is checked in under `Shared/ConduitLLM.Configuration/Data/CompiledModels`. Regenerate it whenever the EF model changes:

```powershell
$env:ConduitEfTooling = 'true'
$env:CONDUIT_EF_COMPILED_MODEL = 'true'
$env:DATABASE_URL = 'postgresql://user:password@localhost:5432/conduitdb'
dotnet ef dbcontext optimize --project Shared/ConduitLLM.Configuration --startup-project Shared/ConduitLLM.Configuration --context ConduitDbContext --output-dir Data/CompiledModels --namespace ConduitLLM.Configuration.Data.CompiledModels --nativeaot --configuration Release
./scripts/aot/normalize-compiled-model.ps1
```

The generator currently needs the three `Model = ConduitLLM.Configuration.Entities.Model` aliases retained in generated files because the domain entity name collides with an EF internal type.

Normalization also reapplies 22 exact `IL3050` exceptions to generated `Create`
methods whose closed enum and array types are statically named and rooted by the
compiled model. It does not suppress the warning category or any application method.
Each exception names the database/runtime owner, EF NativeAOT tracking issue, and
removal condition. CI runs the script with `-Check` so regeneration cannot silently
drop or broaden the reviewed set.

## Native process gate

CI runs `scripts/aot/gateway-native-parity.ps1` after native publishing. It requires PostgreSQL, Redis, S3-compatible storage (digest-pinned S3Mock in Linux CI, MinIO locally), the standalone migrator, and the published native artifacts. The gate provisions a default-deny global IP policy, a rate-limited virtual key with its own allowlist and media quota, a complete model-routing/cost graph, a durable async task, and a provider credential targeting a separate native OpenAI-compatible stub. It negotiates every hub; opens authenticated JSON connections to all hub families; verifies distributed admission, method counters, cross-host delivery, webhook tracking, typed management status, and public ephemeral-key subscription; exercises authenticated model and function metadata discovery and cross-host task read/cancellation; verifies non-stream chat, SSE chat, error translation, cancellation propagation, request-log persistence, and batch spend settlement; and stores a provider-generated PNG before reading its durable media row and downloading identical bytes through the second Gateway.

The separate `scripts/test/wolverine-two-host-smoke.ps1 -NativeArtifactDirectory <artifact>` gate exercises cross-host Wolverine delivery with native Admin/Gateway processes.

The function phases then reject invalid configuration requests before dispatch,
execute successful and rejected inline Exa searches on both hosts, and carry four
non-streaming chats through generated tool schemas, one configured search and a
final LLM reply. Independent SQL checks complete execution/log mappings,
opposite-host ownership and stable combined settlement at each column's precision.
Inherited cost summaries, iteration and log attribution are tracked in
[#1577](https://github.com/nickna/Conduit/issues/1577),
[#1578](https://github.com/nickna/Conduit/issues/1578) and
[#1579](https://github.com/nickna/Conduit/issues/1579).
The stateless MCP and Admin settings phases passed in
[Linux run 38147134135](https://github.com/nickna/Conduit/actions/runs/38147134135).
The Admin IP phase additionally checks authenticated CRUD and version headers,
independent persisted scalars, paginated selections and real source-IP enforcement.

Before normal fixture seeding, the parity script creates two marked disposable
databases: one empty, one with the real penultimate EF migration. It starts native
Admin and two Gateways using PostgreSQL Wolverine with provisioning disabled,
requires live200/ready503, runs the standalone release migrator, and requires
ready200 on the same processes. The read-only startup barrier checks the release
lock, exact schema version and mandatory bus tables/columns before starting
Wolverine once. It does not claim full catalog equivalence. Owned processes and
databases are removed on failure as well as success; temporary environment fields
are restored before the normal parity workload. The provider-key reprobe worker
is disabled only inside these isolated startup scenarios.

The metrics gate inserts two uniquely labeled request rows after startup, with
costs $1.25/$0.75 and response times 100/300ms. It polls both Gateways for up to 150
seconds to accommodate leader election and the one-minute collection interval.
The response-time histogram observes the database mean once per collection, so
the gate checks its sum/count rather than assuming two histogram observations.
Bounded cleanup removes only those owned rows before request-accounting workflows.

The outage gate uses loopback control protected by a per-run token, closes its own
forwarded connections while blocked, and restores both routes with a fresh cleanup
deadline. It never stops the PostgreSQL or Redis servers. Migration and fixture
setup use the original dependency routes; child hosts use the proxy routes, while
probe SQL/Redis reads and cleanup keep direct access. The script restores its
environment and stops only processes it started. Actual authenticated database,
provider, media, Redis and cross-host SignalR workflows run after dependency
recovery. The fixture requires single TCP endpoints without Redis Sentinel and
rejects PostgreSQL VerifyFull TLS, whose original certificate hostname cannot be
preserved by changing the connection host to loopback. Router option-preservation
tests do not extend the hosts' existing URI parsing contract; its inconsistencies
are tracked in [#1561](https://github.com/nickna/Conduit/issues/1561).

## Third-party NativeAOT risk ledger

| Dependency | Version | Upstream reference | Remaining risk | Removal condition |
|---|---:|---|---|---|
| EF Core | 10.0.12 | [NativeAOT epic #29754](https://github.com/dotnet/efcore/issues/29754), [precompiled queries #25009](https://github.com/dotnet/efcore/issues/25009) | Dynamic repository query composition is rejected by the experimental precompiler. Query-backed features are excluded and observable. | The EF precompiler accepts Conduit's query shapes, or the repository layer is converted to precompilable/static queries; then enable and process-test the data plane before removing exclusions. |
| Wolverine / JasperFx | Wolverine 6.48.3, JasperFx 2.81.0 | [Wolverine #2769](https://github.com/JasperFx/wolverine/issues/2769), [#2757](https://github.com/JasperFx/wolverine/issues/2757) | Wolverine still closes router/serializer generics and locates its generated handler registry and adapters reflectively. Conduit supplies narrow runtime directives and explicit application assembly selection. | Remove each directive only after the pinned dependency no longer uses runtime generic construction/exported-type discovery and the two-host native gate remains green. |
| MessagePack-CSharp | 3.1.11; SignalR protocol 10.0.12 | [NativeAOT support #1503](https://github.com/MessagePack-CSharp/MessagePack-CSharp/issues/1503), [generator issue #2283](https://github.com/MessagePack-CSharp/MessagePack-CSharp/issues/2283) | The native client protocol registration and package reference are excluded, with a resolved graph build guard. The Redis backplane retains its internal MessagePack dependency. JIT retains contractless client payload compatibility. | Supporting the MessagePack client protocol in native hosts later requires generated hub payload resolvers and native protocol tests. |
| AWS SDK for .NET S3 | 4.0.104.2 | [trim-safe runtime dependency work #4354](https://github.com/aws/aws-sdk-net/issues/4354), [AOT tracking #2486](https://github.com/aws/aws-sdk-net/issues/2486) | Authenticated upload/read/download is process-tested against S3Mock in Linux CI and the current local published gate, with earlier MinIO proof. Publish logs retain third-party linker diagnostics outside the first-party ratchet; CI does not cover every S3-compatible vendor. | Remove warnings only after the pinned SDK supplies the required trim annotations; add provider-specific gates before claiming behavior beyond the S3-compatible contract. |

The first-party linker ratchet is empty. This includes bounded exceptions on 22
generated compiled-model methods and 25 unsupported EF query methods; zero
emitted diagnostics does not establish native support for those workloads.
Replacing or proving those queries remains a production-promotion blocker. This
ledger records third-party runtime/AOT boundaries specific to protocol and
infrastructure parity.
