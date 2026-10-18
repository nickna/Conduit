using System.Text.Json;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Helpers;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Metrics;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Core.Services.Strategies;
using ConduitLLM.Core.Utilities;

namespace ConduitLLM.Gateway.EventHandlers;

/// <summary>Imports verified original output; it has no generation client or billing service.</summary>
public sealed class IndeterminateMediaTaskRecoveryRequestedHandler(IMediaTaskReconciliationStore store,
    IMediaTaskTerminalWriter terminalWriter, IHttpClientFactory clients, IMediaStorageService storage,
    IEventBus bus, ILogger<UrlMediaProcessor> processorLogger,
    ILogger<IndeterminateMediaTaskRecoveryRequestedHandler> logger) : IEventHandler<IndeterminateMediaTaskRecoveryRequested>
{
    public async Task HandleAsync(IndeterminateMediaTaskRecoveryRequested request, IEventContext context)
    {
        var task = await store.GetAsync(request.TaskId, request.DispatchId, context.CancellationToken);
        if (task == null) return; // A completed, superseded, or explicitly blocked decision cannot be replayed.
        var decision = task.Decision;
        MediaGenerationTaskResult result;
        WebhookDeliveryRequested? webhook;
        var recoveredMedia = new List<MediaGenerationCompleted>();
        try
        {
            var command = MediaGenerationCommandReconstruction.Build(task.TaskId, task.TaskType, task.Metadata);
            var modelInfo = new GenerationModelInfo { ModelId = decision.ProviderModelId, ModelAlias = decision.Model,
                ProviderId = decision.ProviderId, Provider = new Provider { Id = decision.ProviderId,
                    ProviderName = task.Metadata.MediaProvider?.ProviderName ?? $"provider:{decision.ProviderId}" } };
            var processor = new UrlMediaProcessor(clients, storage, bus, processorLogger);
            var items = new List<MediaGenerationTaskResultItem>();
            for (var i = 0; i < decision.ResultUrls.Count; i++)
            {
                var item = await processor.ProcessAsync(new ConduitLLM.Core.Models.ImageData { Url = decision.ResultUrls[i] }, new MediaProcessingContext
                {
                    MediaType = task.TaskType == "image_generation" ? MediaType.Image : MediaType.Video,
                    Index = i, VirtualKeyId = decision.VirtualKeyId, RequestId = task.TaskId, RequireStoredMedia = true,
                    ModelInfo = modelInfo, Prompt = command.Image?.Request.Prompt ?? command.Video!.ResolveRequest().Prompt,
                    CorrelationId = task.Metadata.CorrelationId
                }, context.CancellationToken);
                items.Add(new() { Url = item.Url, Metadata = new() { ["storageKey"] = item.StorageKey!, ["recovered"] = true } });
                recoveredMedia.Add(new()
                {
                    VirtualKeyId = decision.VirtualKeyId, MediaUrl = item.Url!, StorageKey = item.StorageKey!,
                    MediaType = task.TaskType == "image_generation" ? MediaType.Image : MediaType.Video,
                    FileSizeBytes = (long)item.Metadata["fileSizeBytes"], ContentType = (string)item.Metadata["contentType"],
                    GeneratedByModel = decision.ProviderModelId, Provider = modelInfo.ProviderName,
                    GenerationPrompt = command.Image?.Request.Prompt ?? command.Video!.ResolveRequest().Prompt,
                    CorrelationId = task.Metadata.CorrelationId ?? task.TaskId
                });
            }
            result = new() { Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Model = decision.Model, Data = items,
                Usage = new() { Count = items.Count }, InternalMetadata = new() { Cost = 0, Provider = modelInfo.ProviderName } };
            var url = command.Image?.WebhookUrl ?? command.Video?.WebhookUrl;
            webhook = string.IsNullOrWhiteSpace(url) ? null : new()
            {
                TaskId = task.TaskId, TaskType = task.TaskType == "image_generation" ? "image" : "video",
                VirtualKeyId = decision.VirtualKeyId, EventType = WebhookEventType.TaskCompleted, WebhookUrl = url,
                Headers = command.Image?.WebhookHeaders ?? command.Video?.WebhookHeaders,
                CorrelationId = task.Metadata.CorrelationId ?? task.TaskId,
                PayloadJson = WebhookPayloadHelper.SerializePayload(command.Image is { } image ?
                    (object)new ImageCompletionWebhookPayload { TaskId = task.TaskId, Status = "completed", Model = decision.Model,
                        Prompt = image.Request.Prompt, ImageUrls = items.Select(x => x.Url!).ToList(), ImagesGenerated = items.Count,
                        ImagesRequested = image.Request.N, Size = image.Request.Size, ResponseFormat = "url" } :
                    new VideoCompletionWebhookPayload { TaskId = task.TaskId, Status = "completed", Model = decision.Model,
                        Prompt = command.Video!.ResolveRequest().Prompt, VideoUrl = items[0].Url }, CoreWebhookJsonContext.Default)
            };
        }
        catch (Exception) when (!context.CancellationToken.IsCancellationRequested)
        {
            await store.BlockAsync(task.TaskId, decision.DispatchId, context.CancellationToken);
            MediaTaskIdempotencyMetrics.RecordReconciliation(task.TaskType, "blocked");
            logger.LogWarning("Media recovery {DispatchId} for task {TaskId} is blocked; operator action is required", decision.DispatchId, task.TaskId);
            return;
        }
        // A failed/unconfirmed commit must be redelivered, not converted into a contradictory failure.
        await terminalWriter.CommitAsync(new(task.TaskId, TaskState.Completed, Progress: 100,
            ResultJson: JsonSerializer.Serialize(result, AsyncTaskJsonContext.Default.MediaGenerationTaskResult),
            Webhook: webhook, ReconciliationDispatchId: decision.DispatchId, RecoveredMedia: recoveredMedia), context.CancellationToken);
    }
}
