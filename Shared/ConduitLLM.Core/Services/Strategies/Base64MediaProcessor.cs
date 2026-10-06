using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services.Strategies
{
    /// <summary>
    /// Processes base64-encoded media data.
    /// </summary>
    public class Base64MediaProcessor : IMediaProcessingStrategy<IGeneratedMediaData>
    {
        private readonly IMediaStorageService _storageService;
        private readonly IEventBus _eventBus;
        private readonly ILogger<Base64MediaProcessor> _logger;

        public Base64MediaProcessor(
            IMediaStorageService storageService,
            IEventBus eventBus,
            ILogger<Base64MediaProcessor> logger)
        {
            _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<ProcessedMediaItem> ProcessAsync(
            IGeneratedMediaData mediaData,
            MediaProcessingContext context,
            CancellationToken cancellationToken)
        {
            // Extract base64 data from the media object
            string? base64Data = mediaData.B64Json;
            if (string.IsNullOrEmpty(base64Data))
            {
                throw new InvalidOperationException("No base64 data found in media object");
            }

            _logger.LogDebug("Processing base64 {MediaType} for task {RequestId}, index {Index}",
                context.MediaType, context.RequestId, context.Index);

            // Use streaming to decode base64 without loading entire content into memory
            using var base64Stream = new MemoryStream(Encoding.UTF8.GetBytes(base64Data));
            using var decodedStream = new CryptoStream(
                base64Stream,
                new FromBase64Transform(),
                CryptoStreamMode.Read);

            // Create media metadata
            var metadata = CreateMediaMetadata(context);

            // Store the decoded media
            var storageResult = context.MediaType == MediaType.Video
                ? await _storageService.StoreVideoAsync(decodedStream, metadata as VideoMediaMetadata ?? new VideoMediaMetadata(), null)
                : await _storageService.StoreAsync(decodedStream, metadata);

            // Publish media generation completed event
            await PublishMediaCompletedEvent(storageResult, context, metadata);

            _logger.LogInformation("Stored base64 {MediaType} to {Url} for task {RequestId}",
                context.MediaType, storageResult.Url, context.RequestId);

            return new ProcessedMediaItem
            {
                Url = storageResult.Url,
                StorageKey = storageResult.StorageKey,
                Index = context.Index,
                Metadata = new Dictionary<string, object>
                {
                    ["provider"] = context.ModelInfo?.ProviderName ?? "unknown",
                    ["model"] = context.ModelInfo?.ModelId ?? "unknown",
                    ["index"] = context.Index,
                    ["format"] = "b64_json"
                }
            };
        }

        private MediaMetadata CreateMediaMetadata(MediaProcessingContext context)
        {
            var extension = context.MediaType == MediaType.Video ? "mp4" : "png";
            var contentType = context.MediaType == MediaType.Video ? "video/mp4" : "image/png";

            var metadata = new MediaMetadata
            {
                ContentType = contentType,
                FileName = $"generated_{DateTime.UtcNow:yyyyMMddHHmmss}_{context.Index}.{extension}",
                MediaType = context.MediaType,
                CustomMetadata = new Dictionary<string, string>
                {
                    ["prompt"] = context.Prompt,
                    ["model"] = context.ModelInfo?.ModelId ?? "",
                    ["provider"] = context.ModelInfo?.ProviderName ?? ""
                }
            };

            // Add CreatedBy if we have virtual key info
            metadata.CreatedBy = context.CreatedBy ??
                (context.VirtualKeyId > 0 ? context.VirtualKeyId.ToString() : null);

            return metadata;
        }

        private async Task PublishMediaCompletedEvent(
            MediaStorageResult storageResult,
            MediaProcessingContext context,
            MediaMetadata metadata)
        {
            await _eventBus.PublishAsync(new MediaGenerationCompleted
            {
                MediaType = context.MediaType,
                VirtualKeyId = context.VirtualKeyId,
                MediaUrl = storageResult.Url,
                StorageKey = storageResult.StorageKey,
                FileSizeBytes = storageResult.SizeBytes,
                ContentType = metadata.ContentType,
                GeneratedByModel = context.ModelInfo?.ModelId ?? "",
                Provider = context.ModelInfo?.ProviderName ?? "",
                GenerationPrompt = context.Prompt,
                GeneratedAt = DateTime.UtcNow,
                Metadata = new Dictionary<string, object>
                {
                    ["provider"] = context.ModelInfo?.ProviderName ?? "",
                    ["model"] = context.ModelInfo?.ModelId ?? "",
                    ["index"] = context.Index,
                    ["format"] = "b64_json"
                },
                CorrelationId = context.CorrelationId ?? string.Empty
            });
        }
    }
}
