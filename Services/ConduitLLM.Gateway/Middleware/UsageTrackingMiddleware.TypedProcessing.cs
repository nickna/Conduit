using System.Text.Json;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration;
using ConduitLLM.Core;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.UsageTracking;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Gateway.Serialization;
using IVirtualKeyService = ConduitLLM.Core.Interfaces.IVirtualKeyService;

namespace ConduitLLM.Gateway.Middleware;

public partial class UsageTrackingMiddleware
{
    private async Task ProcessTypedResponseAsync(
        HttpContext context,
        ICostCalculationService costCalculationService,
        IBatchSpendUpdateService batchSpendService,
        IRequestLogService requestLogService,
        IVirtualKeyService virtualKeyService,
        IBillingAuditService billingAuditService,
        IToolCostCalculationService toolCostCalculationService)
    {
        if (context.Response.StatusCode >= StatusCodes.Status400BadRequest)
        {
            await LogBillingDecisionAsync(context, billingAuditService);
            return;
        }

        var snapshot = context.GetRequestAccountingSnapshot();
        if (snapshot is null || snapshot.Operation == RequestOperation.Unknown)
        {
            LogMissingUsageData(
                context,
                billingAuditService,
                "Controller did not publish typed accounting evidence",
                "missing_typed_evidence");
            return;
        }

        if (snapshot.Operation == RequestOperation.Function)
        {
            await ProcessTypedFunctionResponseAsync(
                context,
                snapshot,
                batchSpendService,
                requestLogService,
                virtualKeyService,
                billingAuditService);
            return;
        }

        if (snapshot.Operation is RequestOperation.Image or RequestOperation.Video or RequestOperation.Audio)
        {
            await ProcessTypedMediaResponseAsync(
                context,
                snapshot,
                costCalculationService,
                batchSpendService,
                requestLogService,
                virtualKeyService,
                billingAuditService);
            return;
        }

        await ProcessTypedModelUsageAsync(
            context,
            snapshot,
            costCalculationService,
            batchSpendService,
            requestLogService,
            virtualKeyService,
            billingAuditService,
            toolCostCalculationService);
    }

