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
            var downstreamCompleted = false;
            try
            {
                await _next(context);
                downstreamCompleted = true;
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
                else if (downstreamCompleted || context.GetRequestAccountingSnapshot() is { } snapshot &&
                    (snapshot.ProviderUsage is not null || snapshot.ProviderInvocations?.Count > 0 ||
                     snapshot.DirectCost is not null || snapshot.FunctionExecutions.Count > 0 ||
                     snapshot.ProviderToolUsage?.Tools.Count > 0))
                {
                    // Published evidence survives a later provider or response-writing exception.
                    // Accounting failures must not replace the downstream exception.
                    try
                    {
                        await ProcessTypedResponseAsync(context, costCalculationService, batchSpendService,
                            requestLogService, virtualKeyService, billingAuditService, toolCostCalculationService);
                    }
                    catch (Exception accountingEx)
                    {
                        context.GetOrCreateRequestAccountingContext().MarkIndeterminate(
                            "Typed accounting finalization failed after request execution");
                        _logger.LogCritical(accountingEx, "Typed accounting finalization failed for {Path}",
                            LoggingSanitizer.S(context.Request.Path.ToString()));
                        try { LogUnexpectedError(context, accountingEx, billingAuditService); }
                        catch (Exception auditEx)
                        {
                            _logger.LogCritical(auditEx, "Failed to report typed accounting finalization error");
                        }
                    }
                }
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

    private static void ApplyProviderBillingPolicy(
        HttpContext context,
        ConduitLLM.Core.Models.Usage usage,
        bool includeAggregateReportedCost = true)
        => ProviderUsagePricing.ApplyProviderBillingPolicy(context, usage, includeAggregateReportedCost);
}

public static class UsageTrackingMiddlewareExtensions
{
    public static IApplicationBuilder UseUsageTracking(this IApplicationBuilder builder) =>
        builder.UseMiddleware<UsageTrackingMiddleware>();
}
