# ConduitLLM.Functions

A comprehensive function execution framework for Conduit that enables integration with third-party API services (search engines, RAG systems, data providers) with built-in billing, distributed execution, and cost tracking.

## Table of Contents

- [Purpose](#purpose)
- [Architecture Overview](#architecture-overview)
- [Database Design](#database-design)
- [Key Features](#key-features)
- [Execution Models](#execution-models)
- [Pricing Models](#pricing-models)
- [Distributed Execution](#distributed-execution)
- [Integration Points](#integration-points)

## Purpose

The Functions module provides a unified framework for:

1. **Function Provider Management**: Configure and manage multiple instances of function providers (Exa.ai, Perplexity, Tavily, custom RAG systems)
2. **Execution Tracking**: Complete lifecycle tracking from request to completion/failure
3. **Cost Management**: Flexible pricing models with reserve-then-commit billing
4. **Distributed Async Execution**: Leasing mechanism for scalable worker-based processing
5. **Audit Trail**: Comprehensive audit logging for compliance and billing verification
6. **Balance Enforcement**: Integration with Conduit's billing system to block/permit execution based on account balance

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

```
┌─────────────────────────────────────────────────────────────────┐
│                         Conduit APIs                             │
│                    (Gateway API / Admin API)                        │
└────────────────────────┬────────────────────────────────────────┘
                         │
                         ├──► REST Endpoints (enumerate/execute)
                         │
┌────────────────────────┴────────────────────────────────────────┐
│                    Functions Framework                           │
├─────────────────────────────────────────────────────────────────┤
│  Configuration Layer:                                            │
│  • FunctionConfiguration (provider instances)                    │
│  • FunctionCredential (API keys with load balancing)            │
│  • FunctionCost (flexible pricing models)                        │
│  • FunctionCostMapping (config-to-cost links)                   │
├─────────────────────────────────────────────────────────────────┤
│  Execution Layer:                                                │
│  • FunctionExecution (lifecycle tracking)                        │
│  • ExecutionState machine (Pending→Running→Completed/Failed)    │
│  • Leasing mechanism for distributed workers                     │
│  • Optimistic concurrency control                                │
├─────────────────────────────────────────────────────────────────┤
│  Audit Layer:                                                    │
│  • FunctionExecutionAudit (complete event trail)                │
│  • 11 event types for billing verification                       │
└─────────────────────────────────────────────────────────────────┘
                         │
                         ├──► MassTransit (async execution)
                         ├──► PostgreSQL (persistence)
                         └──► Billing System (reserve/commit)
```

### Multi-Instance Pattern

Following Conduit's Provider architecture, Functions support **multiple configurations of the same provider type**:

- **Provider ID**: Canonical identifier for configuration instances
- **ProviderType**: Categorizes by API type (Exa, Perplexity, etc.)
- **ConfigurationName**: User-facing display name (e.g., "Production Exa", "Dev Exa")

Example:
```csharp
// Multiple Exa configurations
FunctionConfiguration prodExa = new()
{
    ProviderType = FunctionProviderType.Exa,
    ConfigurationName = "Production Exa",
    BaseUrl = "https://api.exa.ai",
    // ... credentials, costs
};

FunctionConfiguration devExa = new()
{
    ProviderType = FunctionProviderType.Exa,
    ConfigurationName = "Development Exa",
    BaseUrl = "https://api.exa.ai",
    // ... different credentials/costs
};
```

## Database Design

### Entity Relationship Diagram

```
┌─────────────────────────┐
│ FunctionConfiguration   │
│─────────────────────────│
│ PK: Id                  │
│     ProviderType (enum) │◄────┐
│     ConfigurationName   │     │
│     Purpose (enum)      │     │
│     DefaultExecutionMode│     │
│     BaseUrl             │     │
│     IsEnabled           │     │
│     ProviderSettings    │     │ 1:N
└─────────────────────────┘     │
         │                       │
         │ 1:N                   │
         ▼                       │
┌─────────────────────────┐     │
│ FunctionCredential      │     │
│─────────────────────────│     │
│ PK: Id                  │     │
│ FK: FunctionConfigId    │─────┘
│     ApiKey (encrypted)  │
│     CredentialGroup     │
│     IsPrimary           │
│     IsEnabled           │
└─────────────────────────┘


┌─────────────────────────┐
│ FunctionCost            │
│─────────────────────────│
│ PK: Id                  │
│     CostName            │◄────┐
│     PricingModel (enum) │     │
│     CostPerExecution    │     │
│     CostPerResult       │     │
│     CostPerToken        │     │ N:M via
│     TieredPricing       │     │ CostMapping
│     EffectiveDate       │     │
│     ExpiryDate          │     │
│     Priority            │     │
└─────────────────────────┘     │
                                │
┌─────────────────────────┐     │
│ FunctionCostMapping     │     │
│─────────────────────────│     │
│ PK: Id                  │     │
│ FK: FunctionConfigId    │─────┤
│ FK: FunctionCostId      │─────┘
│     IsActive            │
└─────────────────────────┘


┌─────────────────────────┐
│ FunctionExecution       │
│─────────────────────────│
│ PK: Id (Guid)           │
│ FK: FunctionConfigId    │
│ FK: VirtualKeyId        │
│     ExecutionMode       │
│     State (enum)        │◄────┐
│     RequestedAt         │     │
│     StartedAt           │     │
│     CompletedAt         │     │
│     Duration            │     │ 1:N
│     RequestJson         │     │
│     ResponseJson        │     │
│     EstimatedCost       │     │
│     ActualCost          │     │
│     LeasedBy            │     │
│     LeaseExpiryTime     │     │
│     Version             │     │
│     WebhookUrl          │     │
└─────────────────────────┘     │
                                │
┌─────────────────────────┐     │
│ FunctionExecutionAudit  │     │
│─────────────────────────│     │
│ PK: Id (Guid)           │     │
│ FK: FunctionExecutionId │─────┘
│     Timestamp           │
│     EventType (enum)    │
│     EventDetails (json) │
│     Cost                │
│     FailureReason       │
└─────────────────────────┘
```

### Table Descriptions

#### FunctionConfiguration
Defines a function provider instance with its settings and capabilities.

**Key Fields:**
- `ProviderType`: Enum defining the provider API type (Exa, Perplexity, Tavily, etc.)
- `Purpose`: Function category (Search, Answer, RAG_Search, RAG_Save, etc.)
- `DefaultExecutionMode`: Synchronous or Asynchronous execution
- `ProviderSettings`: JSONB column for provider-specific configuration
- `TimeoutSeconds`, `MaxRetries`: Execution policies

#### FunctionCredential
API credentials for function providers with load balancing support.

**Key Fields:**
- `ApiKey`: Encrypted API key
- `CredentialGroup`: Grouping for load balancing (0-255)
- `IsPrimary`: Primary credential flag (one per configuration)
- `IsEnabled`: Active/inactive flag for credential rotation

**Load Balancing:**
- Multiple credentials can be grouped using `CredentialGroup`
- One credential per configuration must be marked `IsPrimary`
- Failed credentials can be disabled without deletion

#### FunctionCost
Flexible cost definitions supporting multiple pricing models.

**Supported Pricing Models:**
1. **FlatRate**: Fixed cost per execution
2. **PerResult**: Cost based on number of results returned
3. **PerToken**: Token-based pricing (like LLM costs)
4. **Tiered**: Volume-based pricing tiers (JSONB configuration)
5. **TimeBased**: Cost per minute/hour
6. **Hybrid**: Combination of multiple models (JSONB configuration)

**Key Fields:**
- `PricingModel`: Enum defining the pricing strategy
- `EffectiveDate`, `ExpiryDate`: Temporal validity
- `Priority`: For selecting among multiple active costs
- `TieredPricing`, `PricingConfiguration`: JSONB for complex pricing

#### FunctionCostMapping
Links configurations to cost definitions (many-to-many).

**Purpose:**
- One configuration can have multiple cost mappings (e.g., one active, others historical)
- Enables cost changes without modifying configurations
- Supports A/B testing of pricing models

#### FunctionExecution
Tracks the complete lifecycle of a function execution.

**State Machine:**
```
Pending → Running → Completed
                 ↘ Failed
                 ↘ Cancelled
                 ↘ TimedOut
```

**Key Fields for Distributed Execution:**
- `LeasedBy`: Worker instance ID that claimed this execution
- `LeaseExpiryTime`: When the lease expires (enables recovery)
- `Version`: Optimistic concurrency control

**Cost Fields:**
- `EstimatedCost`: Reserved upfront from virtual key balance
- `ActualCost`: Final cost after execution (refund difference)
- `CostCalculationDetails`: JSONB breakdown of cost calculation

**Retry Logic:**
- `RetryCount`: Number of retry attempts
- `NextRetryAt`: Scheduled retry time with exponential backoff

**Progress Tracking:**
- `ProgressPercentage`: 0-100 for long-running executions
- `StatusMessage`: Human-readable status updates

#### FunctionExecutionAudit
Complete audit trail for compliance and billing verification.

**Event Types (11 total):**
1. `ExecutionRequested`
2. `BalanceReserved`
3. `ExecutionStarted`
4. `ProgressUpdated`
5. `ExecutionCompleted`
6. `ExecutionFailed`
7. `ExecutionCancelled`
8. `BalanceCommitted`
9. `BalanceRefunded`
10. `RetryScheduled`
11. `WebhookDelivered`

**Key Fields:**
- `EventDetails`: JSONB for event-specific data
- `Cost`: Cost associated with this event (for reserve/commit/refund)
- `IsEstimated`: Whether cost is estimated or actual

## Key Features

### 1. Reserve-Then-Commit Billing

```csharp
// Execution lifecycle
1. User requests function execution
2. System calculates EstimatedCost
3. Reserve EstimatedCost from VirtualKey balance
4. Execute function
5. Calculate ActualCost based on response
6. Commit ActualCost to billing
7. Refund (EstimatedCost - ActualCost) if any
```

**Customer-Friendly:** Only charge for successful executions (HTTP 200).

### 2. Distributed Async Execution

**Leasing Mechanism:**
```csharp
// Worker process
var execution = await repository.LeaseNextPendingAsync(
    workerId: "worker-instance-123",
    leaseDuration: TimeSpan.FromMinutes(5)
);

// Atomic lease acquisition prevents duplicate processing
// If worker crashes, lease expiry allows recovery
```

**Optimistic Concurrency:**
```csharp
execution.State = ExecutionState.Completed;
execution.Version++; // Incremented on every update

var success = await repository.UpdateAsync(execution);
// Returns false if another worker modified it first
```

### 3. Flexible Cost Configuration

**Example: Tiered Pricing**
```json
{
  "tiers": [
    {"upTo": 100, "costPerResult": 0.01},
    {"upTo": 1000, "costPerResult": 0.008},
    {"upTo": null, "costPerResult": 0.005}
  ]
}
```

**Example: Hybrid Pricing**
```json
{
  "base": 0.10,
  "perToken": 0.0001,
  "perResult": 0.005
}
```

### 4. Multi-Instance Provider Support

```csharp
// Different Exa configurations for different use cases
var searchConfig = await repository.GetByConfigurationNameAsync("Production Exa Search");
var answerConfig = await repository.GetByConfigurationNameAsync("Production Exa Answer");

// Both use ProviderType.Exa but different purposes
searchConfig.Purpose == FunctionPurpose.Search
answerConfig.Purpose == FunctionPurpose.Answer
```

## Execution Models

### Synchronous Execution
- Immediate response to API caller
- Timeout enforced (default: configuration setting)
- Best for: Quick searches, simple lookups

### Asynchronous Execution
- Immediate acknowledgment with execution ID
- Processing via MassTransit workers
- Webhook notification on completion
- Best for: Long-running tasks, batch operations

## Pricing Models

### 1. FlatRate
Fixed cost per execution regardless of results.
```csharp
Cost = CostPerExecution
```

### 2. PerResult
Cost based on number of results returned.
```csharp
Cost = ResultCount × CostPerResult
```

### 3. PerToken
Token-based pricing (similar to LLM costs).
```csharp
Cost = TokenCount × CostPerToken
```

### 4. Tiered
Volume-based pricing with configurable tiers.
```csharp
Cost = CalculateFromTiers(ResultCount, TieredPricing)
```

### 5. TimeBased
Cost based on execution duration.
```csharp
Cost = DurationMinutes × CostPerMinute
```

### 6. Hybrid
Combination of multiple pricing components.
```csharp
Cost = BaseCost + (Tokens × CostPerToken) + (Results × CostPerResult)
```

## Distributed Execution

### Worker Pattern

```csharp
public class FunctionExecutionWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Lease next pending execution
            var execution = await _repository.LeaseNextPendingAsync(
                workerId: _workerId,
                leaseDuration: TimeSpan.FromMinutes(5),
                cancellationToken: stoppingToken
            );

            if (execution != null)
            {
                await ProcessExecutionAsync(execution, stoppingToken);
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
```

### Lease Recovery

```csharp
// Find executions with expired leases
var expiredLeases = await _repository.GetExpiredLeasesAsync();

foreach (var execution in expiredLeases)
{
    // Clear lease to allow re-acquisition
    execution.LeasedBy = null;
    execution.LeaseExpiryTime = null;
    await _repository.UpdateAsync(execution);
}
```

## Integration Points

### 1. REST APIs
- **Gateway API**: `/api/functions/execute` - Execute functions
- **Gateway API**: `/api/functions/available` - List available functions
- **Gateway API**: `/api/functions/status/{id}` - Check execution status
- **Admin API**: `/api/admin/functions` - Manage configurations
- **Admin API**: `/api/admin/functions/{id}/credentials` - Manage credentials

### 2. MassTransit
- **ExecuteFunctionCommand**: Async execution requests
- **FunctionExecutionCompletedEvent**: Completion notifications
- **FunctionExecutionFailedEvent**: Failure notifications

### 3. Billing System
- **IVirtualKeyRepository**: Balance checks and reservations
- **IBillingService**: Cost commits and refunds
- **BillingAuditEvent**: Audit trail integration

### 4. Provider Implementations
- Concrete implementations in `ConduitLLM.Providers` project
- Factory pattern for provider client instantiation
- Follow established `ILLMClient` patterns

## Usage Examples

### Creating a Function Configuration

```csharp
var config = new FunctionConfiguration
{
    ProviderType = FunctionProviderType.Exa,
    ConfigurationName = "Production Exa Search",
    Purpose = FunctionPurpose.Search,
    DefaultExecutionMode = ExecutionMode.Synchronous,
    BaseUrl = "https://api.exa.ai",
    IsEnabled = true,
    TimeoutSeconds = 30,
    MaxRetries = 3,
    ProviderSettings = JsonSerializer.Serialize(new
    {
        type = "neural",
        numResults = 10
    })
};

var configId = await _configRepository.CreateAsync(config);
```

### Adding Credentials

```csharp
var credential = new FunctionCredential
{
    FunctionConfigurationId = configId,
    ApiKey = "exa_api_key_here", // Will be encrypted
    CredentialGroup = 0,
    IsPrimary = true,
    IsEnabled = true,
    CredentialName = "Primary Production Key"
};

await _credentialRepository.CreateAsync(credential);
```

### Defining Costs

```csharp
var cost = new FunctionCost
{
    CostName = "Exa Search - Standard Pricing",
    PricingModel = FunctionPricingModel.PerResult,
    CostPerResult = 0.01m,
    IsActive = true,
    EffectiveDate = DateTime.UtcNow,
    Priority = 1
};

var costId = await _costRepository.CreateAsync(cost);

// Map cost to configuration
await _costMappingRepository.CreateAsync(new FunctionCostMapping
{
    FunctionConfigurationId = configId,
    FunctionCostId = costId,
    IsActive = true
});
```

### Executing a Function

```csharp
var execution = new FunctionExecution
{
    FunctionConfigurationId = configId,
    VirtualKeyId = virtualKeyId,
    ExecutionMode = ExecutionMode.Synchronous,
    State = ExecutionState.Pending,
    RequestJson = JsonSerializer.Serialize(new
    {
        query = "latest AI research papers",
        numResults = 5
    }),
    EstimatedCost = 0.05m // 5 results × $0.01
};

var executionId = await _executionRepository.CreateAsync(execution);

// Process execution (sync or async via MassTransit)
```

## See Also

- [Provider Architecture](../../../docs/architecture/provider-system/provider-architecture.md)
- [Model & Cost Mapping](../../../docs/architecture/provider-system/model-and-cost-mapping.md)
- [Repository & Data Access Patterns](../../../docs/architecture/patterns/repository-and-data-access.md)
- [Async Media Generation](../../../docs/architecture/media-generation/async-media-generation.md) - Similar distributed execution patterns
