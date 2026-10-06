using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Utilities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using ConduitLLM.Configuration.Interfaces;

namespace ConduitLLM.Configuration.Repositories
{
    /// <summary>
    /// Repository implementation for request logs using Entity Framework Core.
    /// Extends RepositoryBase for standard CRUD operations.
    /// </summary>
    public class RequestLogRepository : RepositoryBase<RequestLog, int>, IRequestLogRepository
    {
        /// <summary>
        /// Maximum page size for request log queries
        /// </summary>
        protected override int MaxPageSize => 1000;

        /// <summary>
        /// Creates a new instance of the repository
        /// </summary>
        /// <param name="dbContextFactory">The database context factory</param>
        /// <param name="logger">The logger</param>
        public RequestLogRepository(
            IDbContextFactory<ConduitDbContext> dbContextFactory,
            ILogger<RequestLogRepository> logger)
            : base(dbContextFactory, logger)
        {
        }

        /// <inheritdoc/>
        protected override DbSet<RequestLog> GetDbSet(ConduitDbContext context)
        {
            return context.RequestLogs;
        }

        /// <inheritdoc/>
        protected override IQueryable<RequestLog> ApplyDefaultOrdering(IQueryable<RequestLog> query)
        {
            return query.OrderByDescending(r => r.Timestamp);
        }

        /// <inheritdoc/>
        protected override void OnBeforeCreate(RequestLog entity)
        {
            base.OnBeforeCreate(entity);

            // Ensure timestamp is set
            if (entity.Timestamp == default)
            {
                entity.Timestamp = DateTime.UtcNow;
            }
        }

        /// <inheritdoc/>
        [Obsolete("Use GetByVirtualKeyIdPaginatedAsync instead. This method loads all records into memory and will be removed in a future version.")]
        public async Task<List<RequestLog>> GetByVirtualKeyIdAsync(int virtualKeyId, CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async context =>
            {
                return await context.RequestLogs
                    .AsNoTracking()
                    .Where(r => r.VirtualKeyId == virtualKeyId)
                    .OrderByDescending(r => r.Timestamp)
                    .ToListAsync(cancellationToken);
            }, cancellationToken, $"getting by virtual key ID {virtualKeyId}");
        }

        /// <inheritdoc/>
        public async Task<(List<RequestLog> Logs, int TotalCount)> GetByVirtualKeyIdPaginatedAsync(
            int virtualKeyId,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            return await GetFilteredPaginatedAsync(
                r => r.VirtualKeyId == virtualKeyId,
                pageNumber,
                pageSize,
                q => q.OrderByDescending(r => r.Timestamp),
                cancellationToken,
                $"getting paginated by virtual key ID {virtualKeyId}");
        }

        /// <inheritdoc/>
        public async Task<List<RequestLog>> GetByDateRangeAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
        {
            // Ensure dates are UTC for PostgreSQL timestamp with time zone
            var utcStartDate = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
            var utcEndDate = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

            return await ExecuteAsync(async context =>
            {
                return await context.RequestLogs
                    .AsNoTracking()
                    .Where(r => r.Timestamp >= utcStartDate && r.Timestamp <= utcEndDate)
                    .OrderByDescending(r => r.Timestamp)
                    .ToListAsync(cancellationToken);
            }, cancellationToken, $"getting by date range {startDate:d} to {endDate:d}");
        }

        /// <inheritdoc/>
        public async Task<List<RequestLog>> GetByDateRangeFilteredAsync(
            DateTime startDate,
            DateTime endDate,
            string? modelFilter = null,
            int? virtualKeyId = null,
            CancellationToken cancellationToken = default)
        {
            var utcStartDate = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
            var utcEndDate = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

            // Pre-compute ILIKE pattern outside the EF expression so it's a parameterized literal.
            // Escape the LIKE wildcards so a user-supplied "_" or "%" matches itself.
            string? likePattern = null;
            if (!string.IsNullOrEmpty(modelFilter))
            {
                var escaped = modelFilter
                    .Replace("\\", "\\\\")
                    .Replace("%", "\\%")
                    .Replace("_", "\\_");
                likePattern = $"%{escaped}%";
            }

            return await ExecuteAsync(async context =>
            {
                var query = context.RequestLogs
                    .AsNoTracking()
                    .Where(r => r.Timestamp >= utcStartDate && r.Timestamp <= utcEndDate);

                if (likePattern != null)
                {
                    query = query.Where(r => EF.Functions.ILike(r.ModelName, likePattern));
                }

                if (virtualKeyId.HasValue)
                {
                    var vkId = virtualKeyId.Value;
                    query = query.Where(r => r.VirtualKeyId == vkId);
                }

                return await query
                    .OrderByDescending(r => r.Timestamp)
                    .ToListAsync(cancellationToken);
            }, cancellationToken, $"getting filtered by date range {startDate:d} to {endDate:d}");
        }

