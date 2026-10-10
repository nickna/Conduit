# JSON source-generation contract inventory

Status: Accepted

Issues: #1357, #1370 (parent epic #1368)

Current-state review: 2026-10-09, after integration PR #1489.

## Decision

Conduit uses explicit `JsonSerializerContext` metadata for its HTTP, messaging,
Redis, SignalR, provider, and internal persistence contracts. Metadata generation
keeps existing host serializer policies authoritative, so this change does not
alter property names, enum representation, null/default handling, or
polymorphism.

The original #1357 decision covered representative contracts and retained a
reflection fallback for the remaining graphs. Phase 1 of #1368 subsequently
removed the production `DefaultJsonTypeInfoResolver` fallbacks. Both service
projects now set `JsonSerializerIsReflectionEnabledByDefault=false` for ordinary
JIT builds as well as native builds. Admin and Gateway configure generated HTTP
resolver chains and closed generic `JsonStringEnumConverter<TEnum>` instances.
Wolverine and JSON SignalR also use generated metadata.

The focused compatibility test executable separately disables reflection
globally. Its fixture coverage proves the listed representative wire contracts;
it does not prove every endpoint or provider workload executes in a native
process. The supported native workload and remaining exclusions are recorded in
[the feature matrix](../operations/gateway-native-aot-feature-matrix.md) and
[the audit checkpoint](../operations/native-aot-audit.md).

## Inventory

| Contract family | Context and owner | Wire options | Producer / consumer | Compatibility coverage |
| --- | --- | --- | --- | --- |
| OpenAI-compatible chat and errors | `CoreHttpJsonContext` / Core | `snake_case`, omit nulls; existing host converters remain authoritative | Gateway endpoints and SDK clients | `gateway-chat-response.json`; production Gateway options also exercised |
| Gateway discovery, models, files, uploads, and cancellation | `GatewayHttpJsonContext` / Gateway | `snake_case`, omit nulls | Gateway endpoints / API clients | `gateway-model-list.json`; generated metadata and discovery dialect checks |
| Admin requests, errors, lists, and batch-spend status | `AdminHttpJsonContext` and `AdminHttpResponseJsonContext` / Admin | `camelCase`, case-insensitive reads | Admin minimal APIs / WebAdmin and API clients | `admin-problem-details.json`; generated metadata, collection-wrapper, and `ProviderType` regression checks |
| Wolverine topology messages | `CoreMessagingJsonContext` / Core | Existing Pascal-case CLR names, numeric enums, and explicit JSON attributes | Gateway and Admin publishers / Wolverine handlers | `wolverine-spend-update.json`; runtime resolver-chain serialization |
| Gateway Redis cache and invalidation payloads | `GatewayRedisJsonContext` / Gateway | Existing Pascal-case names, case-insensitive reads | Gateway instances across a rolling deployment | `redis-model-cost-legacy.json` old-to-new and new-to-old; `redis-virtual-key-invalidation.json` |
| Redis webhook reliability state | `CoreRedisJsonContext` / Core | Existing Pascal-case names, case-insensitive reads | Webhook circuit-breaker instances | `redis-webhook-circuit-state.json` |
| Live SignalR notifications | `ConfigurationSignalRJsonContext` / Configuration; runtime adapter owned by SignalR | `camelCase`, case-insensitive reads | Gateway hubs / WebAdmin | `signalr-virtual-key-created.json`; generated metadata and native JSON SignalR process coverage |
| Bedrock requests, responses, model discovery, and streams | `ProvidersJsonContext` / Providers | `camelCase`, omit nulls, case-insensitive reads | Bedrock client / AWS Bedrock | `bedrock-converse-response.json`; existing provider tests |
| OpenRouter catalog | `ProvidersJsonContext` / Providers | `camelCase`, omit nulls, case-insensitive reads | OpenRouter client / OpenRouter API | Reflection-disabled metadata closure; existing provider tests |

Additional generated boundaries introduced during the epic are:

| Boundary | Contexts and owner | Verification boundary |
| --- | --- | --- |
| Admin internal payloads and cleanup cache | `AdminInternalJsonContext`, `MediaCleanupRedisJsonContext` / Admin | Generated internal/persisted metadata; cleanup cache regression tests |
| Gateway task/accounting/tool metadata | `GatewayInternalJsonContext` / Gateway | Named accounting shape and generated metadata checks |
| Core internal, pricing, and async-task persistence | `CoreInternalJsonContext`, `CorePricingJsonContext`, `AsyncTaskJsonContext` / Core | Persisted/cache and pricing regression tests |
| Shared application cache | `ApplicationCacheJsonContext` / Core | Cache contracts and independent-process probes |
| Configuration settings and model capabilities | `ConfigurationJsonContext`, `ConfigurationModelJsonContext` / Configuration | Generated persistence metadata and legacy reads |
| Function execution and provider payloads | `FunctionsJsonContext`, `FunctionProviderJsonContext` / Functions | Provider/pricing/cache tests; full native function workload remains outstanding |
| Security middleware and caches | `SecurityCacheJsonContext` / Security | Generated cache and error metadata tests |

The `[JsonSerializable]` declarations in each context are the concrete type
inventory. The table groups their ownership and verification boundaries; adding
a type to a context alone does not establish end-to-end native coverage.

Batch-spend Redis accumulation uses scalar Redis values and hashes rather than
JSON documents. Its durable flush event is included in
`CoreMessagingJsonContext`.

## Compatibility boundaries

- The removed queued SignalR polymorphic envelope and its
  `DefaultJsonTypeInfoResolver` modifier are not present in the current
  Gateway/Admin path. The live strongly typed notification DTOs are generated
  without introducing a discriminator or changing the wire contract.
- Converted graphs do not require reflective discovery for arbitrary CLR
  objects. Existing OpenAI-compatible extension slots accept only supported
  JSON primitives, `JsonElement`, or source-generated bounded collection
  shapes; the reflection-disabled suite exercises those paths. Bedrock
  tool-choice marker objects now use `JsonElement` instead of unbounded
  `object`.
- Service HTTP options use closed generic enum converters. Admin preserves
  `camelCase` string enum names and Gateway preserves `snake_case` names;
  persisted/message contexts retain their established numeric or explicitly
  attributed representation. The reflection-disabled suite checks the
  `ProviderType.OpenAICompatible` and function execution-state wire names.
- Redis fixtures model a previous release using an explicit reflection resolver.
  They prove both previous-release payloads read on the new release and
  new-release payloads read with the previous serializer policy.

## Reflection-disabled verification

`ConduitLLM.SerializationTests` sets
`JsonSerializerIsReflectionEnabledByDefault=false`. Its 16 tests check
representative fixtures in both directions, verify that an unregistered type
fails, and cover service metadata, enum names, Admin collection wrappers, and
the navigation-free Redis virtual-key shape. Service `.csproj` feature switches
do not apply automatically to a separate test executable; the broader unit and
integration projects do not globally disable reflection defaults.

The historical Admin native endpoint-materialization failure for `ProviderType`
is covered by generated nullable-enum metadata and enum-name regression checks.
Both services also have native OpenAPI boot gates. Public OpenAPI drift,
provider tests, cache contracts, and published native process gates remain
necessary alongside these serialization fixtures.

Run:

```powershell
dotnet test Tests/ConduitLLM.SerializationTests/ConduitLLM.SerializationTests.csproj -c Release
```

## Benchmark

BenchmarkDotNet, .NET 10.0.10, Intel Core i5-12400F, 8 measured iterations:

| Serializer metadata | Mean | Allocated | Ratio |
| --- | ---: | ---: | ---: |
| Reflection | 774.0 ns | 920 B | 1.00 |
| Source-generated | 737.9 ns | 920 B | 0.95 |

The representative chat response is approximately 4.7% faster with unchanged
per-operation allocation.

Run:

```powershell
dotnet run --project Tests/ConduitLLM.Benchmarks/ConduitLLM.Benchmarks.csproj -c Release -- --filter *JsonSourceGeneration*
```
