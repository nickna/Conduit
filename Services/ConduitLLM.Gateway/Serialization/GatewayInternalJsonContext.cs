using System.Text.Json.Serialization;

using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.Services.SpendNotification;
using ConduitLLM.Gateway.Utilities;

namespace ConduitLLM.Gateway.Serialization;

/// <summary>
/// Source-generated metadata for Gateway-owned cache, audit, and SSE payloads.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BillingReconciliationMetadata))]
[JsonSerializable(typeof(ChatToolCallsMetadata))]
[JsonSerializable(typeof(FunctionCostMetadata))]
[JsonSerializable(typeof(FunctionExecutionMetadata))]
[JsonSerializable(typeof(FunctionExecutionResultsMetadata))]
[JsonSerializable(typeof(MediaTaskAccountingMetadata))]
[JsonSerializable(typeof(MediaUsageMetadata))]
[JsonSerializable(typeof(PerformanceMetrics))]
[JsonSerializable(typeof(RedisCircuitBreakerMetadata))]
[JsonSerializable(typeof(SpendNotificationInstanceData))]
[JsonSerializable(typeof(StreamingMetrics))]
[JsonSerializable(typeof(ToolExecutionEvent))]
[JsonSerializable(typeof(ToolUsageData))]
internal partial class GatewayInternalJsonContext : JsonSerializerContext;

internal sealed record FunctionCostMetadata(
    [property: JsonPropertyName("functionConfigurationId")] int FunctionConfigurationId,
    [property: JsonPropertyName("executionId")] Guid ExecutionId,
    [property: JsonPropertyName("state")] string State);

internal sealed record MediaTaskAccountingMetadata(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("imageCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ImageCount = null,
    [property: JsonPropertyName("quality"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Quality = null,
    [property: JsonPropertyName("size"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Size = null,
    [property: JsonPropertyName("durationSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DurationSeconds = null,
    [property: JsonPropertyName("resolution"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Resolution = null,
    [property: JsonPropertyName("fps"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Fps = null,
    [property: JsonPropertyName("style"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Style = null);

internal sealed record RedisCircuitBreakerMetadata(
    [property: JsonPropertyName("circuit_state")] string CircuitState,
    [property: JsonPropertyName("total_failures")] long TotalFailures,
    [property: JsonPropertyName("rejected_requests")] long RejectedRequests,
    [property: JsonPropertyName("last_failure_at")] string? LastFailureAt,
    [property: JsonPropertyName("circuit_opened_at")] string? CircuitOpenedAt,
    [property: JsonPropertyName("retry_after_seconds")] double? RetryAfterSeconds);

internal sealed record ChatToolCallsMetadata(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("toolCallCount")] int ToolCallCount,
    [property: JsonPropertyName("toolCalls")] List<ChatToolCallMetadata> ToolCalls);

internal sealed record ChatToolCallMetadata(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("functionName")] string? FunctionName,
    [property: JsonPropertyName("hasArguments")] bool HasArguments);

internal sealed record MediaUsageMetadata(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("imageCount"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ImageCount = null,
    [property: JsonPropertyName("quality"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Quality = null,
    [property: JsonPropertyName("size"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Size = null,
    [property: JsonPropertyName("style"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Style = null,
    [property: JsonPropertyName("durationSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? DurationSeconds = null,
    [property: JsonPropertyName("resolution"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Resolution = null,
    [property: JsonPropertyName("fps"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Fps = null,
    [property: JsonPropertyName("pricingParametersUsed"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? PricingParametersUsed = null,
    [property: JsonPropertyName("audioDurationSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? AudioDurationSeconds = null,
    [property: JsonPropertyName("ttsCharacters"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TtsCharacters = null);

internal sealed record BillingReconciliationMetadata(
    [property: JsonPropertyName("windowStartUtc")] DateTime WindowStartUtc,
    [property: JsonPropertyName("windowEndUtc")] DateTime WindowEndUtc,
    [property: JsonPropertyName("requestLogCost")] decimal RequestLogCost,
    [property: JsonPropertyName("ledgerDebitCost")] decimal LedgerDebitCost,
    [property: JsonPropertyName("ledgerDifference")] decimal LedgerDifference,
    [property: JsonPropertyName("ledgerRelativeDifference")] decimal LedgerRelativeDifference,
    [property: JsonPropertyName("providerActualCost")] decimal ProviderActualCost,
    [property: JsonPropertyName("providerExpectedBilledCost")] decimal ProviderExpectedBilledCost,
    [property: JsonPropertyName("providerBilledCost")] decimal ProviderBilledCost,
    [property: JsonPropertyName("providerDifference")] decimal ProviderDifference,
    [property: JsonPropertyName("providerRelativeDifference")] decimal ProviderRelativeDifference,
    [property: JsonPropertyName("requestCount")] int RequestCount,
    [property: JsonPropertyName("ledgerTransactionCount")] int LedgerTransactionCount,
    [property: JsonPropertyName("incompleteProviderEvidenceCount")] int IncompleteProviderEvidenceCount,
    [property: JsonPropertyName("comparisonTypes")] string[] ComparisonTypes);

public sealed record SpendNotificationInstanceData(
    string InstanceId,
    string MachineName,
    int ProcessId,
    DateTime StartedAt,
    DateTime LastHeartbeat);

internal sealed record FunctionExecutionResultsMetadata(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("functionCallCount")] int FunctionCallCount,
    [property: JsonPropertyName("totalCost")] decimal TotalCost,
    [property: JsonPropertyName("successCount")] int SuccessCount,
    [property: JsonPropertyName("failedCount")] int FailedCount,
    [property: JsonPropertyName("functionCalls")] List<ToolExecutionEvent> FunctionCalls);
