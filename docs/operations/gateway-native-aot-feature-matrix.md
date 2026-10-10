# Gateway NativeAOT feature matrix

This page defines the support contract for the first Conduit Gateway NativeAOT image. The contract is intentionally narrower than the JIT image: only behavior exercised by the native process gate is supported. The JIT service keeps its existing behavior.

The running Gateway exposes the same information at `GET /health/runtime-capabilities`. Operators should use that endpoint, rather than inferring support from a successful publish.

## Supported in the first native image

| Area | NativeAOT contract | Process-gate evidence |
|---|---|---|
| Host topology | One native Admin and two native Gateway processes start together. | Each process is launched and its liveness/capability endpoint returns 200. |
| PostgreSQL / Wolverine | Wolverine 6.14 PostgreSQL persistence and transport initialize in every host. Static application assembly selection and bounded generic metadata avoid runtime-codegen startup failures. | Real PostgreSQL migration, two-host startup, queue assignment, representative static handler-adapter dispatch, and settled request spend. |
| Redis / SignalR | Redis supplies the SignalR backplane, distributed connection and method limits, webhook subscription state, ephemeral keys, and the data-protection key ring. | The probe observes both live connections, rejects a third at the shared ceiling, checks RPM/RPD keys and zero-count teardown, exercises Redis-backed webhook and ephemeral-key operations, and observes `Conduit-DataProtection-Keys`. |
| SignalR protocol surface | JSON is the only protocol. All eight Gateway hub routes accept real native clients; the public video hub authenticates each subscription with an ephemeral key. | The probe connects and invokes every hub, receives typed virtual-key status, and carries a notification between two native Gateway hosts through the Redis backplane. |
| Authentication boundary | Requests without a virtual key are rejected before the EF-backed data plane. | `GET /v1/models` returns 401 without credentials. |
| Typed request persistence | Virtual-key lookup plus global and per-key IP policy use typed Npgsql repositories in the native Gateway. | With default-deny IP filtering enabled, a seeded virtual key negotiates all eight hub routes on both native Gateway processes. |
| Authenticated model discovery | Model list, retrieval, and effective capability metadata use the fixed-query typed Npgsql model-routing graph. | A seeded alias is listed and retrieved on both native Gateways, and its canonical/provider capability graph is returned by the metadata endpoint. |
| Provider chat transport | OpenAI-compatible non-stream HTTP, SSE streaming, customer-safe provider error translation, and downstream cancellation propagation are supported. | A separate native provider process validates the credential, returns JSON/SSE responses, emits a private 400 diagnostic that must be sanitized, and observes the upstream connection close after the probe disconnects. |
| Request accounting | Provider usage is priced through the fixed-shape model-cost lookup, persisted by the typed request-log writer, and settled by the typed virtual-key store. | Two successful chat requests persist exact token/cost rows; the batch worker updates balance and lifetime spend and creates the matching debit ledger entry in real PostgreSQL. |
| Async-task persistence | Task status, cancellation, claims, provider phases, recovery, retry preparation, and retention use a fixed-shape runtime store. | A seeded task is read on one native Gateway, cancelled on the second, observed through the shared cache on the first, and verified durable in PostgreSQL. EF and typed Npgsql also pass the same real-PostgreSQL lifecycle contract. |
| S3 media workflows | Provider image generation, media quota lookup, S3-compatible upload/read, durable media ownership, and authenticated downloads use the native runtime stores. | A separate provider returns a base64 PNG; the primary Gateway stores it in S3-compatible storage and PostgreSQL, while the secondary Gateway proves typed ownership and returns identical bytes through authenticated metadata/download endpoints. Linux CI uses S3Mock; local proof uses MinIO. EF and typed Npgsql pass the same media/quota contract. |
| Operations | Liveness, Prometheus exposition, and middleware/telemetry startup are exercised. Gateway metrics select fixed-shape aggregates through EF on JIT and typed Npgsql on native. | Native liveness and `/metrics` return 200; the probe checks a `# HELP` body. EF/Npgsql metric aggregates pass the same JIT real-PostgreSQL contract. Seeded aggregate values are not yet asserted from a published native process. |

SignalR MessagePack is deliberately not registered by a NativeAOT publish; its
registration code is excluded at compile time. `ConduitLLM.SignalR.csproj` still
has an unconditional MessagePack protocol package reference. Disabling the protocol
does not prove that its package was omitted from the restore graph or that every
related dependency disappeared from the linked image. A normal JIT build enables
MessagePack by default and still honors `SIGNALR_MESSAGEPACK_ENABLED=false`.

## Explicit exclusions

The following features are excluded from the first native image and appear in `excluded_features`:

- `signalr-messagepack`
- `ef-core-query-data-plane`
- `redis-virtual-key-authentication-cache`
- `readiness-and-database-health`

