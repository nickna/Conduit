using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using IVirtualKeyService = ConduitLLM.Core.Interfaces.IVirtualKeyService;
using IModelProviderMappingService = ConduitLLM.Configuration.Interfaces.IModelProviderMappingService;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Metrics;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services.Abstractions;
using ConduitLLM.Core.Validation;
using ConduitLLM.Tests.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ConduitLLM.Tests.Services.Orchestrators
{
    /// <summary>
    /// Base test class for all media generation orchestrators.
    /// Provides common test cases and helper methods.
    /// </summary>
    public abstract class MediaGenerationOrchestratorTestBase<TOrchestrator, TRequest, TResponse, TEventRequest>
        where TOrchestrator : MediaGenerationOrchestrator<TRequest, TResponse, TEventRequest>
        where TRequest : class
        where TResponse : class
        where TEventRequest : class
    {
        // Mocked dependencies
        protected readonly Mock<ILLMClientFactory> ClientFactoryMock;
        protected readonly Mock<IAsyncTaskService> TaskServiceMock;
        protected readonly Mock<IMediaStorageService> StorageServiceMock;
        protected readonly Mock<IEventBus> EventBusMock;
        protected readonly Mock<IModelProviderMappingService> ModelMappingServiceMock;
        protected readonly Mock<IVirtualKeyService> VirtualKeyServiceMock;
        protected readonly Mock<ICostCalculationService> CostServiceMock;
        protected readonly Mock<ICancellableTaskRegistry> TaskRegistryMock;
        protected readonly Mock<IWebhookNotificationService> WebhookServiceMock;
        protected readonly Mock<IHttpClientFactory> HttpClientFactoryMock;
        protected readonly Mock<MinimalParameterValidator> ParameterValidatorMock;
        protected readonly MediaGenerationMetrics Metrics;
        protected readonly Mock<IProviderErrorTrackingService> ErrorTrackingServiceMock;
        protected readonly Mock<ILogger> LoggerMock;

        // System under test
        private TOrchestrator? _orchestrator;
        protected TOrchestrator Orchestrator 
        { 
            get 
            { 
                if (_orchestrator == null)
                {
                    _orchestrator = CreateOrchestrator();
                }
                return _orchestrator;
            }
        }

        // Abstract methods to get properties from event
        protected abstract string GetRequestId(TEventRequest request);
        protected abstract string? GetWebhookUrl(TEventRequest request);

        protected MediaGenerationOrchestratorTestBase()
        {
            // Initialize mocks
            ClientFactoryMock = new Mock<ILLMClientFactory>();
            TaskServiceMock = new Mock<IAsyncTaskService>();
            StorageServiceMock = new Mock<IMediaStorageService>();
            EventBusMock = new Mock<IEventBus>();
            ModelMappingServiceMock = new Mock<IModelProviderMappingService>();
            VirtualKeyServiceMock = new Mock<IVirtualKeyService>();
            CostServiceMock = new Mock<ICostCalculationService>();
            TaskRegistryMock = new Mock<ICancellableTaskRegistry>();
            WebhookServiceMock = new Mock<IWebhookNotificationService>();
            HttpClientFactoryMock = new Mock<IHttpClientFactory>();
            ErrorTrackingServiceMock = new Mock<IProviderErrorTrackingService>();
            LoggerMock = new Mock<ILogger>();
            
            // MinimalParameterValidator requires a logger in its constructor
            var validatorLoggerMock = new Mock<ILogger<MinimalParameterValidator>>();
            ParameterValidatorMock = new Mock<MinimalParameterValidator>(validatorLoggerMock.Object);
            
            // Create real metrics instance for tests - MediaGenerationMetrics needs a real IMeterFactory
            var meterFactory = new TestMeterFactory();
            Metrics = new MediaGenerationMetrics(meterFactory);

            // Setup default behaviors
            SetupDefaultMocks();

            // Note: Orchestrator is created lazily on first access to allow derived class constructors to run first
        }

        /// <summary>
        /// Creates the specific orchestrator instance to test.
        /// Must be implemented by derived test classes.
        /// </summary>
        protected abstract TOrchestrator CreateOrchestrator();

        /// <summary>
        /// Creates a test event request.
        /// Must be implemented by derived test classes.
        /// </summary>
        protected abstract TEventRequest CreateTestEventRequest();

        /// <summary>
        /// Creates a test response.
        /// Must be implemented by derived test classes.
        /// </summary>
        protected abstract TResponse CreateTestResponse();

        /// <summary>
        /// Sets up default mock behaviors.
        /// </summary>
        protected virtual void SetupDefaultMocks()
        {
            // Setup task service
            var taskMetadata = new TaskMetadata
            {
                VirtualKeyId = 1,
                ExtensionData = new Dictionary<string, object>
                {
                    ["VirtualKey"] = "test-virtual-key"
                }
            };

            TaskServiceMock.Setup(x => x.GetTaskStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AsyncTaskStatus
                {
                    TaskId = "test-task-id",
                    State = TaskState.Pending,
                    Metadata = taskMetadata
                });

            TaskServiceMock.Setup(x => x.TryClaimTaskAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AsyncTaskClaimResult.Claimed);
            TaskServiceMock.Setup(x => x.MarkProviderInvocationStartedAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            TaskServiceMock.Setup(x => x.ExtendTaskLeaseAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            TaskServiceMock.Setup(x => x.MarkProviderInvocationCompletedAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            TaskServiceMock.Setup(x => x.UpdateTaskStatusAsync(
                It.IsAny<string>(),
                It.IsAny<TaskState>(),
                It.IsAny<int?>(),
                It.IsAny<object?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            // Setup virtual key service
            VirtualKeyServiceMock.Setup(x => x.ValidateVirtualKeyAsync(It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(VirtualKeyValidationOutcome.Success(new VirtualKey
                {
                    Id = 1,
                    KeyName = "test-virtual-key",
                    KeyHash = "hashed-test-virtual-key",
                    IsEnabled = true,
                    VirtualKeyGroupId = 1
                }));

            // Setup model mapping service
            var testProvider = new Provider
            {
                Id = 1,
                ProviderName = "Test Provider",
                ProviderType = ProviderType.OpenAI,
                IsEnabled = true
            };

            ModelMappingServiceMock.Setup(x => x.GetMappingByModelAliasAsync(It.IsAny<string>()))
                .ReturnsAsync(new ModelProviderMapping
                {
                    Id = 1,
                    ModelAlias = "test-model",
                    ProviderModelId = "provider-model-id",
                    ProviderId = 1,
                    Provider = testProvider,
                    IsEnabled = true
                });

            // Setup cost service
            CostServiceMock.Setup(x => x.CalculateCostAsync(
                It.IsAny<string>(),
                It.IsAny<Usage>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(0.01m);

            // No IEventBus setup needed — the loose mock returns a completed Task for
            // any PublishAsync<TEvent> call; individual tests Verify specific publishes.
        }

        // ==========================================
        // Common Test Cases
        // ==========================================

        [Fact]
        public async Task HandleAsync_WhenRequestIsValid_ShouldProcessSuccessfully()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var response = CreateTestResponse();

            SetupSuccessfulGeneration(response);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert
            TaskServiceMock.Verify(x => x.TryClaimTaskAsync(
                GetRequestId(request),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()), Times.Once);

            TaskServiceMock.Verify(x => x.UpdateTaskStatusAsync(
                GetRequestId(request),
                TaskState.Completed,
                It.IsAny<int?>(),
                It.IsAny<object?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task HandleAsync_ShouldGetClientByResolvedProviderId_NotByReResolvingModelName()
        {
            // Arrange - the default mapping mock has ModelAlias "test-model" and
            // ProviderModelId "provider-model-id" (deliberately different), so re-resolving
            // the provider model id as an alias would fail (issue #958)
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var response = CreateTestResponse();

            SetupSuccessfulGeneration(response);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert
            ClientFactoryMock.Verify(x => x.GetClientByProviderIdAsync(
                1, "provider-model-id", It.IsAny<CancellationToken>()), Times.Once);
            ClientFactoryMock.Verify(x => x.GetClientAsync(
                It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task HandleAsync_WhenProviderOutcomeIsUnknown_ShouldBecomeIndeterminate()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var exception = new InvalidOperationException("Generation failed");

            SetupFailedGeneration(exception);

            // Act - Should handle failure gracefully
            await Orchestrator.HandleAsync(request, context);

            // Assert - provider may have accepted the request, so blind retry is unsafe
            TaskServiceMock.Verify(x => x.UpdateTaskStatusAsync(
                GetRequestId(request),
                TaskState.Indeterminate,
                It.IsAny<int?>(),
                It.IsAny<object?>(),
                It.Is<string?>(message => message != null && message.Contains(exception.Message)),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task HandleAsync_WhenProviderErrors_ExternalModeSanitizesTaskError()
        {
            // Arrange — orchestrators default to External customer mode when no translator
            // is injected, so raw provider bodies must not reach the stored task error.
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var exception = new ConduitLLM.Core.Exceptions.LLMCommunicationException(
                "API returned an error: 429 - secret provider quota text",
                System.Net.HttpStatusCode.TooManyRequests,
                "secret provider quota text");

            SetupFailedGeneration(exception);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert — indeterminate or failed, either way the stored error is sanitized
            TaskServiceMock.Verify(x => x.UpdateTaskStatusAsync(
                GetRequestId(request),
                It.Is<TaskState>(state => state == TaskState.Indeterminate || state == TaskState.Failed),
                It.IsAny<int?>(),
                It.IsAny<object?>(),
                It.Is<string?>(message =>
                    message != null &&
                    !message.Contains("secret provider quota text") &&
                    message.Contains("rate-limited")),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        // Note: Cancellation testing removed from base class due to complexity
        // The orchestrator's cancellation handling requires the linked CancellationTokenSource
        // to be cancelled, which only happens when an OperationCanceledException is thrown
        // with the correct token. This is difficult to test generically across all derived
        // orchestrators which have different initialization requirements.
        // Each derived orchestrator can implement its own cancellation test if needed.

        [Fact]
        public async Task HandleAsync_WhenVirtualKeyIsInvalid_ShouldFailGracefully()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();

            VirtualKeyServiceMock.Setup(x => x.ValidateVirtualKeyAsync(It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(VirtualKeyValidationOutcome.Failure(
                    VirtualKeyValidationFailureCodes.KeyNotFound,
                    401,
                    "Virtual key was not found."));

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert - Should update task status to Failed with appropriate error
            TaskServiceMock.Verify(x => x.UpdateTaskStatusAsync(
                GetRequestId(request),
                TaskState.Failed,
                It.IsAny<int?>(),
                It.IsAny<object?>(),
                It.Is<string>(s =>
                    s.Contains("Virtual key validation returned null", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("disabled", StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task HandleAsync_WhenVirtualKeyIsDisabled_ShouldFailGracefully()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();

            VirtualKeyServiceMock.Setup(x => x.ValidateVirtualKeyAsync(It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(VirtualKeyValidationOutcome.Failure(
                    VirtualKeyValidationFailureCodes.KeyDisabled,
                    401,
                    "Virtual key is disabled."));

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert - Should update task status to Failed with appropriate error
            TaskServiceMock.Verify(x => x.UpdateTaskStatusAsync(
                GetRequestId(request),
                TaskState.Failed,
                It.IsAny<int?>(),
                It.IsAny<object?>(),
                It.Is<string>(s => s.Contains("Virtual key validation returned null") || s.Contains("Invalid") || s.Contains("unauthorized") || s.Contains("disabled")),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task HandleAsync_WhenModelNotFound_ShouldFailGracefully()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();

            ModelMappingServiceMock.Setup(x => x.GetMappingByModelAliasAsync(It.IsAny<string>()))
                .ReturnsAsync((ModelProviderMapping?)null);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert - Should update task status to Failed with appropriate error
            TaskServiceMock.Verify(x => x.UpdateTaskStatusAsync(
                GetRequestId(request),
                TaskState.Failed,
                It.IsAny<int?>(),
                It.IsAny<object?>(),
                It.Is<string>(s => s.Contains("Model") && (s.Contains("not configured") || s.Contains("not found") || s.Contains("not available"))),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task HandleAsync_ShouldRegisterAndUnregisterTaskInRegistry()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var response = CreateTestResponse();

            SetupSuccessfulGeneration(response);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert
            TaskRegistryMock.Verify(x => x.RegisterClaimedTask(
                GetRequestId(request),
                It.IsAny<CancellationTokenSource>()), Times.Once);

            TaskRegistryMock.Verify(x => x.UnregisterTask(
                GetRequestId(request), It.IsAny<CancellationTokenSource>()), Times.Once);
        }

        [Fact]
        public async Task HandleAsync_WhenWebhookConfigured_ShouldSendNotification()
        {
            // Arrange
            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var response = CreateTestResponse();

            SetupSuccessfulGeneration(response);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert
            var webhookUrl = GetWebhookUrl(request);
            if (!string.IsNullOrEmpty(webhookUrl))
            {
                EventBusMock.Verify(x => x.PublishAsync(
                    It.Is<WebhookDeliveryRequested>(w => 
                        w.TaskId == GetRequestId(request) &&
                        w.WebhookUrl == webhookUrl &&
                        w.Headers != null && w.Headers["Authorization"] == "Bearer callback-test"),
                    It.IsAny<CancellationToken>()), Times.Once);
            }
        }

        [Fact]
        public async Task HandleAsync_TerminalCommitError_DoesNotManufactureFailedOutcome()
        {
            var request = CreateTestEventRequest();
            SetupSuccessfulGeneration(CreateTestResponse());
            EventBusMock.Setup(bus => bus.PublishAsync(It.IsAny<WebhookDeliveryRequested>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("Commit outcome may be unknown"));
            await Assert.ThrowsAsync<IOException>(() => Orchestrator.HandleAsync(request, CreateEventContext()));
            TaskServiceMock.Verify(service => service.UpdateTaskStatusAsync(GetRequestId(request), TaskState.Failed,
                It.IsAny<int?>(), It.IsAny<object?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task HandleAsync_WhenMappingHasModelCostId_ShouldUseDirectCostLookupAndPublishSpend()
        {
            // Arrange - mapping resolved through the model catalog: legacy ProviderModelId is stale
            // ("unknown"), but the association carries a direct ModelCost link (issue #955)
            SetupMappingWithAssociation(providerModelId: "unknown", modelCostId: 42, identifier: "canonical-model-id");

            CostServiceMock.Setup(x => x.CalculateCostByIdAsync(
                42,
                It.IsAny<Usage>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(0.05m);

            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var response = CreateTestResponse();

            SetupSuccessfulGeneration(response);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert - direct lookup used, string matching skipped
            CostServiceMock.Verify(x => x.CalculateCostByIdAsync(
                42,
                It.IsAny<Usage>(),
                It.IsAny<CancellationToken>()), Times.Once);

            CostServiceMock.Verify(x => x.CalculateCostAsync(
                It.IsAny<string>(),
                It.IsAny<Usage>(),
                It.IsAny<CancellationToken>()), Times.Never);

            // Assert - spend event published with the calculated cost
            EventBusMock.Verify(x => x.PublishAsync(
                It.Is<SpendUpdateRequested>(e => e.KeyId == 1 && e.Amount == 0.05m),
                It.IsAny<CancellationToken>()), Times.Once);
            EventBusMock.Verify(x => x.PublishAsync(
                It.IsAny<SpendUpdateRequested>(),
                It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task HandleAsync_SequentialReplay_InvokesProviderExactlyOnce()
        {
            var request = CreateTestEventRequest();
            var response = CreateTestResponse();
            SetupSuccessfulGeneration(response);
            TaskServiceMock.SetupSequence(x => x.TryClaimTaskAsync(
                    GetRequestId(request), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(AsyncTaskClaimResult.Claimed)
                .ReturnsAsync(AsyncTaskClaimResult.Terminal)
                .ReturnsAsync(AsyncTaskClaimResult.Terminal);

            await Orchestrator.HandleAsync(request, CreateEventContext());
            await Orchestrator.HandleAsync(request, CreateEventContext());
            await Orchestrator.HandleAsync(request, CreateEventContext());

            ClientFactoryMock.Verify(x => x.GetClientByProviderIdAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
            TaskServiceMock.Verify(x => x.UpdateTaskStatusAsync(
                GetRequestId(request), TaskState.Completed, It.IsAny<int?>(), It.IsAny<object?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task HandleAsync_ConcurrentReplay_InvokesProviderExactlyOnce()
        {
            var request = CreateTestEventRequest();
            var response = CreateTestResponse();
            SetupSuccessfulGeneration(response);
            var claims = 0;
            TaskServiceMock.Setup(x => x.TryClaimTaskAsync(
                    GetRequestId(request), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Interlocked.Increment(ref claims) == 1
                    ? AsyncTaskClaimResult.Claimed
                    : AsyncTaskClaimResult.AlreadyClaimed);

            await Task.WhenAll(
                Orchestrator.HandleAsync(request, CreateEventContext()),
                Orchestrator.HandleAsync(request, CreateEventContext()),
                Orchestrator.HandleAsync(request, CreateEventContext()));

            ClientFactoryMock.Verify(x => x.GetClientByProviderIdAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task HandleAsync_WhenNoModelCostId_ShouldFallBackToAssociationIdentifierLookup()
        {
            // Arrange - no direct cost link; the string fallback must use the association's
            // canonical identifier (what cost records match against), not the stale ProviderModelId
            SetupMappingWithAssociation(providerModelId: "unknown", modelCostId: null, identifier: "canonical-model-id");

            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var response = CreateTestResponse();

            SetupSuccessfulGeneration(response);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert
            CostServiceMock.Verify(x => x.CalculateCostAsync(
                "canonical-model-id",
                It.IsAny<Usage>(),
                It.IsAny<CancellationToken>()), Times.Once);

            CostServiceMock.Verify(x => x.CalculateCostByIdAsync(
                It.IsAny<int>(),
                It.IsAny<Usage>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task HandleAsync_WhenCostIsZero_ShouldNotPublishSpendEvent()
        {
            // Arrange
            SetupMappingWithAssociation(providerModelId: "unknown", modelCostId: 42, identifier: "canonical-model-id");

            CostServiceMock.Setup(x => x.CalculateCostByIdAsync(
                42,
                It.IsAny<Usage>(),
                It.IsAny<CancellationToken>()))
                .ReturnsAsync(0m);

            var request = CreateTestEventRequest();
            var context = CreateEventContext();
            var response = CreateTestResponse();

            SetupSuccessfulGeneration(response);

            // Act
            await Orchestrator.HandleAsync(request, context);

            // Assert
            EventBusMock.Verify(x => x.PublishAsync(
                It.IsAny<SpendUpdateRequested>(),
                It.IsAny<CancellationToken>()), Times.Never);
        }

        // ==========================================
        // Helper Methods
        // ==========================================

        /// <summary>
        /// Configures the model mapping service to return a mapping whose
        /// ModelProviderTypeAssociation carries the given cost linkage.
        /// </summary>
        protected void SetupMappingWithAssociation(string providerModelId, int? modelCostId, string identifier)
        {
            var testProvider = new Provider
            {
                Id = 1,
                ProviderName = "Test Provider",
                ProviderType = ProviderType.OpenAI,
                IsEnabled = true
            };

            ModelMappingServiceMock.Setup(x => x.GetMappingByModelAliasAsync(It.IsAny<string>()))
                .ReturnsAsync(new ModelProviderMapping
                {
                    Id = 1,
                    ModelAlias = "test-model",
                    ProviderModelId = providerModelId,
                    ProviderId = 1,
                    Provider = testProvider,
                    IsEnabled = true,
                    ModelProviderTypeAssociationId = 10,
                    ModelProviderTypeAssociation = new ModelProviderTypeAssociation
                    {
                        Id = 10,
                        ModelId = 100,
                        Identifier = identifier,
                        ModelCostId = modelCostId,
                        IsEnabled = true
                    }
                });
        }

        protected abstract void SetupSuccessfulGeneration(TResponse response);
        protected abstract void SetupFailedGeneration(Exception exception);

        protected TestEventContext CreateEventContext(CancellationToken cancellationToken = default)
        {
            return new TestEventContext
            {
                CancellationToken = cancellationToken,
                CorrelationId = Guid.NewGuid().ToString()
            };
        }
    }
    
    /// <summary>
    /// Test implementation of IMeterFactory for unit tests
    /// </summary>
    public class TestMeterFactory : System.Diagnostics.Metrics.IMeterFactory
    {
        public System.Diagnostics.Metrics.Meter Create(string name, string? version = null)
        {
            return new System.Diagnostics.Metrics.Meter(name, version);
        }

        public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options)
        {
            return new System.Diagnostics.Metrics.Meter(options.Name, options.Version);
        }

        public void Dispose()
        {
            // No-op for tests
        }
    }
}
