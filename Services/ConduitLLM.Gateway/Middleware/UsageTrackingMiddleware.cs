using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Gateway.Constants;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.UsageTracking;
using ConduitLLM.Gateway.Utilities;
using IVirtualKeyRuntimeService = ConduitLLM.Core.Interfaces.IVirtualKeyRuntimeService;

namespace ConduitLLM.Gateway.Middleware;

/// <summary>
/// Finalizes controller-published usage evidence without intercepting response transport.
/// </summary>
public partial class UsageTrackingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<UsageTrackingMiddleware> _logger;

    public UsageTrackingMiddleware(
        RequestDelegate next,
        ILogger<UsageTrackingMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(
        HttpContext context,
        ICostCalculationService costCalculationService,
        IBatchSpendUpdateService batchSpendService,
        IRequestLogRuntimeWriter requestLogService,
        IVirtualKeyRuntimeService virtualKeyService,
        IBillingAuditService billingAuditService,
        IToolCostCalculationService toolCostCalculationService)
    {
        if (!ShouldTrackUsage(context))
        {
            await LogBillingDecisionAsync(context, billingAuditService);
            await _next(context);
            return;
        }

        using var activity = GatewayRequestMetrics.StartUsageTrackingActivity(
            UsageExtractor.DetermineRequestType(context.Request.Path));

        try
        {
            try
            {
                await _next(context);
            }
            finally
            {
                if (context.Response.ContentType?.Contains("text/event-stream") == true)
                {
                    await TrackStreamingUsageAsync(
                        context,
                        costCalculationService,
                        batchSpendService,
                        requestLogService,
                        virtualKeyService,
                        billingAuditService,
                        toolCostCalculationService);
                }
            }

            if (context.Response.ContentType?.Contains("text/event-stream") == true)
            {
                _logger.LogDebug("Detected streaming response; response body was never intercepted");
                return;
            }

            try
            {
                await ProcessTypedResponseAsync(
                    context,
                    costCalculationService,
                    batchSpendService,
                    requestLogService,
                    virtualKeyService,
                    billingAuditService,
                    toolCostCalculationService);
            }
            catch (Exception accountingEx)
            {
                var accountingContext = context.GetOrCreateRequestAccountingContext();
                accountingContext.MarkIndeterminate(
                    "Typed accounting finalization failed after the response was produced");
                _logger.LogCritical(
                    accountingEx,
                    "Typed accounting finalization failed for {Path}",
                    LoggingSanitizer.S(context.Request.Path.ToString()));
                LogUnexpectedError(context, accountingEx, billingAuditService);
            }
        }
        finally
        {
            try
            {
                await FinalizeOpenReservationAsync(context);
            }
            catch (Exception reservationFinalizationEx)
            {
                _logger.LogCritical(
                    reservationFinalizationEx,
                    "Failed to finalize the request spend reservation for {Path}",
                    LoggingSanitizer.S(context.Request.Path.ToString()));
            }
        }
    }

    private bool ShouldTrackUsage(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/v1") ||
            !context.Items.ContainsKey("VirtualKeyId") ||
            context.Response.StatusCode >= StatusCodes.Status400BadRequest)
        {
            return false;
        }

        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
        if (path.Contains("/tasks/") || path.Contains("/status"))
            return false;

        return path.Contains("/completions") ||
               path.Contains("/responses") ||
               path.Contains("/embeddings") ||
               path.Contains("/images/generations") ||
               path.Contains("/audio/transcriptions") ||
               path.Contains("/audio/speech") ||
               path.Contains("/videos/generations") ||
               path.Contains("/rerank") ||
               path.Contains("/functions/execute");
    }

    private static void ApplyProviderBillingPolicy(HttpContext context, ConduitLLM.Core.Models.Usage usage)
    {
        if (context.Items.TryGetValue(HttpContextKeys.ProviderBillingPolicy, out var policyValue) &&
            policyValue is ConduitLLM.Core.Models.ProviderCostBillingPolicy policy)
        {
            usage.ProviderCostPolicy = policy;
        }

        if (usage.ProviderReportedCostUsd is null &&
            context.Items.TryGetValue(HttpContextKeys.ProviderReportedCost, out var costValue) &&
            costValue is decimal reportedCost)
        {
            usage.ProviderReportedCostUsd = reportedCost;
        }
    }
}

public static class UsageTrackingMiddlewareExtensions
{
    public static IApplicationBuilder UseUsageTracking(this IApplicationBuilder builder) =>
        builder.UseMiddleware<UsageTrackingMiddleware>();
}
