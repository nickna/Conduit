using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using ConduitLLM.Configuration.Serialization;

namespace ConduitLLM.Configuration.Services;

/// <summary>
/// Service for logging and retrieving API requests made using virtual keys.
/// Uses batch processing for efficient database writes.
/// </summary>
public class RequestLogService : BatchAuditServiceBase<RequestLog>, IRequestLogService
{
    /// <summary>
    /// Creates a new instance of the RequestLogService.
    /// </summary>
    /// <param name="serviceProvider">Service provider for creating scoped DbContexts</param>
    /// <param name="logger">Logger instance</param>
    public RequestLogService(
        IServiceProvider serviceProvider,
        ILogger<RequestLogService> logger)
        : base(serviceProvider, logger)
    {
    }

    #region Template Method Implementations

    /// <inheritdoc/>
    protected override DbSet<RequestLog> GetDbSet(ConduitDbContext context)
        => context.RequestLogs;

    /// <inheritdoc/>
    protected override string EntityName => "RequestLog";

    #endregion

    #region IRequestLogService Implementation

    /// <inheritdoc/>
    public async Task LogRequestAsync(LogRequestDto request)
    {
        var log = MapToRequestLog(request);
        await LogEventAsync(log);

        Logger.LogDebug("Request logged for VirtualKeyId={VirtualKeyId}, Cost={Cost:C}, ProviderId={ProviderId}, queued for batch write",
            request.VirtualKeyId, request.Cost, request.ProviderId);
    }

    /// <summary>
    /// Optimized method to log request with batched spend updates.
    /// </summary>
    /// <param name="request">Request log data</param>
    /// <param name="batchSpendService">Batch spend update service</param>
    public async Task LogRequestWithBatchedSpendAsync(LogRequestDto request, BatchSpendUpdateService batchSpendService)
    {
        var log = MapToRequestLog(request);
        await LogEventAsync(log);

        // Queue spend update for batching instead of immediate database write
        await batchSpendService.QueueSpendUpdateAsync(request.VirtualKeyId, request.Cost, request.BilledAtUtc ?? request.Timestamp);

        Logger.LogDebug("Request logged and spend update queued for VirtualKeyId={VirtualKeyId}, Cost={Cost:C}, ProviderId={ProviderId}",
            request.VirtualKeyId, request.Cost, request.ProviderId);
    }

