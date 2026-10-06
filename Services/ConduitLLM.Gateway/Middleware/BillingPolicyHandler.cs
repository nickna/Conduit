using System.Text.Json;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Core.Models;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Gateway.Constants;

namespace ConduitLLM.Gateway.Middleware
{
    /// <summary>
    /// Static helper methods for billing policy decisions and audit logging.
    /// Implements customer-friendly billing policy following Anthropic's approach.
    /// </summary>
    public static class BillingPolicyHandler
    {
        /// <summary>
        /// Logs billing decisions for transparency and audit purposes.
        /// Tracks when billing is skipped due to error responses or other policy reasons.
        /// </summary>
        public static Task LogBillingDecisionAsync(HttpContext context, IBillingAuditService billingAuditService, ILogger logger)
        {
            // Only log for API endpoints that would normally be tracked
            if (!context.Request.Path.StartsWithSegments("/v1"))
                return Task.CompletedTask;

            var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";
            var isTrackableEndpoint = path.Contains("/completions") || 
                                    path.Contains("/embeddings") || 
                                    path.Contains("/images/generations") ||
                                    path.Contains("/audio/transcriptions") ||
                                    path.Contains("/audio/speech") ||
                                    path.Contains("/videos/generations");

            if (!isTrackableEndpoint)
                return Task.CompletedTask;

            var virtualKeyId = context.Items.TryGetValue("VirtualKeyId", out var keyId) ? keyId : "none";
            var statusCode = context.Response.StatusCode;
            var requestId = context.TraceIdentifier;

            // Log reason for skipping billing
            if (statusCode >= 400)
            {
                logger.LogDebug(
                    "Billing Policy: Skipping billing for error response - " +
                    "Status={StatusCode}, VirtualKey={VirtualKeyId}, Path={Path}, RequestId={RequestId}, " +
                    "Reason=ErrorResponse_NoChargePolicy", 
                    statusCode, virtualKeyId, context.Request.Path, requestId);
                
                // Audit log error response skipped
                var providerType = context.Items.TryGetValue("ProviderType", out var pt) ? pt?.ToString() : "unknown";
                billingAuditService.LogBillingEvent(new BillingAuditEvent
                {
                    EventType = BillingAuditEventType.ErrorResponseSkipped,
                    VirtualKeyId = virtualKeyId is int vkId ? vkId : null,
                    RequestId = requestId,
                    RequestPath = context.Request.Path.ToString(),
                    HttpStatusCode = statusCode,
                    FailureReason = $"HTTP {statusCode} error response - no billing per policy",
                    ProviderType = providerType
                });
                
                // Increment metrics
                UsageMetrics.BillingAuditEvents.WithLabels("ErrorResponseSkipped", providerType ?? "unknown").Inc();
            }
            else if (!context.Items.ContainsKey("VirtualKeyId"))
            {
                logger.LogDebug(
                    "Billing Policy: Skipping billing - no virtual key found - " +
                    "Status={StatusCode}, Path={Path}, RequestId={RequestId}, " +
                    "Reason=NoVirtualKey", 
                    statusCode, context.Request.Path, requestId);
                
                // Audit log no virtual key
                billingAuditService.LogBillingEvent(new BillingAuditEvent
                {
                    EventType = BillingAuditEventType.NoVirtualKey,
                    RequestId = requestId,
                    RequestPath = context.Request.Path.ToString(),
                    HttpStatusCode = statusCode,
                    FailureReason = "No virtual key found for request"
                });
                
                // Increment metrics
                UsageMetrics.BillingAuditEvents.WithLabels("NoVirtualKey", "unknown").Inc();
            }
            
            return Task.CompletedTask;
        }

        /// <summary>
        /// Logs successful billing event with usage data.
        /// </summary>
        public static void LogSuccessfulBilling(HttpContext context, string model, Usage usage, decimal cost, 
            string providerType, IBillingAuditService billingAuditService, ILogger logger)
        {
            LogSuccessfulBilling(context, model, usage, cost, providerType, billingAuditService, logger, null, null);
        }

