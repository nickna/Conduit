using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Gateway.Constants;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.Utilities;
using ConduitLLM.Gateway.UsageTracking;
using System.Text.Json;

namespace ConduitLLM.Gateway.Middleware
{
    public partial class UsageTrackingMiddleware
    {
        private async Task<(decimal Cost, bool Failed)> CalculateTrackedCostAsync(
            HttpContext context,
            string model,
            Usage usage,
            ICostCalculationService costCalculationService,
            IBillingAuditService billingAuditService)
        {
            // The token window was charged an estimate before the provider was called; this is
            // the first point where the real figure is known for every response shape.
            await ReconcileTokenReservationAsync(context, usage);

            try
            {
                var providerCalls = context.GetRequestAccountingSnapshot()?.ProviderCalls.ToList();

                async Task<decimal> CalculateCallAsync(Usage callUsage)
                {
                    ApplyProviderBillingPolicy(context, callUsage);
                    return context.Items.TryGetValue(HttpContextKeys.ModelCostId, out var callCostIdObj) &&
                        callCostIdObj is int callCostId
                            ? await costCalculationService.CalculateCostByIdAsync(callCostId, callUsage)
                            : await costCalculationService.CalculateCostAsync(model, callUsage);
                }

                var cost = await BillingCostComposition.CalculateProviderCostAsync(
                    providerCalls, usage, CalculateCallAsync);

                var fallbackReason = usage.PricingFallbackReason;
                if (string.IsNullOrEmpty(fallbackReason) && providerCalls is { Count: > 0 })
                {
                    fallbackReason = string.Join("; ", providerCalls
                        .Where(call => !string.IsNullOrEmpty(call.Usage.PricingFallbackReason))
                        .Select(call => $"iteration {call.Iteration}: {call.Usage.PricingFallbackReason}"));
                }

                if (!string.IsNullOrEmpty(fallbackReason))
                {
                    LogPricingAuditEvent(context, model, usage, cost, fallbackReason,
                        Configuration.Entities.BillingAuditEventType.UsageEstimated, billingAuditService);
                }

                return (cost, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "BILLING ALERT: Cost calculation failed for model {Model}; preserving request for reconciliation",
                    model);
                UsageMetrics.UsageTrackingFailures.WithLabels(
                    "pricing_calculation_error", UsageExtractor.DetermineRequestType(context.Request.Path)).Inc();
                LogPricingAuditEvent(context, model, usage, null, ex.Message,
                    Configuration.Entities.BillingAuditEventType.PricingCalculationFailed, billingAuditService);
                return (0m, true);
            }
        }

        private static void LogPricingAuditEvent(
            HttpContext context,
            string model,
            Usage usage,
            decimal? cost,
            string reason,
            Configuration.Entities.BillingAuditEventType eventType,
            IBillingAuditService billingAuditService)
        {
            var providerType = context.Items.TryGetValue("ProviderType", out var providerTypeObj)
                ? providerTypeObj?.ToString() ?? "unknown"
                : "unknown";

            billingAuditService.LogBillingEvent(new Configuration.Entities.BillingAuditEvent
            {
                EventType = eventType,
                VirtualKeyId = context.Items.TryGetValue("VirtualKeyId", out var keyIdObj) && keyIdObj is int keyId
                    ? keyId
                    : null,
                Model = model,
                RequestId = context.TraceIdentifier,
                RequestPath = context.Request.Path.ToString(),
                HttpStatusCode = context.Response.StatusCode,
                ProviderType = providerType,
                UsageJson = System.Text.Json.JsonSerializer.Serialize(
                    usage,
                    CoreHttpJsonContext.Default.Usage),
                CalculatedCost = cost,
                IsEstimated = eventType == Configuration.Entities.BillingAuditEventType.UsageEstimated,
                FailureReason = reason.Length <= 500 ? reason : reason[..500]
            });
            UsageMetrics.BillingAuditEvents.WithLabels(eventType.ToString(), providerType).Inc();
        }

