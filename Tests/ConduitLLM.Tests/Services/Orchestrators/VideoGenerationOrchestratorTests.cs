using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ConduitLLM.Tests.Services.Orchestrators
{
    /// <summary>
    /// Unit tests for VideoGenerationOrchestrator.
    /// </summary>
    // Test client implementing the explicit video-generation capability.
    public class TestVideoClient : ILLMClient, IVideoGenerationClient
    {
        private readonly VideoGenerationResponse _response;
        
        public TestVideoClient(VideoGenerationResponse response)
        {
            _response = response;
        }
        
        public Task<VideoGenerationResponse> CreateVideoAsync(
            VideoGenerationRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_response);
        }
        
        // ILLMClient interface methods (minimal implementation)
        public Task<ChatCompletionResponse> CreateChatCompletionAsync(ChatCompletionRequest request, string? apiKey = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<ConduitLLM.Core.Models.ImageGenerationResponse> CreateImageAsync(ConduitLLM.Core.Models.ImageGenerationRequest request, string? apiKey = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<EmbeddingResponse> CreateEmbeddingAsync(EmbeddingRequest request, string? apiKey = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public IAsyncEnumerable<ChatCompletionChunk> StreamChatCompletionAsync(ChatCompletionRequest request, string? apiKey = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<List<string>> ListModelsAsync(string? apiKey = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<string>());
        public void Dispose() { }
    }
    
    public class VideoGenerationOrchestratorTests : MediaGenerationOrchestratorTestBase<
        VideoGenerationOrchestrator,
        VideoGenerationRequest,
        VideoGenerationResponse,
        VideoGenerationRequested>
    {
        private readonly Mock<IOptions<VideoGenerationRetryConfiguration>> _retryConfigMock;
        private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;

        protected override string GetRequestId(VideoGenerationRequested request) => request.RequestId;
        protected override string? GetWebhookUrl(VideoGenerationRequested request) => request.WebhookUrl;

        public VideoGenerationOrchestratorTests()
        {
            _retryConfigMock = new Mock<IOptions<VideoGenerationRetryConfiguration>>();
            _retryConfigMock.Setup(x => x.Value)
                .Returns(new VideoGenerationRetryConfiguration
                {
                    EnableRetries = true,
                    MaxRetries = 3,
                    BaseDelaySeconds = 1,
                    MaxDelaySeconds = 30
                });

            _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        }

        protected override VideoGenerationOrchestrator CreateOrchestrator()
        {
            return new VideoGenerationOrchestrator(
                ClientFactoryMock.Object,
                TaskServiceMock.Object,
                StorageServiceMock.Object,
                EventBusMock.Object,
                ModelMappingServiceMock.Object,
                VirtualKeyServiceMock.Object,
                CostServiceMock.Object,
                TaskRegistryMock.Object,
                WebhookServiceMock.Object,
                _retryConfigMock.Object,
                HttpClientFactoryMock.Object,
                ParameterValidatorMock.Object,
                Metrics,
                ErrorTrackingServiceMock.Object,
                LoggerMock.Object as ILogger<VideoGenerationOrchestrator> ?? new Mock<ILogger<VideoGenerationOrchestrator>>().Object);
        }

        protected override VideoGenerationRequested CreateTestEventRequest()
        {
            return new VideoGenerationRequested
            {
                RequestId = "test-task-id",
                Request = new VideoGenerationRequest
                {
                    Model = "test-model",
                    Prompt = "Generate a test video",
                    Duration = 5,
                    Size = "1280x720",
                    Fps = 30
                },
                VirtualKeyId = "1",  // Must be a valid integer string for parsing
                IsAsync = true,
                WebhookUrl = "https://example.com/webhook",
                CorrelationId = "test-correlation-id"
            };
        }

        protected override VideoGenerationResponse CreateTestResponse()
        {
            return new VideoGenerationResponse
            {
                Data = new List<VideoData>
                {
                    new VideoData
                    {
                        Url = "https://example.com/video.mp4",
                        RevisedPrompt = "Generated test video"
                    }
                }
            };
        }

        protected override void SetupSuccessfulGeneration(VideoGenerationResponse response)
        {
            // Use a test client with the explicit video-generation capability.
            var testClient = new TestVideoClient(response);
            
            ClientFactoryMock.Setup(x => x.GetClientByProviderIdAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(testClient);
            
            StorageServiceMock.Setup(x => x.StoreAsync(
                It.IsAny<Stream>(),
                It.IsAny<MediaMetadata>(),
                It.IsAny<IProgress<long>>()))
                .ReturnsAsync(new MediaStorageResult
                {
                    StorageKey = "video-key",
                    Url = "https://storage.example.com/video.mp4",
                    SizeBytes = 1024
                });
        }

        protected override void SetupFailedGeneration(Exception exception)
        {
            // Setup to simulate failure during orchestration
            ClientFactoryMock.Setup(x => x.GetClientByProviderIdAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(exception);
        }

        [Fact]
        public async Task HandleAsync_WhenSyncRequest_ShouldNotProcess()
        {
            // Arrange
            var request = new VideoGenerationRequested
            {
                RequestId = "sync-task-id",
                Request = new VideoGenerationRequest
                {
                    Model = "test-model",
                    Prompt = "Test prompt"
                },
                VirtualKeyId = "1",  // Must be a valid integer string for parsing
                IsAsync = false // Sync request
            };
            var context = CreateEventContext();

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert - Should not process sync requests
            TaskServiceMock.Verify(x => x.UpdateTaskStatusAsync(
                It.IsAny<string>(),
                It.IsAny<TaskState>(),
                It.IsAny<int?>(),
                It.IsAny<object?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }


        [Fact]
        public async Task HandleAsync_WithRetryConfiguration_ShouldRespectSettings()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var exception = new TimeoutException("Video generation timed out");

            SetupFailedGeneration(exception);

            // Act - Should handle timeout gracefully
            await Orchestrator.HandleAsync(request, context);

            // Assert - a timeout after invocation has an unknown provider outcome
            TaskServiceMock.Verify(x => x.UpdateTaskStatusAsync(
                request.RequestId,
                TaskState.Indeterminate,
                It.IsAny<int?>(),
                It.IsAny<object?>(),
                It.Is<string?>(message => message != null && message.Contains(exception.Message)),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task PublishStartedEvent_ShouldIncludeEstimatedTime()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var response = CreateTestResponse();

            SetupSuccessfulGeneration(response);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert
            EventBusMock.Verify(x => x.PublishAsync(
                It.Is<VideoGenerationStarted>(e =>
                    e.RequestId == request.RequestId &&
                    e.EstimatedSeconds == 60 &&
                    e.CorrelationId == request.CorrelationId),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task PublishCompletedEvent_ShouldIncludeVideoUrl()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var response = CreateTestResponse();

            SetupSuccessfulGeneration(response);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert
            EventBusMock.Verify(x => x.PublishAsync(
                It.Is<VideoGenerationCompleted>(e =>
                    e.RequestId == request.RequestId &&
                    !string.IsNullOrEmpty(e.VideoUrl) &&
                    e.CorrelationId == request.CorrelationId),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task PublishCompletedEvent_ShouldIncludeAllBillingFields()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var response = CreateTestResponse();

            // Setup specific cost for verification
            CostServiceMock.Setup(x => x.CalculateCostAsync(
                It.IsAny<string>(),
                It.IsAny<Usage>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(0.34m);

            SetupSuccessfulGeneration(response);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert - Verify all billing-critical fields are populated
            EventBusMock.Verify(x => x.PublishAsync(
                It.Is<VideoGenerationCompleted>(e =>
                    e.RequestId == request.RequestId &&
                    e.Cost == 0.34m &&
                    e.Model == request.Request!.Model &&
                    e.Duration == 5 &&
                    e.Resolution == "1280x720" &&
                    e.Provider == "Test Provider" && // From ModelMappingService mock
                    e.GenerationDuration > TimeSpan.Zero),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ProviderReportedMetadata_ShouldDriveBillingAndCompletionEvent()
        {
            // Arrange - the provider delivers a 6-second 1080p video despite omitted parameters
            var request = new VideoGenerationRequested
            {
                RequestId = "test-task-id",
                Request = new VideoGenerationRequest
                {
                    Model = "test-model",
                    Prompt = "Generate a test video"
                },
                VirtualKeyId = "1",
                IsAsync = true,
                CorrelationId = "test-correlation-id"
            };
            var context = CreateEventContext();
            var response = CreateTestResponse();
            response.Data[0].Metadata = new VideoMetadata
            {
                Duration = 6,
                Width = 1920,
                Height = 1080
            };

            SetupSuccessfulGeneration(response);

            // Act
            await Orchestrator.HandleAsync(request, context);

            CostServiceMock.Verify(x => x.CalculateCostAsync(
                It.IsAny<string>(),
                It.Is<Usage>(usage =>
                    usage.VideoDurationSeconds == 6 &&
                    usage.VideoResolution == "1920x1080" &&
                    (double)usage.PricingParameters!["duration"] == 6 &&
                    (string)usage.PricingParameters["resolution"] == "1080p"),
                It.IsAny<CancellationToken>()), Times.Once);

            EventBusMock.Verify(x => x.PublishAsync(
                It.Is<VideoGenerationCompleted>(e =>
                    e.Duration == 6 &&
                    e.Resolution == "1920x1080"),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task PricingFailure_AfterMediaDelivery_CompletesTaskForReconciliation()
        {
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var response = CreateTestResponse();

            CostServiceMock.Setup(x => x.CalculateCostAsync(
                    It.IsAny<string>(), It.IsAny<Usage>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Malformed pricing configuration"));
            CostServiceMock.Setup(x => x.CalculateCostByIdAsync(
                    It.IsAny<int>(), It.IsAny<Usage>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Malformed pricing configuration"));
            SetupSuccessfulGeneration(response);

            await Orchestrator.HandleAsync(request, context);

            EventBusMock.Verify(x => x.PublishAsync(
                It.Is<VideoGenerationCompleted>(e => e.RequestId == request.RequestId && e.Cost == 0m),
                It.IsAny<CancellationToken>()), Times.Once);
            EventBusMock.Verify(x => x.PublishAsync(
                It.IsAny<VideoGenerationFailed>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ProviderTimeout_ShouldNotPublishRetryableFailure()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var exception = new TimeoutException("Temporary failure");

            SetupFailedGeneration(exception);

            // Act - Should handle failure gracefully
            await Orchestrator.HandleAsync(request, context);

            // Assert - retrying an unknown provider outcome could duplicate generation
            EventBusMock.Verify(x => x.PublishAsync(
                It.IsAny<VideoGenerationFailed>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