        /// <summary>
        /// Logs successful billing event with usage data, including optional tool usage.
        /// </summary>
        public static void LogSuccessfulBilling(HttpContext context, string model, Usage usage, decimal cost, 
            string providerType, IBillingAuditService billingAuditService, ILogger logger,
            string? toolUsageJson, decimal? toolCost)
        {
            var virtualKeyId = (int)context.Items["VirtualKeyId"]!;
            
            // Determine event type based on whether tools were used
            var eventType = toolUsageJson != null 
                ? BillingAuditEventType.ToolUsageTracked 
                : BillingAuditEventType.UsageTracked;
            
            billingAuditService.LogBillingEvent(new BillingAuditEvent
            {
                EventType = eventType,
                VirtualKeyId = virtualKeyId,
                Model = model,
                RequestId = context.TraceIdentifier,
                UsageJson = JsonSerializer.Serialize(usage, CoreHttpJsonContext.Default.Usage),
                CalculatedCost = cost,
                ProviderType = providerType,
                RequestPath = context.Request.Path.ToString(),
                HttpStatusCode = context.Response.StatusCode,
                ToolUsageJson = toolUsageJson,
                ToolUsageCost = toolCost
            });
            
            // Increment metrics
            UsageMetrics.BillingAuditEvents.WithLabels(eventType.ToString(), providerType ?? "unknown").Inc();
            UsageMetrics.BillingRevenue.WithLabels(model ?? "unknown", providerType ?? "unknown").Inc(Convert.ToDouble(cost));
            UsageMetrics.BillingCostDistribution.WithLabels(model ?? "unknown", providerType ?? "unknown").Observe(Convert.ToDouble(cost));
            
            // Log tool usage metrics if applicable
            if (toolCost.HasValue && toolCost.Value > 0)
            {
                logger.LogInformation("Tool usage billed: Model={Model}, ToolCost=${ToolCost:F6}, TotalCost=${TotalCost:F6}",
                    model, toolCost.Value, cost);
                UsageMetrics.BillingRevenue.WithLabels(model ?? "unknown", providerType ?? "unknown_tools").Inc(Convert.ToDouble(toolCost.Value));
            }
        }

        /// <summary>
        /// Logs billing event for zero cost calculations.
        /// </summary>
        public static void LogZeroCostBilling(HttpContext context, string model, Usage usage, decimal cost,
            string providerType, IBillingAuditService billingAuditService)
        {
            LogZeroCostBilling(context, model, usage, cost, providerType, billingAuditService, null, null, null);
        }

        /// <summary>
        /// Logs billing event for zero cost calculations, including optional tool usage.
        /// </summary>
        public static void LogZeroCostBilling(HttpContext context, string model, Usage usage, decimal cost,
            string providerType, IBillingAuditService billingAuditService,
            string? toolUsageJson, decimal? toolCost)
        {
            LogZeroCostBilling(context, model, usage, cost, providerType, billingAuditService, toolUsageJson, toolCost, null);
        }

