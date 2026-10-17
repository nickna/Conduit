# ConduitLLM.Functions

Function provider configuration, inline execution, usage calculation and execution records for Conduit. The current clients support Exa, Tavily and MCP.

## Table of Contents

- [Purpose](#purpose)
- [Architecture Overview](#architecture-overview)
- [Database Design](#database-design)
- [Key Features](#key-features)
- [Execution Models](#execution-models)
- [Pricing Models](#pricing-models)
- [Distributed Execution](#distributed-execution)
- [Integration Points](#integration-points)
- [Usage Examples](#usage-examples)

## Purpose

The Functions module provides:

1. Multiple named configurations of a function provider.
2. Provider-global and configuration-scoped credentials.
3. Usage-based cost estimation and calculation.
4. Durable execution records and repository lifecycle operations.
5. Audit entities and vocabulary for function executions and agentic tool calls.

Gateway authentication and accounting middleware enforce access and settle recorded costs. Creating an execution entity alone does not invoke a provider or charge a virtual key.

## Architecture Overview

The function entities, enums, identity/audit interfaces and five repository
interfaces compile into `ConduitLLM.Persistence.Abstractions` from linked sources.
Their namespaces and signatures stay the same, and Functions forwards all 24
public types so existing compiled consumers and old assembly-qualified names
continue to resolve. Configuration depends on this neutral leaf rather than the
function provider implementations. Clients, services, security, DTOs and JSON
contexts remain in Functions, which Core references explicitly. This ownership
boundary does not change the EF repository implementations or prove native
function persistence.

### Core Components

```text
Gateway FunctionsEndpoints
    → FunctionExecutionService
        → configuration and credential repositories
        → FunctionClientFactory → Exa / Tavily / MCP
        → usage and cost calculation
        → FunctionExecutionRepository
    → request accounting middleware

Admin function configuration changes
    → Wolverine FunctionConfigurationChanged
    → discovery cache invalidation handlers
```

`FunctionExecutionService.ExecuteAsync` validates the configuration, estimates cost, writes a `Running` record, awaits the provider call, calculates usage and actual cost, and writes the final result. Provider results and incurred cost are retained in the returned execution when subsequent bookkeeping fails; a failed database write cannot guarantee a durable final state.

### Multi-Instance Pattern

Configurations have an integer `Id`, a `ProviderType`, a display `ConfigurationName` and a `Purpose`. Several configurations can use the same provider with different settings, schemas or credentials. Exa, Tavily and MCP have concrete clients in `ConduitLLM.Functions.Providers`; other enum entries do not imply an implemented client.

## Database Design

| Entity | Role |
| --- | --- |
| `FunctionConfiguration` | Provider instance, parameter schema and execution settings. |
| `FunctionCredential` | Provider-global credential, or a credential scoped by nullable `FunctionConfigurationId`. |
| `FunctionCost` | Pricing definition, active dates and selection priority. |
| `FunctionCostMapping` | Active configuration-to-cost association. |
| `FunctionExecution` | Request, provider result, calculated cost and lifecycle state. |
| `FunctionExecutionAudit` | Execution audit record vocabulary. |
| `FunctionCallAudit` | Agentic tool-call audit record. |

### FunctionConfiguration

- `ProviderType`: Provider API type, such as Exa, Tavily or MCP.
- `Purpose`: `Search`, `Answer`, `ContentRetrieval` or `RAG`.
- `DefaultExecutionMode`: Stored execution-mode value; see [Execution Models](#execution-models).
- `ProviderSettings`, `ParameterSchema`: JSONB settings and input schema.
- `BaseUrl`, `TimeoutSeconds`, `CacheTtlMinutes`, `MaxRetries`: Configuration fields consumed according to the provider implementation.

Function discovery returns enabled configurations. Purpose and provider filters parse enum names without case sensitivity; unparseable filter text is currently ignored. Use a defined purpose such as `RAG` when filtering.

### FunctionCredential

Credentials have `ProviderType`, optional `FunctionConfigurationId`, `ApiKey`, `BaseUrl`, `Organization`, `FunctionAccountGroup`, `IsPrimary`, `IsEnabled` and `KeyName`.

Selection prefers an enabled credential for the current configuration, then an enabled provider-global credential. Within each scope, a primary credential takes precedence. Credentials scoped to a different configuration are not selected.

The Admin credential endpoints protect configuration-scoped secrets through `IFunctionCredentialProtector`. The execution service reveals the selected secret in memory. Direct repository writes do not encrypt automatically; legacy provider-global plaintext credentials remain readable.

### FunctionCost and FunctionCostMapping

Costs carry a pricing model, rates, JSONB pricing configuration, effective/expiry dates and priority. Configurations can map to several costs. `FunctionCostService.GetCostForConfigurationAsync` considers active mappings and eligible costs before selecting by priority and effective date.

### FunctionExecution

The entity supports `Pending`, `Running`, `Completed`, `Failed`, `Cancelled` and `TimedOut` states. The inline service starts its normal record in `Running` and records the provider outcome.

`EstimatedCost` is a calculated estimate. `ActualCost` records incurred cost; these fields do not themselves reserve or refund a balance. `CostCalculationDetails` stores the usage/cost breakdown. Lease, retry, progress and webhook fields exist in the entity, but their presence does not imply that the inline service schedules workers, retries or webhooks.

The repository uses `Version` for optimistic concurrency. `UpdateAsync` increments the version internally and returns false on a conflict; callers should not increment it separately.

### Audit Entities

`FunctionAuditEventType` defines `ExecutionRequested`, `ExecutionStarted`, `ExecutionProgress`, `ExecutionCompleted`, `ExecutionFailed`, `CostCalculated`, `SpendUpdated`, `SpendUpdateFailed`, `RetryScheduled`, `ExecutionCancelled` and `ExecutionTimedOut`. These are schema vocabulary, not a guarantee that every execution emits a corresponding `FunctionExecutionAudit` row.

Agentic tool-call auditing has its own `FunctionCallAuditEventType` and `IFunctionCallAuditService`.

## Key Features

### Cost Calculation and Gateway Accounting

The execution service calculates an estimate before the provider call and actual cost from its usage result. The Gateway endpoint records direct-cost evidence in the request accounting context; middleware settles that evidence through the shared billing flow. A provider call can incur cost even when later bookkeeping fails. Direct calls to the execution service return calculated costs but do not run Gateway middleware.

### Structured Provider Schemas

Configurations can carry parameter schemas for discovery and validation. MCP configurations discover provider-native tools, while fixed-schema providers map a configuration to its implicit function.

### Multiple Configurations

```csharp
var searchConfig = await repository.GetByNameAsync("Production Exa Search");
var answerConfig = await repository.GetByNameAsync("Production Exa Answer");
```

## Execution Models

`ExecuteAsync` currently awaits the provider inline for both stored execution-mode values. `DefaultExecutionMode` is copied into the execution record; setting it to `Asynchronous` does not enqueue a command or return an immediate acknowledgement.

There is no registered function execution worker or function completion webhook dispatcher. Wolverine currently handles function configuration/discovery cache events, described below.

## Pricing Models

| Model | Calculation |
| --- | --- |
| `FlatRate` | Fixed execution cost. |
| `PerResult` | Result count multiplied by the configured rate. |
| `PerToken` | Token usage multiplied by the configured rate. |
| `Tiered` | Usage evaluated against configured pricing tiers. |
| `TimeBased` | Duration evaluated against the configured time rate. |
| `Hybrid` | Configured combination of supported cost components. |

Refer to `FunctionCostCalculationService` and its model-specific partial files for the accepted pricing JSON and usage fields.

## Distributed Execution

`IFunctionExecutionRepository` exposes `LeaseNextPendingAsync`, `GetExpiredLeasesAsync` and `GetReadyForRetryAsync`. These are persistence primitives for a caller-owned workflow. The normal Gateway function request does not use them, and no function worker loop is registered.

A caller implementing a worker must handle lease recovery, concurrency conflicts and provider-call duplication explicitly. The current lease operation claims a pending record by setting its lease fields and incrementing its version; it leaves the state unchanged. Clearing lease fields alone does not turn a running record back into a pending job.

## Integration Points

### REST APIs

| Method | Route | Purpose |
| --- | --- | --- |
| POST | `/v1/conduit/functions/execute` | Execute a configured function inline. |
| GET | `/v1/conduit/functions/executions/{executionId}` | Read an execution owned by the authenticated virtual key. |
| GET | `/v1/conduit/discovery/functions` | Discover enabled configurations with optional `purpose` and `providerType` filters. |
| GET | `/v1/conduit/discovery/functions/{functionConfigurationId}/parameters` | Read a configuration's structured parameter schema. |
| GET / POST / PATCH / DELETE | `/v1/admin/function-configurations` and `/{id}` | Manage configurations; see endpoint-specific methods in OpenAPI. |
| GET / POST / PATCH / DELETE | `/v1/admin/function-credentials` and `/{id}` | Manage credentials. |
| GET | `/v1/admin/function-credentials/configuration/{functionConfigurationId}` | List credentials for the configuration's provider type. |
| GET | `/v1/admin/function-executions/{id}` | Inspect an execution record. |
| GET | `/v1/admin/function-executions/virtual-keys/{virtualKeyId}` | Inspect a virtual key's execution records. |

Use each host's OpenAPI document for request contracts and the complete management route set.

### Wolverine

`FunctionConfigurationChanged` invalidates cached function discovery after configuration changes. `FunctionDiscoveryCacheInvalidationRequested` requests explicit discovery cache invalidation. Both hosts have generated Wolverine handlers for these shared contracts. Production messaging uses Wolverine with PostgreSQL durability; these events do not execute functions.

The supported transport is defined by [`MessagingBackend`](../ConduitLLM.Configuration/Messaging/MessagingBackend.cs); host registration lives in [Gateway](../../Services/ConduitLLM.Gateway/Program.Messaging.cs) and [Admin](../../Services/ConduitLLM.Admin/Program.Messaging.cs). Use these registrations when extending configuration events or their handlers.

### Provider Implementations

`FunctionClientFactory` creates Exa, Tavily and MCP clients from `ConduitLLM.Functions.Providers` through `IFunctionClient`. Provider HTTP and JSON handling lives with those clients.

## Usage Examples

These examples assume repositories and services resolved from the normal JIT host. They do not establish NativeAOT support for function execution.

### Discovering RAG Configurations

```http
GET /v1/conduit/discovery/functions?purpose=RAG
Authorization: Bearer YOUR_VIRTUAL_KEY
```

`RAG` is the enum purpose for this filter. An unparseable purpose is ignored and returns the enabled configurations matching any remaining filter; it does not select a RAG subcategory.

### Creating a Configuration

```csharp
var configId = await configRepository.CreateAsync(new FunctionConfiguration
{
    ProviderType = FunctionProviderType.Exa,
    ConfigurationName = "Production Exa Search",
    Purpose = FunctionPurpose.Search,
    DefaultExecutionMode = ExecutionMode.Synchronous,
    BaseUrl = "https://api.exa.ai",
    IsEnabled = true,
    TimeoutSeconds = 30,
    ProviderSettings = """{"type":"neural","numResults":10}"""
});
```

### Adding a Configuration-Scoped Credential

```csharp
await credentialRepository.CreateAsync(new FunctionCredential
{
    ProviderType = FunctionProviderType.Exa,
    FunctionConfigurationId = configId,
    ApiKey = credentialProtector.Protect(apiKey),
    FunctionAccountGroup = 0,
    IsPrimary = true,
    IsEnabled = true,
    KeyName = "Primary Production Key"
});
```

The Admin API performs scoped-secret protection for API callers. Repository callers must perform it explicitly as above.

### Defining a Cost

```csharp
var costId = await costRepository.CreateAsync(new FunctionCost
{
    ProviderType = FunctionProviderType.Exa,
    CostName = "Exa Search - Standard Pricing",
    PricingModel = FunctionPricingModel.PerResult,
    CostPerResult = 0.01m,
    IsActive = true,
    EffectiveDate = DateTime.UtcNow,
    Priority = 1
});
await costMappingRepository.CreateAsync(new FunctionCostMapping
{
    FunctionConfigurationId = configId,
    FunctionCostId = costId,
    IsActive = true
});
```

### Executing a Function

```csharp
var execution = await executionService.ExecuteAsync(
    configId,
    virtualKeyId,
    new Dictionary<string, object>
    {
        ["query"] = "latest AI research papers",
        ["numResults"] = 5
    },
    cancellationToken: cancellationToken);
```

Use the Gateway execution route when the request must go through its authentication, parameter validation and accounting middleware.

For retryable HTTP operations, send `Idempotency-Key` and reuse it with the same function,
parameters and metadata. PostgreSQL claims and execution-specific ledger settlement protect
retries across Gateway instances and restarts. See [retry and recovery semantics](../../docs/api-guides/function-resources.md#retrying-an-execution), including permanent claim retention and uncertain provider outcomes.

Exa/Tavily execution and credential-check POSTs have no automatic transport
retries. A lost response can still follow a provider charge; use the documented
[provider reconciliation guidance](../../docs/api-guides/function-resources.md#provider-transport-retries-and-uncertain-charges)
before dispatching another operation.

## See Also

- [Function Resources API](../../docs/api-guides/function-resources.md)
- [Durable Media Dispatch](../../docs/operations/durable-media-dispatch.md)
- [Indeterminate Media Tasks](../../docs/operations/indeterminate-media-tasks.md)