        private async Task LogRequestAsync(
            HttpContext context,
            int virtualKeyId,
            string model,
            Usage usage,
            decimal cost,
            IRequestLogRuntimeWriter requestLogService,
            string? metadata = null)
        {
            try
            {
                var billedAtUtc = GetBillingTimestamp(context);
                var requestType = UsageExtractor.DetermineRequestType(context.Request.Path);

                // Extract provider info from HttpContext.Items (set by controllers)
                int? providerId = context.Items.TryGetValue("ProviderId", out var providerIdObj) && providerIdObj is int pid
                    ? pid
                    : null;
                var providerType = context.Items.TryGetValue("ProviderType", out var providerTypeObj)
                    ? providerTypeObj?.ToString()
                    : null;

                // Record how the request was billed so refunds can be calculated correctly. A trusted
                // provider-reported cost is billed via CostCalculationService's short-circuit; mirror
                // that condition here to tag the log.
                var billedFromProviderCost =
                    usage.ProviderCostPolicy is { TrustProviderReportedCost: true } &&
                    usage.ProviderReportedCostUsd is >= 0m;

                var logRequest = new LogRequestDto
                {
                    VirtualKeyId = virtualKeyId,
                    ModelName = model,
                    ProviderId = providerId,
                    ProviderType = providerType,
                    ModelProviderMappingId = context.Items.TryGetValue(HttpContextKeys.ModelProviderMappingId, out var mappingObj) && mappingObj is int mappingId ? mappingId : null,
                    PromptCachingEligible = context.Items.TryGetValue(HttpContextKeys.PromptCachingEligible, out var eligibleObj) && eligibleObj is true,
                    PromptCachingPolicyApplied = context.Items.TryGetValue(HttpContextKeys.PromptCachingPolicyApplied, out var appliedObj) && appliedObj is true,
                    CachedReadSavings = context.Items.TryGetValue(HttpContextKeys.CachedReadSavings, out var savingsObj) && savingsObj is decimal savings ? savings : 0m,
                    CacheWritePremium = context.Items.TryGetValue(HttpContextKeys.CacheWritePremium, out var premiumObj) && premiumObj is decimal premium ? premium : 0m,
                    RoutingAffinityUsed = context.Items.TryGetValue(HttpContextKeys.RoutingAffinityUsed, out var affinityObj) && affinityObj is true,
                    RoutingDecisionReason = context.Items.TryGetValue(HttpContextKeys.RoutingDecisionReason, out var reasonObj) ? reasonObj as string : null,
                    RoutingFailoverCount = context.Items.TryGetValue(HttpContextKeys.RoutingFailoverCount, out var failoverObj) && failoverObj is int failovers ? failovers : 0,
                    RequestType = requestType,
                    InputTokens = usage.PromptTokens ?? 0,
                    OutputTokens = usage.CompletionTokens ?? 0,
                    CachedInputTokens = usage.CachedInputTokens,
                    CachedWriteTokens = usage.CachedWriteTokens,
                    Cost = cost,
                    BillingMethod = billedFromProviderCost
                        ? ConduitLLM.Configuration.Enums.RequestBillingMethod.ProviderReportedCost
                        : ConduitLLM.Configuration.Enums.RequestBillingMethod.ModelCost,
                    ProviderReportedCostUsd = billedFromProviderCost ? usage.ProviderReportedCostUsd : null,
                    ProviderCostMarkupMultiplier = billedFromProviderCost
                        ? (usage.ProviderCostPolicy!.MarkupMultiplier > 0m ? usage.ProviderCostPolicy.MarkupMultiplier : 1m)
                        : null,
                    BilledAtUtc = cost > 0 ? billedAtUtc : null,
                    Timestamp = billedAtUtc,
                    ResponseTimeMs = UsageExtractor.GetResponseTime(context),
                    UserId = context.User?.Identity?.Name,
                    ClientIp = context.Connection.RemoteIpAddress?.ToString(),
                    RequestPath = context.Request.Path.ToString(),
                    StatusCode = context.Response.StatusCode,
                    Metadata = string.IsNullOrWhiteSpace(metadata)
                        ? null
                        : JsonSerializer.Deserialize(
                            metadata,
                            CoreHttpJsonContext.Default.DictionaryStringJsonElement)
                };

                await requestLogService.LogRequestAsync(logRequest);

                _logger.LogInformation(
                    "Tracked usage for VirtualKey {VirtualKeyId}: Model={Model}, PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, CachedInput={CachedInput}, CachedWrite={CachedWrite}, Cost={Cost:C}",
                    virtualKeyId, model, usage.PromptTokens, usage.CompletionTokens, usage.CachedInputTokens, usage.CachedWriteTokens, cost);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log request for VirtualKey {VirtualKeyId}", virtualKeyId);
                // Don't throw - logging failure shouldn't break the request
            }
        }

        private static DateTime GetBillingTimestamp(HttpContext context)
        {
            const string key = "Conduit.BillingOccurredAtUtc";
            if (context.Items.TryGetValue(key, out var existing) && existing is DateTime timestamp)
                return timestamp;

            var now = DateTime.UtcNow;
            context.Items[key] = now;
            return now;
        }

        #region Billing Audit Logging

        private async Task LogBillingDecisionAsync(HttpContext context, IBillingAuditService billingAuditService)
        {
            await BillingPolicyHandler.LogBillingDecisionAsync(context, billingAuditService, _logger);
        }

        private void LogSuccessfulBilling(HttpContext context, string model, Usage usage, decimal cost,
            string providerType, IBillingAuditService billingAuditService, string? toolUsageJson = null, decimal? toolCost = null)
        {
            BillingPolicyHandler.LogSuccessfulBilling(context, model, usage, cost, providerType, billingAuditService, _logger, toolUsageJson, toolCost);
        }