        /// <summary>
        /// Logs billing event for zero cost calculations, including optional tool usage and logger for enhanced diagnostics.
        /// </summary>
        public static void LogZeroCostBilling(HttpContext context, string model, Usage usage, decimal cost,
            string providerType, IBillingAuditService billingAuditService,
            string? toolUsageJson, decimal? toolCost, ILogger? logger)
        {
            var virtualKeyId = (int)context.Items["VirtualKeyId"]!;

            // Determine if this is a missing tool cost config scenario
            var eventType = toolUsageJson != null && (!toolCost.HasValue || toolCost.Value == 0)
                ? BillingAuditEventType.ToolUsageMissingCostConfig
                : IsTrustedConfiguredZero(usage)
                    ? BillingAuditEventType.ConfiguredZeroCost
                    : HasPositiveConsumption(usage)
                        ? BillingAuditEventType.UnpricedUsage
                        : BillingAuditEventType.ZeroCostSkipped;

            // Get ModelCostId from context if available
            var modelCostIdInfo = context.Items.TryGetValue(HttpContextKeys.ModelCostId, out var mcIdObj) && mcIdObj is int mcId
                ? mcId.ToString()
                : "(not found in context)";

            // Log detailed information to help troubleshoot zero cost issues
            var failureReason = eventType switch
            {
                BillingAuditEventType.ToolUsageMissingCostConfig => "Tool usage detected but no cost configuration found",
                BillingAuditEventType.ConfiguredZeroCost => "Trusted provider explicitly reported zero cost",
                BillingAuditEventType.UnpricedUsage =>
                    $"Positive usage produced zero cost - reconciliation required. ModelCostId={modelCostIdInfo}",
                _ => $"No billable usage produced zero cost. ModelCostId={modelCostIdInfo}"
            };

            // Log at Information level for visibility in production
            logger?.LogInformation(
                "Zero cost billing event for VirtualKeyId {VirtualKeyId}: Model={Model}, Provider={ProviderType}, " +
                "Path={RequestPath}, RequestId={RequestId}, ModelCostId={ModelCostId}. " +
                "Usage: PromptTokens={PromptTokens}, CompletionTokens={CompletionTokens}, ImageCount={ImageCount}, " +
                "ImageQuality={ImageQuality}, ImageResolution={ImageResolution}. " +
                "This may indicate a missing or misconfigured cost entry.",
                virtualKeyId, model, providerType, context.Request.Path, context.TraceIdentifier, modelCostIdInfo,
                usage.PromptTokens, usage.CompletionTokens, usage.ImageCount,
                usage.ImageQuality, usage.ImageResolution);

            billingAuditService.LogBillingEvent(new BillingAuditEvent
            {
                EventType = eventType,
                VirtualKeyId = virtualKeyId,
                Model = model,
                RequestId = context.TraceIdentifier,
                UsageJson = JsonSerializer.Serialize(usage, CoreHttpJsonContext.Default.Usage),
                CalculatedCost = cost,
                ProviderType = providerType,
                RequestPath = context.Request.Path.ToString(),
                HttpStatusCode = context.Response.StatusCode,
                ToolUsageJson = toolUsageJson,
                ToolUsageCost = toolCost,
                FailureReason = failureReason
            });

            // Increment metrics
            UsageMetrics.BillingAuditEvents.WithLabels(eventType.ToString(), providerType ?? "unknown").Inc();

            if (eventType is BillingAuditEventType.ToolUsageMissingCostConfig or BillingAuditEventType.UnpricedUsage)
            {
                UsageMetrics.BillingRevenueLoss.WithLabels(eventType.ToString(), "unpriced_usage").Inc();
            }
            else
            {
                UsageMetrics.ZeroCostEvents.WithLabels(model ?? "unknown", "calculated_zero").Inc();
            }
        }

        private static bool IsTrustedConfiguredZero(Usage usage) =>
            usage.ProviderCostPolicy?.TrustProviderReportedCost == true &&
            usage.ProviderReportedCostUsd == 0m;

        private static bool HasPositiveConsumption(Usage usage) =>
            usage.PromptTokens is > 0 || usage.CompletionTokens is > 0 ||
            usage.CachedInputTokens is > 0 || usage.CachedWriteTokens is > 0 ||
            usage.ReasoningTokens is > 0 || usage.ImageCount is > 0 ||
            usage.VideoDurationSeconds is > 0 || usage.SearchUnits is > 0 ||
            usage.InferenceSteps is > 0 || usage.AudioDurationSeconds is > 0 ||
            usage.TtsCharacters is > 0;

