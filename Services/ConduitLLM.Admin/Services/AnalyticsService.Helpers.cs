using System.Text;
using System.Text.Json;

using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.DTOs.Costs;
using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Admin.Services
{
    /// <summary>
    /// Unified analytics service - Helper Methods
    /// </summary>
    public partial class AnalyticsService
    {
        #region Private Helper Methods

        private static LogRequestDto MapToLogRequestDto(RequestLog log)
        {
            return new LogRequestDto
            {
                Id = log.Id,
                VirtualKeyId = log.VirtualKeyId,
                ModelName = log.ModelName,
                ProviderId = log.ProviderId,
                ProviderType = log.ProviderType,
                ModelProviderMappingId = log.ModelProviderMappingId,
                PromptCachingEligible = log.PromptCachingEligible,
                PromptCachingPolicyApplied = log.PromptCachingPolicyApplied,
                CachedReadSavings = log.CachedReadSavings,
                CacheWritePremium = log.CacheWritePremium,
                RoutingAffinityUsed = log.RoutingAffinityUsed,
                RoutingDecisionReason = log.RoutingDecisionReason,
                RoutingFailoverCount = log.RoutingFailoverCount,
                RequestType = log.RequestType,
                InputTokens = log.InputTokens,
                OutputTokens = log.OutputTokens,
                CachedInputTokens = log.CachedInputTokens,
                CachedWriteTokens = log.CachedWriteTokens,
                Cost = log.Cost,
                BillingMethod = log.BillingMethod,
                ProviderReportedCostUsd = log.ProviderReportedCostUsd,
                ProviderCostMarkupMultiplier = log.ProviderCostMarkupMultiplier,
                BilledAtUtc = log.BilledAtUtc,
                ResponseTimeMs = log.ResponseTimeMs,
                UserId = log.UserId,
                ClientIp = log.ClientIp,
                RequestPath = log.RequestPath,
                StatusCode = log.StatusCode,
                Timestamp = log.Timestamp,
                Metadata = DeserializeMetadata(log.Metadata)
            };
        }

        private static Dictionary<string, JsonElement>? DeserializeMetadata(string? metadata) =>
            ConduitLLM.Configuration.Utilities.JsonMetadataParser.Parse(metadata);

        private static string NormalizeTimeframe(string timeframe)
        {
            return timeframe.ToLower() switch
            {
                "daily" => "daily",
                "weekly" => "weekly",
                "monthly" => "monthly",
                _ => "daily"
            };
        }

        /// <summary>
        /// Converts model aggregations from DB to ModelCostDetail DTOs.
        /// Provider breakdown is derived from model names (e.g., "openai/gpt-4" → "openai")
        /// since it requires string parsing that can't be done at the database level.
        /// </summary>
        private static List<ModelCostDetail> ToModelCostDetails(List<ModelAggregation> models)
        {
            return models
                .Select(m => new ModelCostDetail
                {
                    ModelName = m.ModelName,
                    TotalCost = m.TotalCost,
                    RequestCount = m.RequestCount,
                    InputTokens = m.InputTokens,
                    OutputTokens = m.OutputTokens,
                    AverageCostPerRequest = m.RequestCount > 0 ? m.TotalCost / m.RequestCount : 0,
                    CostPercentage = 0 // Calculated by caller if needed
                })
                .ToList();
        }

        /// <summary>
        /// Derives provider breakdown from model aggregations by extracting the provider
        /// prefix from model names (e.g., "openai/gpt-4" → "openai").
        /// This is an in-memory operation on the small model aggregation set (~10-100 rows),
        /// not on individual request log rows.
        /// </summary>
        private static List<ProviderCostDetail> CalculateProviderBreakdownFromModels(List<ModelAggregation> models)
        {
            return models
                .GroupBy(m => ExtractProviderFromModel(m.ModelName))
                .Select(g => new ProviderCostDetail
                {
                    ProviderName = g.Key,
                    TotalCost = g.Sum(m => m.TotalCost),
                    RequestCount = g.Sum(m => m.RequestCount),
                    AverageCostPerRequest = g.Sum(m => m.RequestCount) > 0
                        ? g.Sum(m => m.TotalCost) / g.Sum(m => m.RequestCount)
                        : 0,
                    CostPercentage = 0 // Calculated by caller if needed
                })
                .OrderByDescending(p => p.TotalCost)
                .ToList();
        }

        /// <summary>
        /// Converts virtual key aggregations from DB to VirtualKeyCostDetail DTOs.
        /// </summary>
        private static List<VirtualKeyCostDetail> ToVirtualKeyCostDetails(List<VirtualKeyAggregation> keys)
        {
            return keys
                .Select(v => new VirtualKeyCostDetail
                {
                    VirtualKeyId = v.VirtualKeyId,
                    KeyName = $"Key #{v.VirtualKeyId}", // Enriched by caller with actual name
                    TotalCost = v.TotalCost,
                    RequestCount = v.RequestCount,
                    AverageCostPerRequest = v.RequestCount > 0 ? v.TotalCost / v.RequestCount : 0,
                    LastUsed = v.LastUsed,
                    UniqueModels = v.UniqueModels
                })
                .ToList();
        }

        private static string ExtractProviderFromModel(string modelName)
        {
            // Extract provider from model name (e.g., "openai/gpt-4" -> "openai")
            var parts = modelName.Split('/');
            return parts.Length > 1 ? parts[0] : "unknown";
        }

        /// <summary>
        /// Aggregates daily statistics to weekly or monthly granularity.
        /// Operates on the small daily aggregation set (~365 rows/year) from the database,
        /// not on individual request log rows.
        /// </summary>
        private static List<DailyStatistics> AggregateStatisticsByTimeframe(
            List<DailyStatisticsAggregation> dailyStats,
            string timeframe)
        {
            if (timeframe == "daily")
            {
                return dailyStats
                    .Select(d => new DailyStatistics
                    {
                        Date = d.Date,
                        RequestCount = d.RequestCount,
                        Cost = d.Cost,
                        InputTokens = d.InputTokens,
                        OutputTokens = d.OutputTokens,
                        CachedInputTokens = d.CachedInputTokens,
                        CachedWriteTokens = d.CachedWriteTokens,
                        AverageResponseTime = d.AverageResponseTime,
                        ErrorCount = d.ErrorCount
                    })
                    .ToList();
            }

            var grouped = timeframe switch
            {
                "weekly" => dailyStats.GroupBy(d => GetStartOfWeek(d.Date)),
                "monthly" => dailyStats.GroupBy(d => new DateTime(d.Date.Year, d.Date.Month, 1)),
                _ => dailyStats.GroupBy(d => d.Date)
            };

            return grouped
                .Select(g =>
                {
                    var totalRequests = g.Sum(d => d.RequestCount);
                    return new DailyStatistics
                    {
                        Date = g.Key,
                        RequestCount = totalRequests,
                        Cost = g.Sum(d => d.Cost),
                        InputTokens = g.Sum(d => d.InputTokens),
                        OutputTokens = g.Sum(d => d.OutputTokens),
                        CachedInputTokens = g.Sum(d => d.CachedInputTokens),
                        CachedWriteTokens = g.Sum(d => d.CachedWriteTokens),
                        AverageResponseTime = totalRequests > 0
                            ? g.Sum(d => d.AverageResponseTime * d.RequestCount) / totalRequests
                            : 0,
                        ErrorCount = g.Sum(d => d.ErrorCount)
                    };
                })
                .OrderBy(s => s.Date)
                .ToList();
        }

        /// <summary>
        /// Aggregates daily cost data to weekly or monthly granularity.
        /// </summary>
        private static List<(DateTime Date, decimal Cost)> AggregateByTimeframe(
            List<DateCostAggregation> dailyCosts,
            string timeframe)
        {
            if (timeframe == "daily")
            {
                return dailyCosts.Select(d => (d.Date, d.TotalCost)).ToList();
            }

            var tuples = dailyCosts.Select(d => (d.Date, d.TotalCost)).ToList();
            return timeframe switch
            {
                "weekly" => AggregateByWeek(tuples),
                "monthly" => AggregateByMonth(tuples),
                _ => tuples
            };
        }

        /// <summary>
        /// Calculates cost trend points from daily cost aggregations.
        /// </summary>
        private static List<CostTrendPoint> CalculateCostTrendsFromDaily(
            List<DateCostAggregation> dailyCosts,
            string period)
        {
            if (period == "daily")
            {
                return dailyCosts
                    .Select(d => new CostTrendPoint
                    {
                        Date = d.Date,
                        Cost = d.TotalCost,
                        RequestCount = d.RequestCount,
                        AverageRequestCost = d.RequestCount > 0 ? d.TotalCost / d.RequestCount : 0
                    })
                    .OrderBy(t => t.Date)
                    .ToList();
            }

            var grouped = period switch
            {
                "weekly" => dailyCosts.GroupBy(d => GetStartOfWeek(d.Date)),
                "monthly" => dailyCosts.GroupBy(d => new DateTime(d.Date.Year, d.Date.Month, 1)),
                _ => dailyCosts.GroupBy(d => d.Date)
            };

            return grouped
                .Select(g =>
                {
                    var totalRequests = g.Sum(d => d.RequestCount);
                    var totalCost = g.Sum(d => d.TotalCost);
                    return new CostTrendPoint
                    {
                        Date = g.Key,
                        Cost = totalCost,
                        RequestCount = totalRequests,
                        AverageRequestCost = totalRequests > 0 ? totalCost / totalRequests : 0
                    };
                })
                .OrderBy(t => t.Date)
                .ToList();
        }

        private static List<(DateTime Date, decimal Cost)> AggregateByWeek(List<(DateTime Date, decimal Cost)> dailyCosts)
        {
            return dailyCosts
                .GroupBy(d => GetStartOfWeek(d.Date))
                .Select(g => (Date: g.Key, Cost: g.Sum(d => d.Cost)))
                .OrderBy(w => w.Date)
                .ToList();
        }

        private static List<(DateTime Date, decimal Cost)> AggregateByMonth(List<(DateTime Date, decimal Cost)> dailyCosts)
        {
            return dailyCosts
                .GroupBy(d => new DateTime(d.Date.Year, d.Date.Month, 1))
                .Select(g => (Date: g.Key, Cost: g.Sum(d => d.Cost)))
                .OrderBy(m => m.Date)
                .ToList();
        }

        private static DateTime GetStartOfWeek(DateTime date)
        {
            int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            return date.AddDays(-1 * diff).Date;
        }

        /// <summary>
        /// Compares current period with previous period using database-level summaries.
        /// Each period is a single aggregate query instead of loading all rows.
        /// </summary>
        private async Task<AnalyticsPeriodComparison> CalculatePreviousPeriodComparison(DateTime startDate, DateTime endDate)
        {
            var periodLength = endDate - startDate;
            var previousStart = startDate - periodLength;
            var previousEnd = startDate;

            // Two lightweight aggregate queries instead of loading all rows for both periods
            var currentTask = _requestLogRepository.GetSummaryAsync(startDate, endDate);
            var previousTask = _requestLogRepository.GetSummaryAsync(previousStart, previousEnd);
            await Task.WhenAll(currentTask, previousTask);

            var current = await currentTask;
            var previous = await previousTask;

            var currentErrorRate = current.TotalRequests > 0
                ? current.ErrorCount * 100.0 / current.TotalRequests
                : 0;
            var previousErrorRate = previous.TotalRequests > 0
                ? previous.ErrorCount * 100.0 / previous.TotalRequests
                : 0;

            return new AnalyticsPeriodComparison
            {
                CostChange = current.TotalCost - previous.TotalCost,
                CostChangePercentage = previous.TotalCost > 0
                    ? ((current.TotalCost - previous.TotalCost) / previous.TotalCost * 100)
                    : 0,
                RequestChange = current.TotalRequests - previous.TotalRequests,
                RequestChangePercentage = previous.TotalRequests > 0
                    ? ((decimal)(current.TotalRequests - previous.TotalRequests) / previous.TotalRequests * 100)
                    : 0,
                ResponseTimeChange = current.TotalRequests > 0 && previous.TotalRequests > 0
                    ? current.AverageResponseTimeMs - previous.AverageResponseTimeMs
                    : 0,
                ErrorRateChange = currentErrorRate - previousErrorRate
            };
        }

        private static byte[] ExportToCsv(IList<RequestLog> logs)
        {
            var csv = new StringBuilder();
            csv.AppendLine("Timestamp,VirtualKeyId,Model,RequestType,InputTokens,OutputTokens,Cost,ResponseTime,StatusCode");

            foreach (var log in logs)
            {
                csv.AppendLine($"{log.Timestamp:yyyy-MM-dd HH:mm:ss},{log.VirtualKeyId},{log.ModelName},{log.RequestType}," +
                              $"{log.InputTokens},{log.OutputTokens},{log.Cost:F6},{log.ResponseTimeMs:F2},{log.StatusCode}");
            }

            return Encoding.UTF8.GetBytes(csv.ToString());
        }

        private static byte[] ExportToJson(IList<RequestLog> logs)
        {
            var json = AdminJson.Serialize(logs.Select(MapToLogRequestDto).ToList(), new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            return Encoding.UTF8.GetBytes(json);
        }

        private class CostTrendPoint
        {
            public DateTime Date { get; set; }
            public decimal Cost { get; set; }
            public int RequestCount { get; set; }
            public decimal AverageRequestCost { get; set; }
        }

        #endregion
    }
}