        /// <inheritdoc/>
        public async Task<(List<RequestLog> Logs, int TotalCount)> GetByModelPaginatedAsync(
            string modelName,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(modelName))
            {
                throw new ArgumentException("Model name cannot be null or empty", nameof(modelName));
            }

            return await GetFilteredPaginatedAsync(
                r => r.ModelName == modelName,
                pageNumber,
                pageSize,
                q => q.OrderByDescending(r => r.Timestamp),
                cancellationToken,
                $"getting paginated by model {LoggingSanitizer.S(modelName)}");
        }

        /// <inheritdoc/>
        public async Task<List<string>> GetDistinctModelsAsync(CancellationToken cancellationToken = default)
        {
            return await ExecuteAsync(async context =>
            {
                return await context.RequestLogs
                    .AsNoTracking()
                    .Where(r => r.ModelName != null && r.ModelName != "")
                    .Select(r => r.ModelName!)
                    .Distinct()
                    .OrderBy(m => m)
                    .ToListAsync(cancellationToken);
            }, cancellationToken, "getting distinct models");
        }

        /// <inheritdoc/>
        public async Task<(List<RequestLog> Logs, int TotalCount)> GetByDateRangePaginatedAsync(
            DateTime startDate,
            DateTime endDate,
            int pageNumber,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            // Ensure dates are UTC for PostgreSQL timestamp with time zone
            var utcStartDate = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
            var utcEndDate = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

            return await GetFilteredPaginatedAsync(
                r => r.Timestamp >= utcStartDate && r.Timestamp <= utcEndDate,
                pageNumber,
                pageSize,
                q => q.OrderByDescending(r => r.Timestamp),
                cancellationToken,
                $"getting paginated by date range {startDate:d} to {endDate:d}");
        }

        /// <inheritdoc/>
        public async Task<UsageStatisticsDto> GetUsageStatisticsAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
        {
            var utcStartDate = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
            var utcEndDate = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

            // Get summary and model breakdown via database-level aggregation
            var summaryTask = GetSummaryAsync(utcStartDate, utcEndDate, cancellationToken);
            var modelTask = GetAggregatedByModelAsync(utcStartDate, utcEndDate, cancellationToken);
            await Task.WhenAll(summaryTask, modelTask);

            var summary = await summaryTask;
            var modelAggregations = await modelTask;

            var modelUsageDict = modelAggregations.ToDictionary(
                m => m.ModelName,
                m => new ModelUsage
                {
                    RequestCount = m.RequestCount,
                    Cost = m.TotalCost,
                    InputTokens = (int)Math.Min(m.InputTokens, int.MaxValue),
                    OutputTokens = (int)Math.Min(m.OutputTokens, int.MaxValue)
                }
            );

            return new UsageStatisticsDto
            {
                TotalRequests = summary.TotalRequests,
                TotalCost = summary.TotalCost,
                AverageResponseTimeMs = summary.AverageResponseTimeMs,
                TotalInputTokens = (int)Math.Min(summary.TotalInputTokens, int.MaxValue),
                TotalOutputTokens = (int)Math.Min(summary.TotalOutputTokens, int.MaxValue),
                ModelUsage = modelUsageDict
            };
        }

        #region Database-Level Aggregation Methods

        /// <inheritdoc/>
        public async Task<List<DateCostAggregation>> GetCostsByDateAsync(
            DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
        {
            var utcStartDate = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
            var utcEndDate = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

            return await ExecuteAsync(async context =>
            {
                return await context.RequestLogs
                    .AsNoTracking()
                    .Where(r => r.Timestamp >= utcStartDate && r.Timestamp <= utcEndDate)
                    .GroupBy(r => r.Timestamp.Date)
                    .Select(g => new DateCostAggregation
                    {
                        Date = g.Key,
                        TotalCost = g.Sum(r => r.Cost),
                        RequestCount = g.Count()
                    })
                    .OrderBy(d => d.Date)
                    .ToListAsync(cancellationToken);
            }, cancellationToken, $"getting daily costs for {startDate:d} to {endDate:d}");
        }

        /// <inheritdoc/>
        public async Task<List<ModelAggregation>> GetAggregatedByModelAsync(
            DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
        {
            var utcStartDate = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
            var utcEndDate = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

            return await ExecuteAsync(async context =>
            {
                return await context.RequestLogs
                    .AsNoTracking()
                    .Where(r => r.Timestamp >= utcStartDate && r.Timestamp <= utcEndDate)
                    .GroupBy(r => r.ModelName)
                    .Select(g => new ModelAggregation
                    {
                        ModelName = g.Key ?? "Unknown",
                        TotalCost = g.Sum(r => r.Cost),
                        RequestCount = g.Count(),
                        InputTokens = g.Sum(r => (long)r.InputTokens),
                        OutputTokens = g.Sum(r => (long)r.OutputTokens),
                        CachedInputTokens = g.Sum(r => (long)(r.CachedInputTokens ?? 0)),
                        CachedWriteTokens = g.Sum(r => (long)(r.CachedWriteTokens ?? 0))
                    })
                    .OrderByDescending(m => m.TotalCost)
                    .ToListAsync(cancellationToken);
            }, cancellationToken, $"getting model aggregations for {startDate:d} to {endDate:d}");
        }

        /// <inheritdoc/>
        public async Task<List<ModelAggregation>> GetAggregatedByModelForVirtualKeyAsync(
            int virtualKeyId, DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
        {
            var utcStartDate = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
            var utcEndDate = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

            return await ExecuteAsync(async context =>
            {
                return await context.RequestLogs
                    .AsNoTracking()
                    .Where(r => r.Timestamp >= utcStartDate && r.Timestamp <= utcEndDate && r.VirtualKeyId == virtualKeyId)
                    .GroupBy(r => r.ModelName)
                    .Select(g => new ModelAggregation
                    {
                        ModelName = g.Key ?? "Unknown",
                        TotalCost = g.Sum(r => r.Cost),
                        RequestCount = g.Count(),
                        InputTokens = g.Sum(r => (long)r.InputTokens),
                        OutputTokens = g.Sum(r => (long)r.OutputTokens),
                        CachedInputTokens = g.Sum(r => (long)(r.CachedInputTokens ?? 0)),
                        CachedWriteTokens = g.Sum(r => (long)(r.CachedWriteTokens ?? 0))
                    })
                    .OrderByDescending(m => m.TotalCost)
                    .ToListAsync(cancellationToken);
            }, cancellationToken, $"getting model aggregations for virtual key {virtualKeyId}");
        }

        /// <inheritdoc/>
        public async Task<List<VirtualKeyAggregation>> GetAggregatedByVirtualKeyAsync(
            DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
        {
            var utcStartDate = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
            var utcEndDate = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

            return await ExecuteAsync(async context =>
            {
                return await context.RequestLogs
                    .AsNoTracking()
                    .Where(r => r.Timestamp >= utcStartDate && r.Timestamp <= utcEndDate)
                    .GroupBy(r => r.VirtualKeyId)
                    .Select(g => new VirtualKeyAggregation
                    {
                        VirtualKeyId = g.Key,
                        TotalCost = g.Sum(r => r.Cost),
                        RequestCount = g.Count(),
                        LastUsed = g.Max(r => r.Timestamp),
                        UniqueModels = g.Select(r => r.ModelName).Distinct().Count()
                    })
                    .OrderByDescending(v => v.TotalCost)
                    .ToListAsync(cancellationToken);
            }, cancellationToken, $"getting virtual key aggregations for {startDate:d} to {endDate:d}");
        }

        /// <inheritdoc/>
        public async Task<RequestLogSummary> GetSummaryAsync(
            DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
        {
            var utcStartDate = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
            var utcEndDate = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

            return await ExecuteAsync(async context =>
            {
                var summary = await context.RequestLogs
                    .AsNoTracking()
                    .Where(r => r.Timestamp >= utcStartDate && r.Timestamp <= utcEndDate)
                    .GroupBy(r => 1) // Single group for whole-set aggregation
                    .Select(g => new RequestLogSummary
                    {
                        TotalRequests = g.Count(),
                        TotalCost = g.Sum(r => r.Cost),
                        TotalInputTokens = g.Sum(r => (long)r.InputTokens),
                        TotalOutputTokens = g.Sum(r => (long)r.OutputTokens),
                        TotalCachedInputTokens = g.Sum(r => (long)(r.CachedInputTokens ?? 0)),
                        TotalCachedWriteTokens = g.Sum(r => (long)(r.CachedWriteTokens ?? 0)),
                        AverageResponseTimeMs = g.Average(r => r.ResponseTimeMs),
                        SuccessCount = g.Sum(r => (r.StatusCode ?? 0) >= 200 && (r.StatusCode ?? 0) < 300 ? 1 : 0),
                        ErrorCount = g.Sum(r => (r.StatusCode ?? 0) >= 400 ? 1 : 0)
                    })
                    .FirstOrDefaultAsync(cancellationToken);

                return summary ?? new RequestLogSummary();
            }, cancellationToken, $"getting summary for {startDate:d} to {endDate:d}");
        }

        /// <inheritdoc/>
        public async Task<RequestLogSummary> GetSummaryForVirtualKeyAsync(
            int virtualKeyId, DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
        {
            var utcStartDate = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
            var utcEndDate = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

            return await ExecuteAsync(async context =>
            {
                var summary = await context.RequestLogs
                    .AsNoTracking()
                    .Where(r => r.Timestamp >= utcStartDate && r.Timestamp <= utcEndDate && r.VirtualKeyId == virtualKeyId)
                    .GroupBy(r => 1)
                    .Select(g => new RequestLogSummary
                    {
                        TotalRequests = g.Count(),
                        TotalCost = g.Sum(r => r.Cost),
                        TotalInputTokens = g.Sum(r => (long)r.InputTokens),
                        TotalOutputTokens = g.Sum(r => (long)r.OutputTokens),
                        TotalCachedInputTokens = g.Sum(r => (long)(r.CachedInputTokens ?? 0)),
                        TotalCachedWriteTokens = g.Sum(r => (long)(r.CachedWriteTokens ?? 0)),
                        AverageResponseTimeMs = g.Average(r => r.ResponseTimeMs),
                        SuccessCount = g.Sum(r => (r.StatusCode ?? 0) >= 200 && (r.StatusCode ?? 0) < 300 ? 1 : 0),
                        ErrorCount = g.Sum(r => (r.StatusCode ?? 0) >= 400 ? 1 : 0)
                    })
                    .FirstOrDefaultAsync(cancellationToken);

                return summary ?? new RequestLogSummary();
            }, cancellationToken, $"getting summary for virtual key {virtualKeyId}");
        }

        /// <inheritdoc/>
        public async Task<List<DailyStatisticsAggregation>> GetDailyStatisticsAsync(
            DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default)
        {
            var utcStartDate = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
            var utcEndDate = DateTime.SpecifyKind(endDate, DateTimeKind.Utc);

            return await ExecuteAsync(async context =>
            {
                return await context.RequestLogs
                    .AsNoTracking()
                    .Where(r => r.Timestamp >= utcStartDate && r.Timestamp <= utcEndDate)
                    .GroupBy(r => r.Timestamp.Date)
                    .Select(g => new DailyStatisticsAggregation
                    {
                        Date = g.Key,
                        RequestCount = g.Count(),
                        Cost = g.Sum(r => r.Cost),
                        InputTokens = g.Sum(r => (long)r.InputTokens),
                        OutputTokens = g.Sum(r => (long)r.OutputTokens),
                        CachedInputTokens = g.Sum(r => (long)(r.CachedInputTokens ?? 0)),
                        CachedWriteTokens = g.Sum(r => (long)(r.CachedWriteTokens ?? 0)),
                        AverageResponseTime = g.Average(r => r.ResponseTimeMs),
                        ErrorCount = g.Sum(r => (r.StatusCode ?? 0) >= 400 ? 1 : 0)
                    })
                    .OrderBy(s => s.Date)
                    .ToListAsync(cancellationToken);
            }, cancellationToken, $"getting daily statistics for {startDate:d} to {endDate:d}");
        }

        #endregion

        /// <inheritdoc/>
        public async Task<bool> UpdateCostByTaskIdAsync(
            string taskId,
            decimal cost,
            string? modelName = null,
            double? durationSeconds = null,
            string? resolution = null,
            DateTime? billedAtUtc = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(taskId))
            {
                throw new ArgumentException("Task ID cannot be null or empty", nameof(taskId));
            }

            return await ExecuteAsync(async context =>
            {
                // Find the request log by task ID in the metadata JSONB column
                // Using PostgreSQL JSONB ->> operator to extract text value
                var requestLog = await context.RequestLogs
                    .FromSqlRaw(
                        @"SELECT * FROM ""RequestLogs"" WHERE ""Metadata"" ->> 'taskId' = {0} LIMIT 1",
                        taskId)
                    .FirstOrDefaultAsync(cancellationToken);

                if (requestLog == null)
                {
                    Logger.LogWarning("Request log not found for task ID {TaskId}", LoggingSanitizer.S(taskId));
                    return false;
                }

                // Update the cost
                requestLog.Cost = cost;
                requestLog.BilledAtUtc = cost > 0 ? billedAtUtc ?? DateTime.UtcNow : null;

                // Update model name if provided and different
                if (!string.IsNullOrEmpty(modelName) && modelName != "unknown")
                {
                    requestLog.ModelName = modelName;
                }

                // Update metadata with actual values
                if (!string.IsNullOrEmpty(requestLog.Metadata))
                {
                    try
                    {
                        using var jsonDoc = System.Text.Json.JsonDocument.Parse(requestLog.Metadata);
                        var root = jsonDoc.RootElement;

                        // Build updated metadata
                        var updatedMetadata = new Dictionary<string, object?>();

                        // Copy existing properties
                        foreach (var prop in root.EnumerateObject())
                        {
                            updatedMetadata[prop.Name] = GetJsonElementValue(prop.Value);
                        }

                        // Update with actual values
                        if (durationSeconds.HasValue)
                        {
                            updatedMetadata["durationSeconds"] = durationSeconds.Value;
                        }
                        if (!string.IsNullOrEmpty(resolution))
                        {
                            updatedMetadata["resolution"] = resolution;
                        }
                        updatedMetadata["costCorrected"] = true;
                        updatedMetadata["costCorrectedAt"] = DateTime.UtcNow.ToString("O");

                        requestLog.Metadata = System.Text.Json.JsonSerializer.Serialize(
                            updatedMetadata,
                            Serialization.ConfigurationJsonContext.Default.DictionaryStringObject);
                    }
                    catch (System.Text.Json.JsonException ex)
                    {
                        Logger.LogWarning(ex, "Failed to parse metadata for task ID {TaskId}, skipping metadata update",
                            LoggingSanitizer.S(taskId));
                    }
                }

                // Save changes
                context.RequestLogs.Update(requestLog);
                var rowsAffected = await context.SaveChangesAsync(cancellationToken);

                Logger.LogInformation(
                    "Updated request log for task {TaskId}: Cost=${Cost}, Model={Model}, Duration={Duration}s",
                    LoggingSanitizer.S(taskId), cost, modelName ?? requestLog.ModelName, durationSeconds);

                return rowsAffected > 0;
            }, cancellationToken, $"updating cost for task {LoggingSanitizer.S(taskId)}");
        }

        /// <summary>
        /// Helper method to extract value from JsonElement for metadata reconstruction
        /// </summary>
        private static object? GetJsonElementValue(System.Text.Json.JsonElement element)
        {
            return element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => element.GetString(),
                System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out var longVal) ? longVal : element.GetDouble(),
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Null => null,
                System.Text.Json.JsonValueKind.Array => element.EnumerateArray().Select(GetJsonElementValue).ToArray(),
                _ => element.GetRawText()
            };
        }
    }
}