        /// <summary>
        /// Logs billing event for missing usage data.
        /// </summary>
        public static void LogMissingUsageData(HttpContext context, IBillingAuditService billingAuditService,
            string? failureReason = null, string metricReason = "no_usage_in_response", string? model = null)
        {
            var vkId = context.Items.ContainsKey("VirtualKeyId") ? (int?)context.Items["VirtualKeyId"] : null;
            var providerType = context.Items.TryGetValue("ProviderType", out var pt) ? pt?.ToString() : "unknown";
            
            billingAuditService.LogBillingEvent(new BillingAuditEvent
            {
                EventType = BillingAuditEventType.MissingUsageData,
                VirtualKeyId = vkId,
                Model = model,
                RequestId = context.TraceIdentifier,
                RequestPath = context.Request.Path.ToString(),
                HttpStatusCode = context.Response.StatusCode,
                ProviderType = providerType,
                FailureReason = failureReason
            });
            
            // Increment metrics
            UsageMetrics.BillingAuditEvents.WithLabels("MissingUsageData", providerType ?? "unknown").Inc();
            UsageMetrics.BillingRevenueLoss.WithLabels("MissingUsageData", metricReason).Inc();
        }

        /// <summary>
        /// Logs billing event for streaming usage.
        /// </summary>
        public static void LogStreamingBilling(HttpContext context, string model, Usage usage, decimal cost, 
            string providerType, bool isEstimated, IBillingAuditService billingAuditService, ILogger logger)
        {
            LogStreamingBilling(context, model, usage, cost, providerType, isEstimated, billingAuditService, logger, null, null);
        }

        /// <summary>
        /// Logs billing event for streaming usage, including optional tool usage.
        /// </summary>
        public static void LogStreamingBilling(HttpContext context, string model, Usage usage, decimal cost, 
            string providerType, bool isEstimated, IBillingAuditService billingAuditService, ILogger logger,
            string? toolUsageJson, decimal? toolCost)
        {
            var virtualKeyId = (int)context.Items["VirtualKeyId"]!;
            
            // Determine event type based on estimation and tool usage
            var eventType = toolUsageJson != null 
                ? BillingAuditEventType.ToolUsageTracked
                : (isEstimated ? BillingAuditEventType.UsageEstimated : BillingAuditEventType.UsageTracked);
            
            billingAuditService.LogBillingEvent(new BillingAuditEvent
            {
                EventType = eventType,
                VirtualKeyId = virtualKeyId,
                Model = model,
                RequestId = context.TraceIdentifier,
                UsageJson = JsonSerializer.Serialize(usage, CoreHttpJsonContext.Default.Usage),
                CalculatedCost = cost,
                ProviderType = providerType,
                RequestPath = context.Request.Path.ToString(),
                HttpStatusCode = context.Response.StatusCode,
                IsEstimated = isEstimated,
                ToolUsageJson = toolUsageJson,
                ToolUsageCost = toolCost,
                FailureReason = isEstimated ? "Provider did not return usage data - usage was estimated conservatively" : null
            });
            
            // Increment metrics
            UsageMetrics.BillingAuditEvents.WithLabels(eventType.ToString(), providerType ?? "unknown").Inc();
            
            // Track estimated vs actual usage metrics
            if (isEstimated)
            {
                logger.LogInformation("Successfully billed estimated usage for streaming response: Cost={Cost:C}", cost);
                // Track that we recovered revenue through estimation
                UsageMetrics.BillingRevenue.WithLabels(model ?? "unknown", providerType ?? "unknown_estimated").Inc(Convert.ToDouble(cost));
            }
            
            // Log tool usage if applicable
            if (toolCost.HasValue && toolCost.Value > 0)
            {
                logger.LogInformation("Tool usage billed in streaming: Model={Model}, ToolCost=${ToolCost:F6}, TotalCost=${TotalCost:F6}",
                    model, toolCost.Value, cost);
                UsageMetrics.BillingRevenue.WithLabels(model ?? "unknown", providerType ?? "unknown_tools_stream").Inc(Convert.ToDouble(toolCost.Value));
            }
            
            UsageMetrics.BillingRevenue.WithLabels(model ?? "unknown", providerType ?? "unknown").Inc(Convert.ToDouble(cost));
            UsageMetrics.BillingCostDistribution.WithLabels(model ?? "unknown", providerType ?? "unknown").Observe(Convert.ToDouble(cost));
        }