    private static RequestLog MapToRequestLog(LogRequestDto request) => new()
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
        BillingMethod = request.BillingMethod,
        ProviderReportedCostUsd = request.ProviderReportedCostUsd,
        ProviderCostMarkupMultiplier = request.ProviderCostMarkupMultiplier,
        BilledAtUtc = request.Cost > 0 ? request.BilledAtUtc ?? request.Timestamp : null,
        ResponseTimeMs = request.ResponseTimeMs,
        Timestamp = request.Timestamp,
        UserId = request.UserId,
        ClientIp = request.ClientIp,
        RequestPath = request.RequestPath,
        StatusCode = request.StatusCode,
        Metadata = request.Metadata is null
            ? null
            : JsonSerializer.Serialize(
                request.Metadata,
                ConfigurationJsonContext.Default.DictionaryStringJsonElement)
    };

    /// <inheritdoc/>
    public new Task FlushEventsAsync()
        => base.FlushEventsAsync();

    /// <inheritdoc/>
    public Task CleanupOldRequestLogsAsync()
        => CleanupOldEventsAsync();

    #endregion

    #region Query Methods

    /// <inheritdoc/>
    public async Task<int?> GetVirtualKeyIdFromKeyValueAsync(string keyValue)
    {
        using var scope = ServiceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConduitDbContext>();

        return await context.VirtualKeys
            .AsNoTracking()
            .Where(k => k.KeyHash == keyValue)
            .Select(k => (int?)k.Id)
            .FirstOrDefaultAsync();
    }

    /// <inheritdoc/>
    public async Task<UsageStatisticsDto> GetUsageStatisticsAsync(int virtualKeyId, DateTime startDate, DateTime endDate)
    {
        using var scope = ServiceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConduitDbContext>();

        // Use projection to avoid loading the entire entities into memory
        var result = new UsageStatisticsDto();

        var stats = await context.RequestLogs
            .AsNoTracking()
            .Where(r => r.VirtualKeyId == virtualKeyId)
            .Where(r => r.Timestamp >= startDate && r.Timestamp <= endDate)
            .GroupBy(r => 1)
            .Select(g => new
            {
                TotalRequests = g.Count(),
                TotalCost = g.Sum(r => r.Cost),
                TotalInputTokens = g.Sum(r => r.InputTokens),
                TotalOutputTokens = g.Sum(r => r.OutputTokens),
                AverageResponseTime = g.Any() ? g.Average(r => r.ResponseTimeMs) : 0
            })
            .FirstOrDefaultAsync();

        if (stats != null)
        {
            result.TotalRequests = stats.TotalRequests;
            result.TotalCost = stats.TotalCost;
            result.TotalInputTokens = stats.TotalInputTokens;
            result.TotalOutputTokens = stats.TotalOutputTokens;
            result.AverageResponseTimeMs = stats.AverageResponseTime;

            // Get model-specific usage statistics
            var modelStats = await context.RequestLogs
                .AsNoTracking()
                .Where(r => r.VirtualKeyId == virtualKeyId)
                .Where(r => r.Timestamp >= startDate && r.Timestamp <= endDate)
                .GroupBy(r => r.ModelName)
                .Select(g => new
                {
                    ModelName = g.Key,
                    RequestCount = g.Count(),
                    Cost = g.Sum(r => r.Cost),
                    InputTokens = g.Sum(r => r.InputTokens),
                    OutputTokens = g.Sum(r => r.OutputTokens)
                })
                .ToListAsync();

            foreach (var modelStat in modelStats)
            {
                result.ModelUsage[modelStat.ModelName] = new ModelUsage
                {
                    RequestCount = modelStat.RequestCount,
                    Cost = modelStat.Cost,
                    InputTokens = modelStat.InputTokens,
                    OutputTokens = modelStat.OutputTokens
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Gets paged request logs for a virtual key
    /// </summary>
    /// <param name="virtualKeyId">The virtual key ID</param>
    /// <param name="pageNumber">Page number (1-based)</param>
    /// <param name="pageSize">Page size</param>
    /// <returns>Paged list of request logs</returns>
    public async Task<(List<RequestLog> Logs, int TotalCount)> GetPagedRequestLogsAsync(
        int virtualKeyId,
        int pageNumber = 1,
        int pageSize = 20)
    {
        using var scope = ServiceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConduitDbContext>();

        var query = context.RequestLogs
            .AsNoTracking()
            .Where(r => r.VirtualKeyId == virtualKeyId)
            .OrderByDescending(r => r.Timestamp);

        var totalCount = await query.CountAsync();

        var logs = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (logs, totalCount);
    }

    /// <inheritdoc/>
    public async Task<(List<RequestLog> Logs, int TotalCount)> SearchLogsAsync(
        int? virtualKeyId,
        string? modelFilter,
        DateTime startDate,
        DateTime endDate,
        int? statusCode,
        int pageNumber = 1,
        int pageSize = 20)
    {
        using var scope = ServiceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConduitDbContext>();

        var query = context.RequestLogs
            .AsNoTracking()
            .Include(r => r.VirtualKey)
            .Where(r => r.Timestamp >= startDate && r.Timestamp <= endDate);

        // Apply optional filters
        if (virtualKeyId.HasValue)
        {
            query = query.Where(r => r.VirtualKeyId == virtualKeyId.Value);
        }

        if (!string.IsNullOrWhiteSpace(modelFilter))
        {
            query = query.Where(r => r.ModelName.Contains(modelFilter));
        }

        if (statusCode.HasValue)
        {
            query = query.Where(r => r.StatusCode == statusCode.Value);
        }

        // Get total count before pagination
        var totalCount = await query.CountAsync();

        // Apply sorting and pagination
        var logs = await query
            .OrderByDescending(r => r.Timestamp)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (logs, totalCount);
    }

    /// <inheritdoc/>
    public async Task<LogsSummaryDto> GetLogsSummaryAsync(DateTime startDate, DateTime endDate)
    {
        using var scope = ServiceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConduitDbContext>();

        var logs = await context.RequestLogs
            .AsNoTracking()
            .Include(r => r.VirtualKey)
            .Where(r => r.Timestamp >= startDate && r.Timestamp <= endDate)
            .ToListAsync();

        var summary = new LogsSummaryDto
        {
            TotalRequests = logs.Count,
            EstimatedCost = logs.Sum(r => r.Cost),
            InputTokens = logs.Sum(r => r.InputTokens),
            OutputTokens = logs.Sum(r => r.OutputTokens),
            AverageResponseTime = logs.Count > 0 ? logs.Average(r => r.ResponseTimeMs) : 0,
            LastRequestDate = logs.Count > 0 ? logs.Max(r => r.Timestamp) : null
        };

        // Group by model
        var modelGroups = logs
            .GroupBy(r => r.ModelName)
            .Select(g => new
            {
                ModelName = g.Key,
                RequestCount = g.Count(),
                TotalCost = g.Sum(r => r.Cost),
                InputTokens = g.Sum(r => r.InputTokens),
                OutputTokens = g.Sum(r => r.OutputTokens)
            })
            .OrderByDescending(g => g.RequestCount)
            .ToList();

        foreach (var model in modelGroups)
        {
            summary.RequestsByModel[model.ModelName] = model.RequestCount;
            summary.CostByModel[model.ModelName] = model.TotalCost;
        }

        // Calculate success and failure counts
        summary.SuccessfulRequests = logs.Count(r => r.StatusCode.HasValue && r.StatusCode >= 200 && r.StatusCode < 300);
        summary.FailedRequests = logs.Count(r => r.StatusCode.HasValue && (r.StatusCode < 200 || r.StatusCode >= 300));

        // Group by status
        var statusGroups = logs
            .Where(r => r.StatusCode.HasValue)
            .GroupBy(r => r.StatusCode!.Value)
            .Select(g => new { StatusCode = g.Key, Count = g.Count() })
            .ToList();

        foreach (var status in statusGroups)
        {
            summary.RequestsByStatus[status.StatusCode] = status.Count;
        }

        // Group by day and model for daily stats
        var dailyStats = logs
            .GroupBy(r => new { Date = r.Timestamp.Date, Model = r.ModelName })
            .Select(g => new DailyUsageStatsDto
            {
                Date = g.Key.Date,
                ModelId = g.Key.Model,
                RequestCount = g.Count(),
                InputTokens = g.Sum(r => r.InputTokens),
                OutputTokens = g.Sum(r => r.OutputTokens),
                Cost = g.Sum(r => r.Cost)
            })
            .OrderBy(s => s.Date)
            .ThenBy(s => s.ModelId)
            .ToList();

        summary.DailyStats = dailyStats;

        return summary;
    }

    /// <inheritdoc/>
    public async Task<List<string>> GetDistinctModelsAsync()
    {
        using var scope = ServiceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ConduitDbContext>();

        return await context.RequestLogs
            .AsNoTracking()
            .Select(r => r.ModelName)
            .Distinct()
            .OrderBy(m => m)
            .ToListAsync();
    }

    #endregion
}
