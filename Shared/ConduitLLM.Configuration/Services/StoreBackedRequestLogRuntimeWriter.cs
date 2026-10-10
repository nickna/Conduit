using System.Text.Json;

using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Serialization;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

namespace ConduitLLM.Configuration.Services;

/// <summary>
/// Gateway request-log writer over the fixed-shape runtime persistence contract.
/// </summary>
public sealed class StoreBackedRequestLogRuntimeWriter : IRequestLogRuntimeWriter
{
    private readonly IRequestLogRuntimeStore _store;

    public StoreBackedRequestLogRuntimeWriter(IRequestLogRuntimeStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public async Task LogRequestAsync(LogRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _store.WriteAsync(new RequestLogRuntimeRecord
        {
            VirtualKeyId = request.VirtualKeyId,
            ModelName = request.ModelName,
            ProviderId = request.ProviderId,
            ProviderType = request.ProviderType,
            ModelProviderMappingId = request.ModelProviderMappingId,
            PromptCachingEligible = request.PromptCachingEligible,
            PromptCachingPolicyApplied = request.PromptCachingPolicyApplied,
            CachedReadSavings = request.CachedReadSavings,
            CacheWritePremium = request.CacheWritePremium,
            RoutingAffinityUsed = request.RoutingAffinityUsed,
            RoutingDecisionReason = request.RoutingDecisionReason,
            RoutingFailoverCount = request.RoutingFailoverCount,
            RequestType = request.RequestType,
            InputTokens = request.InputTokens,
            OutputTokens = request.OutputTokens,
            CachedInputTokens = request.CachedInputTokens,
            CachedWriteTokens = request.CachedWriteTokens,
            Cost = request.Cost,
            BillingMethod = request.BillingMethod is null ? null : (int)request.BillingMethod.Value,
            ProviderReportedCostUsd = request.ProviderReportedCostUsd,
            ProviderCostMarkupMultiplier = request.ProviderCostMarkupMultiplier,
            BilledAtUtc = request.Cost > 0 ? request.BilledAtUtc ?? request.Timestamp : null,
            ResponseTimeMs = request.ResponseTimeMs,
            Timestamp = request.Timestamp,
            UserId = request.UserId,
            ClientIp = request.ClientIp,
            RequestPath = request.RequestPath,
            StatusCode = request.StatusCode,
            MetadataJson = request.Metadata is null
                ? null
                : JsonSerializer.Serialize(
                    request.Metadata,
                    ConfigurationJsonContext.Default.DictionaryStringJsonElement)
        });
    }
}