The native gate explicitly sets `CONDUIT_MIGRATION_MODE=Skip` and checks liveness,
not `/health/ready`; schema-wait and database-health parity remain unproven. The
authenticated model-discovery proof covers `/v1/models`, model retrieval, and
model metadata. It does not cover the newer `/v1/discovery/models` or
`/v1/discovery/functions` query paths. Likewise, connecting a video hub or cancelling
a seeded task does not prove provider video/audio generation or durable worker
recovery. See the [current query inventory](persistence-aot-query-inventory.md) and
[epic assessment](native-aot-audit.md#epic-1368-assessment-2026-10-09) for those gaps.

EF Core 10 can generate the compiled `ConduitDbContext` model, but its NativeAOT query precompiler rejects Conduit's repository abstractions as dynamic LINQ. Extracted request-time operations now bypass those queries for global settings, IP filters, provider/credential reads, virtual keys, model discovery/routing metadata, model-cost reads, request-log writes, virtual-key spend settlement, async tasks, media ownership/quota reads and writes, and the bounded aggregates used by Gateway operational metrics. Broad Admin request-log reporting and retention remain unextracted. OpenAI-compatible provider chat HTTP/SSE, translated errors, cancellation, request accounting, async-task persistence, S3-compatible image workflows, authenticated JSON SignalR, Redis backplane/rate limiting, and public ephemeral-key subscriptions are process-tested and supported. All excluded features remain available in the JIT image.

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

CI runs `scripts/aot/gateway-native-parity.ps1` after native publishing. It requires PostgreSQL, Redis, S3-compatible storage (digest-pinned S3Mock in Linux CI, MinIO locally), the standalone migrator, and the published native artifacts. The gate provisions a default-deny global IP policy, a rate-limited virtual key with its own allowlist and media quota, a complete model-routing/cost graph, a durable async task, and a provider credential targeting a separate native OpenAI-compatible stub. It negotiates every hub; opens authenticated JSON connections to all hub families; verifies distributed admission, method counters, cross-host delivery, webhook tracking, typed management status, and public ephemeral-key subscription; exercises authenticated model discovery and cross-host task read/cancellation; verifies non-stream chat, SSE chat, error translation, cancellation propagation, request-log persistence, and batch spend settlement; and stores a provider-generated PNG before reading its durable media row and downloading identical bytes through the second Gateway.

The separate `scripts/test/wolverine-two-host-smoke.ps1 -NativeArtifactDirectory <artifact>` gate exercises cross-host Wolverine delivery with native Admin/Gateway processes.

## Third-party NativeAOT risk ledger

| Dependency | Version | Upstream reference | Remaining risk | Removal condition |
|---|---:|---|---|---|
| EF Core | 10.0.10 | [NativeAOT epic #29754](https://github.com/dotnet/efcore/issues/29754), [precompiled queries #25009](https://github.com/dotnet/efcore/issues/25009) | Dynamic repository query composition is rejected by the experimental precompiler. Query-backed features are excluded and observable. | The EF precompiler accepts Conduit's query shapes, or the repository layer is converted to precompilable/static queries; then enable and process-test the data plane before removing exclusions. |
| Wolverine / JasperFx | Wolverine 6.14.0, JasperFx 2.13.x | [Wolverine #2769](https://github.com/JasperFx/wolverine/issues/2769), [#2757](https://github.com/JasperFx/wolverine/issues/2757) | Wolverine still closes router/serializer generics and locates its generated handler registry and adapters reflectively. Conduit supplies narrow runtime directives and explicit application assembly selection. | Remove each directive only after the pinned dependency no longer uses runtime generic construction/exported-type discovery and the two-host native gate remains green. |
| MessagePack-CSharp | 2.5.302; SignalR protocol 10.0.10 | [NativeAOT support #1503](https://github.com/MessagePack-CSharp/MessagePack-CSharp/issues/1503), [generator issue #2283](https://github.com/MessagePack-CSharp/MessagePack-CSharp/issues/2283) | Native protocol registration is excluded, but the SignalR project still restores its package; JSON remains supported. | Conditionally remove the unused native package and verify the resulting graph. Supporting MessagePack later requires generated hub payload resolvers and native protocol tests. |
| AWS SDK for .NET S3 | 4.0.24.2 | [trim-safe runtime dependency work #4354](https://github.com/aws/aws-sdk-net/issues/4354), [AOT tracking #2486](https://github.com/aws/aws-sdk-net/issues/2486) | Authenticated upload/read/download is process-tested against S3Mock in Linux CI and MinIO locally. Publish logs retain third-party linker diagnostics outside the first-party ratchet; CI does not cover every S3-compatible vendor. | Remove warnings only after the pinned SDK supplies the required trim annotations; add provider-specific gates before claiming behavior beyond the S3-compatible contract. |

The first-party linker ratchet is empty. This includes bounded exceptions on 22
generated compiled-model methods and 25 unsupported Admin EF query methods; zero
emitted diagnostics does not establish native support for those Admin workloads.
Replacing or proving those queries remains a production-promotion blocker. This
ledger records third-party runtime/AOT boundaries specific to protocol and
infrastructure parity.