    private async Task ProcessTypedModelUsageAsync(
        HttpContext context,
        RequestAccountingSnapshot snapshot,
        ICostCalculationService costCalculationService,
        IBatchSpendUpdateService batchSpendService,
        IRequestLogService requestLogService,
        IVirtualKeyService virtualKeyService,
        IBillingAuditService billingAuditService,
        IToolCostCalculationService toolCostCalculationService)
    {
        var endpointType = UsageExtractor.DetermineRequestType(context.Request.Path);
        var evidence = snapshot.ProviderUsage;
        if (evidence is null)
        {
            LogMissingUsageData(
                context,
                billingAuditService,
                "Controller did not publish provider usage evidence",
                "missing_typed_usage",
                snapshot.RequestedModel);
            return;
        }

        if (snapshot.VirtualKeyId is not int virtualKeyId)
        {
            LogMissingUsageData(
                context,
                billingAuditService,
                "Typed accounting evidence did not contain a Virtual Key identifier",
                "missing_virtual_key",
                evidence.Model);
            return;
        }

        var model = evidence.Model;
        var usage = evidence.Usage;
        ApplyProviderBillingPolicy(context, usage);
        var providerType = context.Items.TryGetValue("ProviderType", out var providerTypeValue)
            ? providerTypeValue?.ToString() ?? "unknown"
            : "unknown";
        var pricingResult = await CalculateTrackedCostAsync(
            context,
            model,
            usage,
            costCalculationService,
            billingAuditService);
        var providerTypeEnum = Enum.TryParse<ProviderType>(providerType, true, out var parsedProviderType)
            ? parsedProviderType
            : ProviderType.OpenAI;
        var providerToolUsage = snapshot.ProviderToolUsage is null
            ? null
            : new ToolUsageData
            {
                Tools = snapshot.ProviderToolUsage.Tools.Select(tool => new ToolUsageItem
                {
                    ToolName = tool.ToolName,
                    Count = tool.Count,
                    DurationSeconds = tool.DurationSeconds
                }).ToList()
            };
        decimal? providerToolCost = null;
        string? providerToolUsageJson = null;
        if (providerToolUsage is not null)
        {
            var toolCostResult = await toolCostCalculationService.CalculateToolCostsAsync(
                providerToolUsage,
                providerTypeEnum);
            providerToolUsageJson = toolCostCalculationService.SerializeToolUsage(providerToolUsage);
            providerToolCost = toolCostResult.TotalCost;
        }

        var totalCost = pricingResult.Cost + (providerToolCost ?? 0m) + snapshot.FunctionExecutionCost;
        var toolCallMetadata = snapshot.StreamingToolCalls.Count > 0
            ? JsonSerializer.Serialize(
                snapshot.StreamingToolCalls.ToList(),
                CoreHttpJsonContext.Default.ListToolCall)
            : providerToolUsageJson;

        UsageMetrics.UsageTrackingRequests.WithLabels(endpointType, "success").Inc();
        if (usage.PromptTokens is int promptTokens)
            UsageMetrics.UsageTrackingTokens.WithLabels(model, providerType, "prompt").Inc(promptTokens);
        if (usage.CompletionTokens is int completionTokens)
            UsageMetrics.UsageTrackingTokens.WithLabels(model, providerType, "completion").Inc(completionTokens);
        if (usage.CachedInputTokens is > 0)
            UsageMetrics.UsageTrackingTokens.WithLabels(model, providerType, "cached_input").Inc(usage.CachedInputTokens.Value);
        if (usage.CachedWriteTokens is > 0)
            UsageMetrics.UsageTrackingTokens.WithLabels(model, providerType, "cached_write").Inc(usage.CachedWriteTokens.Value);
        UsageMetrics.UsageTrackingCosts.WithLabels(model, providerType, endpointType)
            .Inc(Convert.ToDouble(totalCost));

        var requestStatus = context.Response.StatusCode is >= 200 and < 300 ? "success" : "error";
        BusinessMetricsService.RecordModelRequest(model, providerType, requestStatus);
        BusinessMetricsService.RecordTokens(
            model,
            providerType,
            usage.PromptTokens ?? 0,
            usage.CompletionTokens ?? 0,
            usage.CachedInputTokens,
            usage.CachedWriteTokens);
        BusinessMetricsService.RecordResponseTime(
            model,
            providerType,
            UsageExtractor.GetResponseTime(context) / 1000.0);
        if (totalCost > 0m)
            BusinessMetricsService.RecordCost(providerType, model, endpointType, Convert.ToDouble(totalCost));

        RecordPromptCachingMetrics(context, usage, model, providerType);
        await RecordPromptCachingSavingsAsync(context, costCalculationService, model, usage);

        if (totalCost > 0m)
        {
            if (await RecordSpendOrSettleReservationAsync(
                    context,
                    virtualKeyId,
                    totalCost,
                    batchSpendService,
                    virtualKeyService))
            {
                LogSuccessfulBilling(
                    context,
                    model,
                    usage,
                    totalCost,
                    providerType,
                    billingAuditService,
                    providerToolUsageJson,
                    providerToolCost);
            }
        }
        else if (!pricingResult.Failed)
        {
            if (snapshot.Reservation is not null)
            {
                await RecordSpendOrSettleReservationAsync(
                    context,
                    virtualKeyId,
                    0m,
                    batchSpendService,
                    virtualKeyService);
            }

            UsageMetrics.ZeroCostEvents.WithLabels(model, "zero_cost").Inc();
            LogZeroCostBilling(
                context,
                model,
                usage,
                totalCost,
                providerType,
                billingAuditService,
                providerToolUsageJson,
                providerToolCost);
        }

        await LogRequestAsync(
            context,
            virtualKeyId,
            model,
            usage,
            totalCost,
            requestLogService,
            toolCallMetadata);
    }

