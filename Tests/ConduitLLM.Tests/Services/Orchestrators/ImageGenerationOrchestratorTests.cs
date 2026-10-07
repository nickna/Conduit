using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.Logging;
using ConduitLLM.Configuration.Interfaces;
using Moq;
using Xunit;

namespace ConduitLLM.Tests.Services.Orchestrators
{
    /// <summary>
    /// Unit tests for ImageGenerationOrchestrator.
    /// </summary>
    public class ImageGenerationOrchestratorTests : MediaGenerationOrchestratorTestBase<
        ImageGenerationOrchestrator,
        ConduitLLM.Core.Models.ImageGenerationRequest,
        ConduitLLM.Core.Models.ImageGenerationResponse,
        ImageGenerationRequested>
    {
        private Mock<ILLMClient>? _imageClientMock;

        protected override string GetRequestId(ImageGenerationRequested request) => request.TaskId;
        protected override string? GetWebhookUrl(ImageGenerationRequested request) => request.WebhookUrl;

        public ImageGenerationOrchestratorTests()
        {
        }

        protected override ImageGenerationOrchestrator CreateOrchestrator()
        {
            return new ImageGenerationOrchestrator(
                ClientFactoryMock.Object,
                TaskServiceMock.Object,
                StorageServiceMock.Object,
                EventBusMock.Object,
                ModelMappingServiceMock.Object,
                VirtualKeyServiceMock.Object,
                CostServiceMock.Object,
                TaskRegistryMock.Object,
                WebhookServiceMock.Object,
                HttpClientFactoryMock.Object,
                ParameterValidatorMock.Object,
                Metrics,
                ErrorTrackingServiceMock.Object,
                LoggerMock.Object as ILogger<ImageGenerationOrchestrator> ?? new Mock<ILogger<ImageGenerationOrchestrator>>().Object);
        }

        protected override ImageGenerationRequested CreateTestEventRequest()
        {
            return new ImageGenerationRequested
            {
                TaskId = "test-task-id",
                VirtualKeyId = 1,
                Request = new ConduitLLM.Core.Models.ImageGenerationRequest
                {
                    Model = "test-model",
                    Prompt = "Generate a test image",
                    N = 2,
                    Size = "1024x1024",
                    Quality = "standard",
                    Style = "vivid",
                    ResponseFormat = "url"
                },
                WebhookUrl = "https://example.com/webhook",
                WebhookHeaders = new() { ["Authorization"] = "Bearer callback-test" },
                CorrelationId = "test-correlation-id"
            };
        }

        protected override ConduitLLM.Core.Models.ImageGenerationResponse CreateTestResponse()
        {
            return new ConduitLLM.Core.Models.ImageGenerationResponse
            {
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Data = new List<ConduitLLM.Core.Models.ImageData>
                {
                    new ConduitLLM.Core.Models.ImageData
                    {
                        Url = "https://example.com/image1.png"
                    },
                    new ConduitLLM.Core.Models.ImageData
                    {
                        Url = "https://example.com/image2.png"
                    }
                }
            };
        }

        protected override void SetupSuccessfulGeneration(ConduitLLM.Core.Models.ImageGenerationResponse response)
        {
            // Mock client for image generation
            _imageClientMock = new Mock<ILLMClient>();
            _imageClientMock.Setup(x => x.CreateImageAsync(
                It.IsAny<ConduitLLM.Core.Models.ImageGenerationRequest>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(response);
            
            ClientFactoryMock.Setup(x => x.GetClientByProviderIdAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(_imageClientMock.Object);
            
            StorageServiceMock.Setup(x => x.StoreAsync(
                It.IsAny<Stream>(),
                It.IsAny<MediaMetadata>(),
                It.IsAny<IProgress<long>>()))
                .ReturnsAsync(new MediaStorageResult
                {
                    StorageKey = "image-key",
                    Url = "https://storage.example.com/image.png",
                    SizeBytes = 1024
                });
        }

        [Fact]
        public async Task HandleAsync_PreservesImageEditAndNewRequestFields()
        {
            var request = CreateTestEventRequest();
            request.Request.Image = "base64-image";
            request.Request.Mask = "base64-mask";
            request.Request.Operation = "edit";
            request.Request.Background = "transparent";
            request.Request.OutputFormat = "webp";
            var response = CreateTestResponse();
            SetupSuccessfulGeneration(response);

            await Orchestrator.HandleAsync(request, CreateEventContext());

            _imageClientMock!.Verify(client => client.CreateImageAsync(
                It.Is<ConduitLLM.Core.Models.ImageGenerationRequest>(providerRequest =>
                    providerRequest.Image == "base64-image" &&
                    providerRequest.Mask == "base64-mask" &&
                    providerRequest.Operation == "edit" &&
                    providerRequest.Background == "transparent" &&
                    providerRequest.OutputFormat == "webp"),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        protected override void SetupFailedGeneration(Exception exception)
        {
            // Setup to simulate failure during orchestration
            ClientFactoryMock.Setup(x => x.GetClientByProviderIdAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(exception);
        }

        [Fact]
        public async Task HandleAsync_WithMultipleImages_ShouldProcessAllInParallel()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var response = CreateTestResponse();

            SetupSuccessfulGeneration(response);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert - Should publish progress event with correct counts
            EventBusMock.Verify(x => x.PublishAsync(
                It.Is<ImageGenerationProgress>(e =>
                    e.TaskId == request.TaskId &&
                    e.TotalImages == 2),
                It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }

        [Fact]
        public async Task HandleAsync_WithBase64Response_ShouldProcessCorrectly()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            
            var response = new ConduitLLM.Core.Models.ImageGenerationResponse
            {
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Data = new List<ConduitLLM.Core.Models.ImageData>
                {
                    new ConduitLLM.Core.Models.ImageData
                    {
                        B64Json = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="
                    }
                }
            };

            SetupSuccessfulGeneration(response);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert
            TaskServiceMock.Verify(x => x.UpdateTaskStatusAsync(
                request.TaskId,
                TaskState.Completed,
                It.IsAny<int?>(),
                It.IsAny<object?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task HandleAsync_WithUrlResponse_ShouldDownloadAndStore()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var response = CreateTestResponse();

            SetupSuccessfulGeneration(response);

            // Setup HTTP client for downloading
            var httpClient = new System.Net.Http.HttpClient(new MockHttpMessageHandler());
            HttpClientFactoryMock.Setup(x => x.CreateClient(It.IsAny<string>()))
                .Returns(httpClient);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert
            TaskServiceMock.Verify(x => x.UpdateTaskStatusAsync(
                request.TaskId,
                TaskState.Completed,
                It.IsAny<int?>(),
                It.IsAny<object?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task PublishCompletedEvent_ShouldIncludeAllImageUrls()
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
                It.Is<ImageGenerationCompleted>(e =>
                    e.TaskId == request.TaskId &&
                    e.Images.Count == 2 &&
                    e.CorrelationId == request.CorrelationId),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task PublishProgressEvent_ShouldTrackImageCompletion()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var response = CreateTestResponse();

            SetupSuccessfulGeneration(response);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert - Should publish initial progress
            EventBusMock.Verify(x => x.PublishAsync(
                It.Is<ImageGenerationProgress>(e =>
                    e.TaskId == request.TaskId &&
                    e.Status == "processing" &&
                    e.ImagesCompleted == 0 &&
                    e.TotalImages == 2),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task CreateWebhookPayload_ShouldIncludeAllImageDetails()
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
                It.Is<WebhookDeliveryRequested>(w =>
                    w.TaskId == request.TaskId &&
                    w.WebhookUrl == request.WebhookUrl),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task HandleAsync_WithPartialImageFailure_ShouldProcessSuccessfulOnes()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            
            // Create response with one valid URL and one invalid
            var response = new ConduitLLM.Core.Models.ImageGenerationResponse
            {
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Data = new List<ConduitLLM.Core.Models.ImageData>
                {
                    new ConduitLLM.Core.Models.ImageData
                    {
                        Url = "https://example.com/image1.png"
                    },
                    new ConduitLLM.Core.Models.ImageData
                    {
                        // Neither URL nor B64Json - should be skipped
                    }
                }
            };

            SetupSuccessfulGeneration(response);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert - Should complete successfully with partial results
            TaskServiceMock.Verify(x => x.UpdateTaskStatusAsync(
                request.TaskId,
                TaskState.Completed,
                It.IsAny<int?>(),
                It.IsAny<object?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task HandleAsync_WhenAllImageStorageFails_ShouldBillProviderGeneratedImages()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var response = new ConduitLLM.Core.Models.ImageGenerationResponse
            {
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Data = Enumerable.Range(0, 2)
                    .Select(_ => new ConduitLLM.Core.Models.ImageData
                    {
                        B64Json = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="
                    })
                    .ToList()
            };
            SetupSuccessfulGeneration(response);

            StorageServiceMock.Setup(x => x.StoreAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<MediaMetadata>(),
                    It.IsAny<IProgress<long>>()))
                .ThrowsAsync(new InvalidOperationException("Storage unavailable"));

            // Act
            await Orchestrator.HandleAsync(request, CreateEventContext());

            // Assert: storage failures do not change the provider-generated usage or spend.
            CostServiceMock.Verify(x => x.CalculateCostAsync(
                "provider-model-id",
                It.Is<Usage>(usage => usage.ImageCount == 2),
                It.IsAny<CancellationToken>()), Times.Once);
            EventBusMock.Verify(x => x.PublishAsync(
                It.Is<SpendUpdateRequested>(spend => spend.KeyId == 1 && spend.Amount == 0.01m),
                It.IsAny<CancellationToken>()), Times.Once);
            EventBusMock.Verify(x => x.PublishAsync(
                It.Is<ImageGenerationCompleted>(completed =>
                    completed.TaskId == request.TaskId &&
                    completed.Images.Count == 0 &&
                    completed.Cost == 0.01m),
                It.IsAny<CancellationToken>()), Times.Once);
            TaskServiceMock.Verify(x => x.UpdateTaskStatusAsync(
                request.TaskId,
                TaskState.Completed,
                It.IsAny<int?>(),
                It.IsAny<object?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task HandleAsync_WhenEstimatedCostCannotBeReserved_ShouldNotCallProvider()
        {
            // Arrange
            var batchSpendService = new Mock<IBatchSpendUpdateService>();
            batchSpendService
                .Setup(x => x.TryReserveSpendAsync(1, 0.01m, "test-task-id"))
                .ReturnsAsync(false);

            var orchestrator = new ImageGenerationOrchestrator(
                ClientFactoryMock.Object,
                TaskServiceMock.Object,
                StorageServiceMock.Object,
                EventBusMock.Object,
                ModelMappingServiceMock.Object,
                VirtualKeyServiceMock.Object,
                CostServiceMock.Object,
                TaskRegistryMock.Object,
                WebhookServiceMock.Object,
                HttpClientFactoryMock.Object,
                ParameterValidatorMock.Object,
                Metrics,
                ErrorTrackingServiceMock.Object,
                LoggerMock.Object as ILogger<ImageGenerationOrchestrator>
                    ?? new Mock<ILogger<ImageGenerationOrchestrator>>().Object,
                batchSpendService.Object);

            // Act
            await orchestrator.HandleAsync(CreateTestEventRequest(), CreateEventContext());

            // Assert
            batchSpendService.Verify(
                x => x.TryReserveSpendAsync(1, 0.01m, "test-task-id"),
                Times.Once);
            ClientFactoryMock.Verify(
                x => x.GetClientByProviderIdAsync(
                    It.IsAny<int>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task HandleAsync_WhenCancelledAfterProviderCompletes_ShouldStillPublishSpend()
        {
            // Arrange: cancel as soon as billing is published, before media download/storage starts.
            using var cancellationSource = new CancellationTokenSource();
            var request = CreateTestEventRequest();
            var response = CreateTestResponse();
            SetupSuccessfulGeneration(response);

            HttpClientFactoryMock.Setup(x => x.CreateClient(It.IsAny<string>()))
                .Returns(new System.Net.Http.HttpClient(new MockHttpMessageHandler()));
            EventBusMock.Setup(x => x.PublishAsync(
                    It.IsAny<SpendUpdateRequested>(),
                    It.IsAny<CancellationToken>()))
                .Callback(() => cancellationSource.Cancel())
                .Returns(Task.CompletedTask);

            // Act
            await Orchestrator.HandleAsync(request, CreateEventContext(cancellationSource.Token));

            // Assert: provider output is billed even though subsequent processing is cancelled.
            CostServiceMock.Verify(x => x.CalculateCostAsync(
                "provider-model-id",
                It.Is<Usage>(usage => usage.ImageCount == 2),
                It.IsAny<CancellationToken>()), Times.Once);
            EventBusMock.Verify(x => x.PublishAsync(
                It.Is<SpendUpdateRequested>(spend => spend.KeyId == 1 && spend.Amount == 0.01m),
                It.IsAny<CancellationToken>()), Times.Once);
            TaskServiceMock.Verify(x => x.UpdateTaskStatusAsync(
                request.TaskId,
                TaskState.Cancelled,
                It.IsAny<int?>(),
                It.IsAny<object?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        private class MockHttpMessageHandler : System.Net.Http.HttpMessageHandler
        {
            protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(
                System.Net.Http.HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromCanceled<System.Net.Http.HttpResponseMessage>(cancellationToken);
                }

                var response = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.ByteArrayContent(new byte[] { 0x89, 0x50, 0x4E, 0x47 })
                };
                return Task.FromResult(response);
            }
        }
    }
}
