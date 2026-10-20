# Admin and Gateway dependency boundaries

Issue [#1372](https://github.com/nickna/Conduit/issues/1372) implements the assembly
direction in [ADR 0009](../decisions/0009-staged-native-persistence-refactor.md).
Namespaces and linked source locations are compatibility details; the compiling
assembly and the evaluated dependency graph determine ownership.

## Project owners and allowed direction

| Owner | Responsibilities | Allowed shared-project dependencies |
|---|---|---|
| Contracts | Transport-neutral messages and domain events | None |
| Persistence.Abstractions | Persisted POCOs, DTOs, enums, validation primitives, materializing repository ports, runtime operation ports and generated JSON metadata | Contracts |
| Functions | Function clients, discovery and execution | Persistence.Abstractions |
| Core | Execution, pricing, routing decorators, caching, health contracts and media orchestration | Contracts, Persistence.Abstractions, Functions |
| Providers | Provider clients and route selection over named persistence operations | Core and its neutral dependencies |
| Configuration | EF context, compiled model, migrations, mappings, repositories, security/configuration infrastructure and EF reference adapters | Contracts, Persistence.Abstractions |
| Persistence.Npgsql | Selected fixed-query typed PostgreSQL implementations | Persistence.Abstractions |
| Persistence.Composition | Host registrations combining EF implementations and Core/Providers; PostgreSQL connections/locks and EF discovery queries | Configuration, Core, Providers |
| Messaging.Wolverine | PostgreSQL messaging transport and invalidation registration | Contracts, Configuration, Core |
| Media | S3 media storage adapter | Core |
| SignalR | Hub protocol registration, Redis backplane and notification base | Configuration |
| Tokenization | Token counting implementation | Core |
| OpenApi | Document transformers | No execution or persistence implementation |
| Security | Host authentication and security services | Configuration, Core |

Both hosts compose Configuration, Core, Providers, Security, Media, OpenApi,
Persistence.Abstractions, Persistence.Npgsql, Persistence.Composition and
Messaging.Wolverine. Gateway additionally consumes SignalR and Tokenization.
Neither host is a dependency of a shared project or the other host. The executable
projects own feature selection. The standalone migrator continues to own production
schema migration. These boundaries do not remove EF from either host.

## Removed Core and Providers consumers

| Previous dependency | Classification | Replacement owner / operation |
|---|---|---|
| Configuration entities, provider/pricing enums, DTOs, options and validation results | Neutral persisted/public contract | Persistence.Abstractions, preserving existing namespaces and member layouts |
| Configuration service/repository interfaces, credential-protection port | Neutral port | Persistence.Abstractions; no DbContext or caller-supplied query provider |
| Messaging and domain events | Neutral transport contract | Contracts |
| SignalR payloads and generated wire metadata | Neutral DTO contract | Persistence.Abstractions |
| Logging, key hashing, capability resolution and pagination primitives | Neutral utility | Persistence.Abstractions |
| `DiscoveryModelProjector.ProjectAsync(ConduitDbContext, ...)` | EF query and projection | Persistence.Composition; same query/projection and host call sites |
| DbContext-constrained database registration | Host composition | Persistence.Composition |
| `AddSharedApplicationServices` / `AddModelCostCache` | Host composition of EF services and Core decorators | Persistence.Composition; preserved extension namespaces |
| Provider-service registration and persisted credential-protector construction | Host composition | Persistence.Composition; provider execution retains only the credential-protection port |
| Application-cache registration with host Redis URL resolution | Host composition | Persistence.Composition; cache execution and the Redis caching package remain in Core |
| Connection-string manager, database connection factory and PostgreSQL distributed locks | PostgreSQL infrastructure | Persistence.Composition; Core retains the interfaces |
| `DatabaseAwareLLMClientFactory` EF route-policy fallback | EF query | `IModelRoutePolicyReader.GetByAliasAsync` in Persistence.Abstractions, `EfModelRoutePolicyReader` in Configuration |

The provider factory keeps the native runtime mapping store as its first choice.
JIT host composition supplies the EF reader for the former fallback. Missing or
disabled policies retain the existing default-policy behavior. No alternate SQL
backend or native operation is newly qualified by this extraction.

Configuration forwards extracted public types to their new owners. Persisted
tables, EF mappings, converter behavior, namespaces and generated wire metadata
remain part of the existing contract fixtures. Implementation classes that now
compile into Persistence.Composition retain their namespaces for repository source
callers. Consumers of those implementation classes must reference the composition
adapter; Core cannot forward to it without creating the prohibited cycle.
Direct construction of `DatabaseAwareLLMClientFactory` now supplies the optional
`routePolicyReader` port in place of its former concrete `dbContextFactory`
parameter. Host registrations supply this automatically. The EF-typed constructor
cannot remain in Providers without restoring the dependency this issue removes;
the HTTP and persisted contracts are unchanged.

## Package ownership and explicit profiles

| Package family | Owner and consumer | JIT | Native / analyzer profile |
|---|---|---|---|
| EF Core, Npgsql EF provider | Configuration; host management/reference services | Retained | Retained for explicitly unsupported management operations and converter proof; not removed by selected typed stores |
| Npgsql | Configuration, Persistence.Npgsql and Persistence.Composition | Retained | Retained for selected typed operations and infrastructure |
| DistributedLock.Postgres | Persistence.Composition, host lock registration | Retained | Retained; absent from Core/Providers closure |
| Wolverine/PostgreSQL transport | Messaging.Wolverine, both hosts | Retained | Retained with checked-in generated artifacts |
| Wolverine runtime compilation | Host code generation only | Absent normally | Absent normally; opt in with `ConduitWolverineCodegen=true` for generation, never runtime release inputs |
| EF Design and MSBuild tasks | Configuration design-time generation | Absent normally | Absent normally; `ConduitEfTooling=true` is a tooling profile |
| AWS S3 | Media, both hosts | Retained | Retained for storage/cleanup; Admin is an intentional consumer |
| MCP client SDK | Functions, both hosts | Retained | Retained for selected function execution and Admin credential testing; absent from neutral leaves and Configuration |
| SignalR MessagePack client protocol | SignalR, Gateway only | Retained | Omitted when `PublishAot=true` or `ConduitAotAudit=true` |
| MessagePack (internal backplane format) | SignalR Redis backplane | Retained | Retained; JSON-only hub clients still require the backplane's internal format |
| Tokenizer | Tokenization, Gateway only | Retained | Retained; Admin does not consume it |
| OpenAPI/Scalar/document generation | OpenApi and host HTTP document paths | Retained | Retained where used by generated document serving and smoke gates |
| StackExchange.Redis / FusionCache | Core caches, Configuration caching/data protection, Security, SignalR backplane, host Redis wiring | Retained | Retained for supported authentication, cache, rate-limit and backplane paths |
| Health checks | Core health abstractions, Configuration/Security EF and Redis checks, host readiness | Retained | Selected readiness and PostgreSQL/Redis health remain supported; retained EF management health does not imply native query support |
| OpenTelemetry and Prometheus | Host telemetry wiring; Core/Configuration runtime metrics | Retained | Retained; no blanket telemetry exclusion |

Adapter separation alone is not an omission switch. The current hosts do not offer
a supported S3-free, Functions-free, Redis-free, Wolverine-free or EF-free publish.
Removing an adapter project reference manually is not a supported feature profile.
Admin omits Gateway protocols and tokenization through its project graph. Native
Gateway omits the optional MessagePack client protocol using the properties above.
Normal JIT protocol compatibility is preserved.

Self-contained ASP.NET builds enumerate the shared framework's SignalR Core,
Common and JSON assemblies among publish inputs before trimming. The Admin
filename guards permit these framework assemblies while rejecting the optional
SignalR client, Redis backplane, MessagePack client protocol and Conduit adapter
assemblies. The broader Admin resolved-package ban and emitted-assembly checks
remain in force; framework-pack inputs do not establish an optional adapter root.

The [Gateway feature matrix](gateway-native-aot-feature-matrix.md) and
[Admin feature matrix](native-aot-admin-feature-matrix.md) remain authoritative for
supported operations and excluded workers/routes. `ConduitAotAudit=true` enables
analyzers and the native compile-time feature selection without invoking the native
linker. `PublishAot=true` selects native compilation. Production remains JIT under
ADR 0006; #1373, #1374 and #1375 retain their independent acceptance criteria.

## Verification and measurement provenance

Project-edge, cycle, resolved-package and publish-input/output guards accompany
the extraction. The normal JIT tests and separate Linux native workflow retain
contract serialization, host composition, real PostgreSQL parity, published
persistence/converter probes, two-host Wolverine, supported Gateway infrastructure
and packaged-entrypoint gates. Both first-party analyzer/linker ratchets stay zero;
no additional suppression or exception is authorized by the extraction.

From the repository root, capture the graph for each matching restore:

```powershell
python scripts/architecture/dependency_graph.py --profile jit --restore --output artifacts/dependencies/jit.json
python scripts/architecture/dependency_graph.py --profile audit --restore --output artifacts/dependencies/audit.json
python scripts/architecture/dependency_graph.py --profile native --rid linux-x64 --restore --output artifacts/dependencies/native.json
python -m unittest discover -s scripts/architecture -p 'test_*.py'
```

Each profile must restore before reading its assets; normal and native package
graphs share intermediate paths. Do not run different profile restores/builds
concurrently in the same checkout. The central MSBuild targets also run during
ordinary host builds and publishes. CI retains inventories alongside native
publish manifests and runtime evidence.

The measurement record must identify both exact source revisions, SDK and compiler,
RID, Release/profile properties, native flags and PGO inputs. Executable bytes and
runtime publish bytes are distinct. For images, compare the same runtime base
digest and distinguish unpacked image bytes from compressed transfer bytes. Artifact
ZIP size is neither metric. The historical win-x64 SDK 10.0.303 baseline is not a
comparable Linux measurement. Final PR evidence records measured values and any
unavoidable toolchain/base differences; a successful publish does not by itself
establish a size improvement.