        private void LogZeroCostBilling(HttpContext context, string model, Usage usage, decimal cost,
            string providerType, IBillingAuditService billingAuditService, string? toolUsageJson = null, decimal? toolCost = null)
        {
            BillingPolicyHandler.LogZeroCostBilling(context, model, usage, cost, providerType, billingAuditService, toolUsageJson, toolCost, _logger);
        }

        private void LogMissingUsageData(HttpContext context, IBillingAuditService billingAuditService,
            string? failureReason = null, string metricReason = "no_usage_in_response", string? model = null)
        {
            BillingPolicyHandler.LogMissingUsageData(context, billingAuditService, failureReason, metricReason, model);
        }

        private void LogStreamingBilling(HttpContext context, string model, Usage usage, decimal cost,
            string providerType, bool isEstimated, IBillingAuditService billingAuditService, string? toolUsageJson = null, decimal? toolCost = null)
        {
            BillingPolicyHandler.LogStreamingBilling(context, model, usage, cost, providerType, isEstimated, billingAuditService, _logger, toolUsageJson, toolCost);
        }

        private void LogMissingStreamingUsage(HttpContext context, IBillingAuditService billingAuditService)
        {
            BillingPolicyHandler.LogMissingStreamingUsage(context, billingAuditService);
        }

        private void LogJsonParseError(HttpContext context, Exception ex, IBillingAuditService billingAuditService)
        {
            BillingPolicyHandler.LogJsonParseError(context, ex, billingAuditService);
        }

        private void LogUnexpectedError(HttpContext context, Exception ex, IBillingAuditService billingAuditService)
        {
            BillingPolicyHandler.LogUnexpectedError(context, ex, billingAuditService);
        }

        #endregion

        #region Prompt Caching Metrics

        /// <summary>
        /// Records prompt caching request-level metrics (hit/miss/disabled).
        /// </summary>
        private static void RecordPromptCachingMetrics(HttpContext context, Usage usage, string model, string provider)
        {
            var read = usage.CachedInputTokens is > 0;
            var write = usage.CachedWriteTokens is > 0;
            var eligible = context.Items.TryGetValue(HttpContextKeys.PromptCachingEligible, out var eligibleValue) && eligibleValue is true;
            var mapping = context.Items.TryGetValue(HttpContextKeys.ModelProviderMappingId, out var mappingValue)
                ? Convert.ToString(mappingValue, System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"
                : "unknown";
            if (read) PromptCachingMetrics.RecordCacheHit(model, provider, mapping);
            if (write) PromptCachingMetrics.RecordCacheWrite(model, provider, mapping);
            if (eligible && !read) PromptCachingMetrics.RecordCacheMiss(model, provider, mapping);
            if (!eligible && provider is "Replicate" or "MiniMax") PromptCachingMetrics.RecordCacheUnsupported(model, provider, mapping);
            else if (!eligible && !usage.CachedInputTokens.HasValue && !usage.CachedWriteTokens.HasValue)
                PromptCachingMetrics.RecordCacheUnknown(model, provider, mapping);
        }

        /// <summary>
        /// Calculates and records prompt caching cost savings.
        /// </summary>
        private static async Task RecordPromptCachingSavingsAsync(
            HttpContext context,
            ICostCalculationService costCalculationService,
            string model,
            Usage usage)
        {
            if (usage.CachedInputTokens is not > 0 && usage.CachedWriteTokens is not > 0)
                return;

            try
            {
                decimal savings = 0m;
                var providerType = context.Items.TryGetValue("ProviderType", out var pt)
                    ? pt?.ToString() ?? "unknown"
                    : "unknown";

                if (usage.CachedInputTokens is > 0 &&
                    context.Items.TryGetValue(HttpContextKeys.ModelCostId, out var mcIdObj) && mcIdObj is int mcId)
                {
                    savings = await costCalculationService.CalculateCacheSavingsByIdAsync(mcId, usage);
                }
                else if (usage.CachedInputTokens is > 0)
                {
                    savings = await costCalculationService.CalculateCacheSavingsAsync(model, usage);
                }

                PromptCachingMetrics.RecordSavings(model, providerType, Convert.ToDouble(savings));
                context.Items[HttpContextKeys.CachedReadSavings] = savings;

                decimal writePremium;
                if (context.Items.TryGetValue(HttpContextKeys.ModelCostId, out var writeMcIdObj) && writeMcIdObj is int writeMcId)
                    writePremium = await costCalculationService.CalculateCacheWritePremiumByIdAsync(writeMcId, usage);
                else
                    writePremium = await costCalculationService.CalculateCacheWritePremiumAsync(model, usage);
                PromptCachingMetrics.RecordWritePremium(model, providerType, Convert.ToDouble(writePremium));
                context.Items[HttpContextKeys.CacheWritePremium] = writePremium;
            }
            catch
            {
                // Non-critical — don't fail the request pipeline for savings calculation
            }
        }

        #endregion
    }
}