        /// <summary>
        /// Logs billing event for missing streaming usage data.
        /// </summary>
        public static void LogMissingStreamingUsage(HttpContext context, IBillingAuditService billingAuditService)
        {
            var vkId = context.Items.ContainsKey("VirtualKeyId") ? (int?)context.Items["VirtualKeyId"] : null;
            var providerType = context.Items.TryGetValue("ProviderType", out var pt) ? pt?.ToString() : "unknown";
            
            billingAuditService.LogBillingEvent(new BillingAuditEvent
            {
                EventType = BillingAuditEventType.StreamingUsageMissing,
                VirtualKeyId = vkId,
                RequestId = context.TraceIdentifier,
                RequestPath = context.Request.Path.ToString(),
                HttpStatusCode = context.Response.StatusCode,
                FailureReason = "No provider usage in the typed request accounting snapshot",
                ProviderType = providerType
            });
            
            // Increment metrics
            UsageMetrics.BillingAuditEvents.WithLabels("StreamingUsageMissing", providerType ?? "unknown").Inc();
            UsageMetrics.BillingRevenueLoss.WithLabels("StreamingUsageMissing", "streaming_no_usage").Inc();
        }

        /// <summary>
        /// Logs billing event for JSON parsing errors.
        /// </summary>
        public static void LogJsonParseError(HttpContext context, Exception ex, IBillingAuditService billingAuditService)
        {
            var virtualKeyId = context.Items.ContainsKey("VirtualKeyId") ? (int?)context.Items["VirtualKeyId"] : null;
            var providerType = context.Items.TryGetValue("ProviderType", out var pt) ? pt?.ToString() : "unknown";
            
            billingAuditService.LogBillingEvent(new BillingAuditEvent
            {
                EventType = BillingAuditEventType.JsonParseError,
                VirtualKeyId = virtualKeyId,
                RequestId = context.TraceIdentifier,
                RequestPath = context.Request.Path.ToString(),
                HttpStatusCode = context.Response.StatusCode,
                FailureReason = ex.Message,
                ProviderType = providerType
            });
            
            // Increment metrics
            UsageMetrics.BillingAuditEvents.WithLabels("JsonParseError", providerType ?? "unknown").Inc();
            UsageMetrics.BillingRevenueLoss.WithLabels("JsonParseError", "parsing_failed").Inc();
        }

        /// <summary>
        /// Logs billing event for unexpected errors.
        /// </summary>
        public static void LogUnexpectedError(HttpContext context, Exception ex, IBillingAuditService billingAuditService)
        {
            var virtualKeyId = context.Items.ContainsKey("VirtualKeyId") ? (int?)context.Items["VirtualKeyId"] : null;
            var providerType = context.Items.TryGetValue("ProviderType", out var pt) ? pt?.ToString() : "unknown";
            
            billingAuditService.LogBillingEvent(new BillingAuditEvent
            {
                EventType = BillingAuditEventType.UnexpectedError,
                VirtualKeyId = virtualKeyId,
                RequestId = context.TraceIdentifier,
                RequestPath = context.Request.Path.ToString(),
                HttpStatusCode = context.Response.StatusCode,
                FailureReason = ex.Message,
                ProviderType = providerType
            });
            
            // Increment metrics
            UsageMetrics.BillingAuditEvents.WithLabels("UnexpectedError", providerType ?? "unknown").Inc();
            UsageMetrics.BillingRevenueLoss.WithLabels("UnexpectedError", "exception").Inc();
        }
    }
}
