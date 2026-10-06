using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Metrics;
using ConduitLLM.Core.Models;
using IVirtualKeyService = ConduitLLM.Core.Interfaces.IVirtualKeyService;
using IModelProviderMappingService = ConduitLLM.Configuration.Interfaces.IModelProviderMappingService;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Services.Abstractions;
using ConduitLLM.Core.Services.Strategies;
using ConduitLLM.Core.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Image generation orchestrator using the generic base class.
    /// </summary>
    public class ImageGenerationOrchestrator : MediaGenerationOrchestrator<
        ConduitLLM.Core.Models.ImageGenerationRequest,
        ConduitLLM.Core.Models.ImageGenerationResponse,
        ImageGenerationRequested>,
        IEventHandler<ImageGenerationCancelled>
    {
        private readonly IMediaProcessingStrategy<ConduitLLM.Core.Models.ImageData> _base64Processor;
        private readonly IMediaProcessingStrategy<ConduitLLM.Core.Models.ImageData> _urlProcessor;

        // Implement abstract property accessors
        protected override string GetRequestId(ImageGenerationRequested request) => request.TaskId;
        protected override string GetModel(ImageGenerationRequested request) => request.Request.Model ?? "";
        protected override string GetPrompt(ImageGenerationRequested request) => request.Request.Prompt;
        protected override string GetVirtualKeyId(ImageGenerationRequested request) => request.VirtualKeyId.ToString();
        protected override string? GetWebhookUrl(ImageGenerationRequested request) => request.WebhookUrl;
        protected override string? GetCorrelationId(ImageGenerationRequested request) => request.CorrelationId;
        protected override bool GetIsAsync(ImageGenerationRequested request) => true; // Images are always async

        public ImageGenerationOrchestrator(
            ILLMClientFactory clientFactory,
            IAsyncTaskService taskService,
            IMediaStorageService storageService,
            IEventBus eventBus,
            IModelProviderMappingService modelMappingService,
            IVirtualKeyService virtualKeyService,
            ICostCalculationService costService,
            ICancellableTaskRegistry taskRegistry,
            IWebhookNotificationService webhookService,
            IHttpClientFactory httpClientFactory,
            MinimalParameterValidator parameterValidator,
            MediaGenerationMetrics metrics,
            IProviderErrorTrackingService errorTrackingService,
            ILogger<ImageGenerationOrchestrator> logger,
            ConduitLLM.Configuration.Interfaces.IBatchSpendUpdateService? batchSpendService = null,
            IProviderErrorTranslator? providerErrorTranslator = null)
            : base(clientFactory, taskService, storageService, eventBus,
                   modelMappingService, virtualKeyService, costService, taskRegistry,
                   webhookService, httpClientFactory, parameterValidator, metrics,
                   errorTrackingService, logger, batchSpendService, providerErrorTranslator)
        {

            // Initialize processing strategies
            _base64Processor = new Base64MediaProcessor(storageService, eventBus,
                logger as ILogger<Base64MediaProcessor> ?? new NullLogger<Base64MediaProcessor>());
            _urlProcessor = new UrlMediaProcessor(httpClientFactory, storageService, eventBus,
                logger as ILogger<UrlMediaProcessor> ?? new NullLogger<UrlMediaProcessor>());
        }


        protected override bool ShouldProcessRequest(ImageGenerationRequested request)
        {
            // Images are always processed asynchronously in our system
            return true;
        }

        protected override async Task<ConduitLLM.Core.Models.ImageGenerationResponse> ExecuteGenerationAsync(
            ConduitLLM.Core.Models.ImageGenerationRequest request,
            GenerationModelInfo modelInfo,
            VirtualKey virtualKey,
            CancellationToken cancellationToken)
        {
            // Get the client via the already-resolved provider — modelInfo.ModelId is the
            // provider's model id, not a model alias, so it must not be re-resolved by name
            var client = await _clientFactory.GetClientByProviderIdAsync(modelInfo.ProviderId, modelInfo.ModelId, cancellationToken);
            
            // Generate images
            return await client.CreateImageAsync(request, cancellationToken: cancellationToken);
        }

        protected override async Task<ProcessedMedia> ProcessMediaAsync(
            ImageGenerationResponse response,
            ImageGenerationRequested request,
            GenerationModelInfo modelInfo,
            VirtualKey virtualKey,
            CancellationToken cancellationToken)
        {
            var processedMedia = new ProcessedMedia();
            
            if (response.Data == null || !response.Data.Any())
            {
                return processedMedia;
            }

            // Process images in parallel
            var tasks = response.Data.Select(async (imageData, index) =>
            {
                var context = new MediaProcessingContext
                {
                    MediaType = MediaType.Image,
                    Index = index,
                    ModelInfo = modelInfo,
                    Prompt = request.Request.Prompt,
                    VirtualKeyId = virtualKey.Id,
                    RequestId = request.TaskId,
                    CorrelationId = request.CorrelationId
                };

                // Choose appropriate processor based on data format
                IMediaProcessingStrategy<ConduitLLM.Core.Models.ImageData> processor;
                if (!string.IsNullOrEmpty(imageData.B64Json))
                {
                    processor = _base64Processor;
                }
                else if (!string.IsNullOrEmpty(imageData.Url))
                {
                    processor = _urlProcessor;
                }
                else
                {
                    _logger.LogWarning("Image data has neither B64Json nor Url for index {Index}", index);
                    return null;
                }

                try
                {
                    // Safe cast since we know the processors handle ImageData
                    return await processor.ProcessAsync(imageData, context, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process image at index {Index}", index);
                    return null;
                }
            });

            var results = await Task.WhenAll(tasks);
            processedMedia.Items = results.Where(r => r != null).ToList()!;
            
            // Set primary URL to first image
            if (processedMedia.Items.Any())
            {
                processedMedia.Url = processedMedia.Items.First().Url;
            }

            return processedMedia;
        }

        protected override void ValidateParameters(ConduitLLM.Core.Models.ImageGenerationRequest request)
        {
            _parameterValidator.ValidateImageParameters(request);
        }

        protected override Task<ConduitLLM.Core.Models.ImageGenerationRequest> BuildGenerationRequestAsync(
            ImageGenerationRequested request,
            GenerationModelInfo modelInfo)
        {
            request.Request.Model = modelInfo.ModelId;
            request.Request.ResponseFormat ??= "url";
            return Task.FromResult(request.Request);
        }

        protected override void ValidateModelSupport(GenerationModelInfo modelInfo, ImageGenerationRequested request)
        {
            // Check if model supports image generation
            // This would typically check model capabilities
            // For now, we'll assume all models in image requests support images
        }

        protected override Usage CreateUsageObject(ImageGenerationRequested request, ImageGenerationResponse response)
        {
            var imageCount = response.Data?.Count ?? 0;

            // Build pricing parameters for rules-based pricing
            var pricingParameters = new Dictionary<string, object>
            {
                ["count"] = imageCount
            };

            // Add resolution/size if provided
            if (!string.IsNullOrEmpty(request.Request.Size))
            {
                pricingParameters["resolution"] = request.Request.Size;
                pricingParameters["image_resolution"] = request.Request.Size;
            }

            // Add quality if provided
            if (!string.IsNullOrEmpty(request.Request.Quality))
            {
                pricingParameters["quality"] = request.Request.Quality.ToLowerInvariant();
                pricingParameters["image_quality"] = request.Request.Quality.ToLowerInvariant();
            }

            // Add style if provided
            if (!string.IsNullOrEmpty(request.Request.Style))
            {
                pricingParameters["style"] = request.Request.Style.ToLowerInvariant();
            }

            return new Usage
            {
                ImageCount = imageCount,
                ImageResolution = request.Request.Size,
                ImageQuality = request.Request.Quality,
                PricingParameters = pricingParameters
            };
        }

        protected override Usage CreateEstimatedUsageObject(ImageGenerationRequested request)
        {
            var imageCount = Math.Max(1, request.Request.N);
            var pricingParameters = new Dictionary<string, object>
            {
                ["count"] = imageCount
            };

            if (!string.IsNullOrEmpty(request.Request.Size))
            {
                pricingParameters["resolution"] = request.Request.Size;
                pricingParameters["image_resolution"] = request.Request.Size;
            }
            if (!string.IsNullOrEmpty(request.Request.Quality))
            {
                pricingParameters["quality"] = request.Request.Quality.ToLowerInvariant();
                pricingParameters["image_quality"] = request.Request.Quality.ToLowerInvariant();
            }
            if (!string.IsNullOrEmpty(request.Request.Style))
            {
                pricingParameters["style"] = request.Request.Style.ToLowerInvariant();
            }

            return new Usage
            {
                ImageCount = imageCount,
                ImageResolution = request.Request.Size,
                ImageQuality = request.Request.Quality,
                PricingParameters = pricingParameters
            };
        }

        protected override async Task PublishStartedEventAsync(ImageGenerationRequested request)
        {
            await _eventBus.PublishAsync(new ImageGenerationProgress
            {
                TaskId = request.TaskId,
                Status = "processing",
                ImagesCompleted = 0,
                TotalImages = request.Request.N,
                CorrelationId = request.CorrelationId
            });
        }

        protected override async Task PublishCompletedEventAsync(
            ImageGenerationRequested request,
            ProcessedMedia media,
            decimal cost,
            GenerationModelInfo modelInfo,
            TimeSpan duration)
        {
            var imageData = media.Items.Select(item => new ConduitLLM.Core.Models.ImageData
            {
                Url = item.Url
            }).ToList();

            await _eventBus.PublishAsync(new ImageGenerationCompleted
            {
                TaskId = request.TaskId,
                VirtualKeyId = request.VirtualKeyId,
                Images = imageData.Select(d => new ConduitLLM.Core.Events.ImageData
                {
                    Url = d.Url,
                    Metadata = new System.Collections.Generic.Dictionary<string, object>()
                }).ToList(),
                Duration = duration,
                Cost = cost,
                Provider = modelInfo.ProviderName,
                Model = modelInfo.ModelId,
                CorrelationId = request.CorrelationId
            });
        }

        protected override async Task PublishFailedEventAsync(
            ImageGenerationRequested request,
            CustomerFacingProviderError customerError,
            bool isRetryable,
            int retryCount,
            int maxRetries)
        {
            await _eventBus.PublishAsync(new ImageGenerationFailed
            {
                TaskId = request.TaskId,
                VirtualKeyId = request.VirtualKeyId,
                Error = customerError.Message,
                ErrorCode = customerError.ErrorCode,
                Provider = request.Request.Model ?? "unknown",
                IsRetryable = isRetryable,
                AttemptCount = retryCount + 1,
                CorrelationId = request.CorrelationId
            });
        }

        protected override async Task PublishProgressEventAsync(
            ImageGenerationRequested request,
            int current,
            int total,
            string status)
        {
            await _eventBus.PublishAsync(new ImageGenerationProgress
            {
                TaskId = request.TaskId,
                Status = status,
                ImagesCompleted = current,
                TotalImages = total,
                Message = $"Processed {current} of {total} images",
                CorrelationId = request.CorrelationId
            });
        }

        protected override object CreateWebhookPayload(
            ImageGenerationRequested request,
            ProcessedMedia media,
            TimeSpan duration,
            string status,
            string? error = null)
        {
            var imageUrls = media.Items
                .Where(item => !string.IsNullOrEmpty(item.Url))
                .Select(item => item.Url)
                .ToList();

            return new ImageCompletionWebhookPayload
            {
                TaskId = request.TaskId,
                Status = status,
                ImageUrls = imageUrls,
                ImagesGenerated = media.Count,
                ImagesRequested = request.Request.N,
                GenerationDurationSeconds = duration.TotalSeconds,
                Model = request.Request.Model ?? "",
                Prompt = request.Request.Prompt,
                Size = request.Request.Size,
                ResponseFormat = request.Request.ResponseFormat ?? "url",
                Error = error
            };
        }

        protected override string GetMediaType() => "Image";

        protected override void LogGenerationDetails(
            ImageGenerationRequested request,
            GenerationModelInfo modelInfo,
            ConduitLLM.Core.Models.ImageGenerationRequest generationRequest)
        {
            _logger.LogInformation(
                "Image generation request prepared: TaskId={TaskId}, Model={Model}, Provider={Provider}, " +
                "Count={Count}, Size={Size}, Quality={Quality}, Style={Style}, PromptLength={PromptLength}",
                request.TaskId,
                request.Request.Model ?? modelInfo.ModelId,
                modelInfo.ProviderName,
                generationRequest.N,
                generationRequest.Size,
                generationRequest.Quality,
                generationRequest.Style,
                request.Request.Prompt?.Length ?? 0);
        }

        /// <summary>
        /// Handles image generation cancellation events.
        /// </summary>
        public async Task HandleAsync(ImageGenerationCancelled cancellationEvent, IEventContext context)
        {
            _logger.LogInformation("Received cancellation request for image generation task {TaskId}",
                cancellationEvent.TaskId);

            // Cancel the task using the task registry
            var cancelled = _taskRegistry.TryCancel(cancellationEvent.TaskId);
            
            if (cancelled)
            {
                _logger.LogInformation("Successfully cancelled image generation task {TaskId}", 
                    cancellationEvent.TaskId);
                
                // Update task status to cancelled
                await _taskService.UpdateTaskStatusAsync(
                    cancellationEvent.TaskId,
                    TaskState.Cancelled,
                    error: "Task cancelled by user request");
                
                // Publish cancellation completed event
                await _eventBus.PublishAsync(new ImageGenerationProgress
                {
                    TaskId = cancellationEvent.TaskId,
                    Status = "cancelled",
                    Message = "Task cancelled by user request",
                    ImagesCompleted = 0,
                    TotalImages = 0,
                    CorrelationId = cancellationEvent.CorrelationId ?? Guid.NewGuid().ToString()
                });
            }
            else
            {
                _logger.LogWarning("Could not cancel image generation task {TaskId} - task may have already completed or was not found", 
                    cancellationEvent.TaskId);
            }
        }
    }

}