    private async Task ProcessTypedMediaResponseAsync(
        HttpContext context,
        RequestAccountingSnapshot snapshot,
        ICostCalculationService costCalculationService,
        IBatchSpendUpdateService batchSpendService,
        IRequestLogService requestLogService,
        IVirtualKeyService virtualKeyService,
        IBillingAuditService billingAuditService)
    {
        if (snapshot.ProviderUsage is not { } evidence || snapshot.VirtualKeyId is not int virtualKeyId)
        {
            LogMissingUsageData(
                context,
                billingAuditService,
                "Media controller did not publish complete typed accounting evidence",
                "missing_typed_media_usage",
                snapshot.RequestedModel);
            return;
        }

        var providerType = context.Items.TryGetValue("ProviderType", out var providerTypeValue)
            ? providerTypeValue?.ToString() ?? "unknown"
            : "unknown";
        var usageContext = context.GetUsageContext();
        var mediaType = UsageExtractor.DetermineRequestType(context.Request.Path);
        var metadata = snapshot.MetadataJson ?? JsonSerializer.Serialize(
            usageContext switch
            {
                ImageUsageContext image => new MediaUsageMetadata(
                    "image",
                    ImageCount: evidence.Usage.ImageCount,
                    Quality: image.Quality,
                    Size: image.Size,
                    Style: image.Style),
                VideoUsageContext video => new MediaUsageMetadata(
                    "video",
                    DurationSeconds: evidence.Usage.VideoDurationSeconds,
                    Resolution: evidence.Usage.VideoResolution,
                    Fps: video.Fps,
                    Style: video.Style,
                    PricingParametersUsed: video.PricingParameters?.Keys.ToArray()),
                AudioUsageContext audio => new MediaUsageMetadata(
                    mediaType,
                    AudioDurationSeconds: audio.AudioDurationSeconds,
                    TtsCharacters: audio.TtsCharacters),
                _ => new MediaUsageMetadata(mediaType)
            },
            GatewayInternalJsonContext.Default.MediaUsageMetadata);

        ApplyProviderBillingPolicy(context, evidence.Usage);
        await ProcessMediaResponseAsync(context, new MediaProcessingContext
        {
            MediaType = mediaType,
            Model = evidence.Model,
            Usage = evidence.Usage,
            MetadataJson = metadata,
            ProviderType = providerType,
            VirtualKeyId = virtualKeyId,
            LogDetail = "typed accounting evidence",
            BillingDeferred = snapshot.Operation is RequestOperation.Video or RequestOperation.Image &&
                              context.Response.StatusCode == StatusCodes.Status202Accepted
        }, costCalculationService, batchSpendService, requestLogService, virtualKeyService, billingAuditService);
    }

    private async Task ProcessTypedFunctionResponseAsync(
        HttpContext context,
        RequestAccountingSnapshot snapshot,
        IBatchSpendUpdateService batchSpendService,
        IRequestLogService requestLogService,
        IVirtualKeyService virtualKeyService,
        IBillingAuditService billingAuditService)
    {
        if (snapshot.DirectCost is not { } directCost || snapshot.VirtualKeyId is not int virtualKeyId)
        {
            LogMissingUsageData(
                context,
                billingAuditService,
                "Function controller did not publish direct-cost evidence",
                "missing_typed_function_cost",
                snapshot.RequestedModel);
            return;
        }

        var providerType = context.Items.TryGetValue("ProviderType", out var providerTypeValue)
            ? providerTypeValue?.ToString() ?? "unknown"
            : "unknown";
        var usage = new Usage { PromptTokens = 0, CompletionTokens = 0, TotalTokens = 0 };
        UsageMetrics.UsageTrackingRequests.WithLabels("function", "success").Inc();
        UsageMetrics.UsageTrackingCosts.WithLabels(directCost.OperationName, providerType, "function")
            .Inc(Convert.ToDouble(directCost.ActualCost));

        if (directCost.ActualCost > 0m)
        {
            if (await RecordSpendOrSettleReservationAsync(
                    context,
                    virtualKeyId,
                    directCost.ActualCost,
                    batchSpendService,
                    virtualKeyService))
            {
                LogSuccessfulBilling(
                    context,
                    directCost.OperationName,
                    usage,
                    directCost.ActualCost,
                    providerType,
                    billingAuditService);
            }
        }
        else
        {
            LogZeroCostBilling(
                context,
                directCost.OperationName,
                usage,
                0m,
                providerType,
                billingAuditService);
        }

        await LogRequestAsync(
            context,
            virtualKeyId,
            directCost.OperationName,
            usage,
            directCost.ActualCost,
            requestLogService,
            directCost.MetadataJson);
    }
}
