using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Gateway.Services;
using IVirtualKeyRuntimeService = ConduitLLM.Core.Interfaces.IVirtualKeyRuntimeService;

namespace ConduitLLM.Gateway.Middleware;

public partial class UsageTrackingMiddleware
{
    private sealed class MediaProcessingContext
    {
        public required string MediaType { get; init; }
        public required string Model { get; init; }
        public required Usage Usage { get; init; }
        public required string MetadataJson { get; init; }
        public required string ProviderType { get; init; }
        public required int VirtualKeyId { get; init; }
        public required string LogDetail { get; init; }
        public bool BillingDeferred { get; init; }
    }

    private async Task ProcessMediaResponseAsync(
        HttpContext context,
        MediaProcessingContext media,
        ICostCalculationService costCalculationService,
        IBatchSpendUpdateService batchSpendService,
        IRequestLogRuntimeWriter requestLogService,
        IVirtualKeyRuntimeService virtualKeyService,
        IBillingAuditService billingAuditService)
    {
        decimal cost;
        var pricingFailed = false;
        if (media.BillingDeferred)
        {
            cost = 0m;
        }
        else
        {
            var pricingResult = await CalculateTrackedCostAsync(
                context,
                media.Model,
                media.Usage,
                costCalculationService,
                billingAuditService);
            cost = pricingResult.Cost;
            pricingFailed = pricingResult.Failed;
        }

        UsageMetrics.UsageTrackingRequests.WithLabels(media.MediaType, "success").Inc();
        UsageMetrics.UsageTrackingCosts.WithLabels(media.Model, media.ProviderType, media.MediaType)
            .Inc(Convert.ToDouble(cost));

        var requestStatus = context.Response.StatusCode is >= 200 and < 300 ? "success" : "error";
        BusinessMetricsService.RecordModelRequest(media.Model, media.ProviderType, requestStatus);
        BusinessMetricsService.RecordResponseTime(
            media.Model,
            media.ProviderType,
            UsageExtractor.GetResponseTime(context) / 1000.0);
        if (cost > 0m)
        {
            BusinessMetricsService.RecordCost(
                media.ProviderType,
                media.Model,
                media.MediaType,
                Convert.ToDouble(cost));
        }

        if (cost > 0m)
        {
            if (await RecordSpendOrSettleReservationAsync(
                    context,
                    media.VirtualKeyId,
                    cost,
                    batchSpendService,
                    virtualKeyService))
            {
                LogSuccessfulBilling(
                    context,
                    media.Model,
                    media.Usage,
                    cost,
                    media.ProviderType,
                    billingAuditService);
            }
        }
        else if (!pricingFailed)
        {
            UsageMetrics.ZeroCostEvents.WithLabels(media.Model, $"{media.MediaType}_zero").Inc();
            LogZeroCostBilling(
                context,
                media.Model,
                media.Usage,
                cost,
                media.ProviderType,
                billingAuditService);
        }

        await LogRequestAsync(
            context,
            media.VirtualKeyId,
            media.Model,
            media.Usage,
            cost,
            requestLogService,
            media.MetadataJson);

        _logger.LogInformation(
            "Tracked {MediaType} generation for VirtualKey {VirtualKeyId}: Model={Model}, {Detail}, Cost={Cost:C}, BillingDeferred={BillingDeferred}",
            media.MediaType,
            media.VirtualKeyId,
            media.Model,
            media.LogDetail,
            cost,
            media.BillingDeferred);
    }
}
