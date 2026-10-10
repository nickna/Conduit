using ConduitLLM.Configuration.Entities;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Configuration.Repositories;

/// <summary>
/// Fixed-shape EF reference implementation for Gateway request-log writes.
/// </summary>
public sealed class EfRequestLogRuntimeStore : IRequestLogRuntimeStore
{
    private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;

    public EfRequestLogRuntimeStore(IDbContextFactory<ConduitDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
    }

    /// <inheritdoc />
    public async Task<int> WriteAsync(
        RequestLogRuntimeRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = new RequestLog
        {
            VirtualKeyId = record.VirtualKeyId,
            ModelName = record.ModelName,
            ProviderId = record.ProviderId,
            ProviderType = record.ProviderType,
            ModelProviderMappingId = record.ModelProviderMappingId,
            PromptCachingEligible = record.PromptCachingEligible,
            PromptCachingPolicyApplied = record.PromptCachingPolicyApplied,
            CachedReadSavings = record.CachedReadSavings,
            CacheWritePremium = record.CacheWritePremium,
            RoutingAffinityUsed = record.RoutingAffinityUsed,
            RoutingDecisionReason = record.RoutingDecisionReason,
            RoutingFailoverCount = record.RoutingFailoverCount,
            RequestType = record.RequestType,
            InputTokens = record.InputTokens,
            OutputTokens = record.OutputTokens,
            CachedInputTokens = record.CachedInputTokens,
            CachedWriteTokens = record.CachedWriteTokens,
            Cost = record.Cost,
            BillingMethod = record.BillingMethod is null
                ? null
                : (Enums.RequestBillingMethod)record.BillingMethod.Value,
            ProviderReportedCostUsd = record.ProviderReportedCostUsd,
            ProviderCostMarkupMultiplier = record.ProviderCostMarkupMultiplier,
            BilledAtUtc = record.BilledAtUtc,
            ResponseTimeMs = record.ResponseTimeMs,
            Timestamp = record.Timestamp,
            UserId = record.UserId,
            ClientIp = record.ClientIp,
            RequestPath = record.RequestPath,
            StatusCode = record.StatusCode,
            Metadata = record.MetadataJson
        };
        context.RequestLogs.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }
}
