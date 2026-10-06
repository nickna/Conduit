using System.Diagnostics.Metrics;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Metrics;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Core.Validation;
using ConduitLLM.Gateway.EventHandlers;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.FaultInjectionTests;

[Collection(BillingFaultCollection.Name)]
public sealed class MediaCancellationFaultTests(BillingFaultFixture fixture)
{
    [Fact(Timeout = 90_000)]
    public async Task ImageCancellation_AfterProviderGeneration_PersistsSpendExactlyOnce()
    {
        const decimal expectedCost = 0.01m;
        var account = await fixture.SeedAccountAsync();
        using var cancellation = new CancellationTokenSource();

        var services = new ServiceCollection();
        services.AddScoped<IVirtualKeyRepository>(_ => fixture.CreateKeyRepository());
        services.AddScoped<IVirtualKeyGroupRepository>(_ => fixture.CreateGroupRepository());
        using var provider = services.BuildServiceProvider();
        var bus = new ProcessingEventBus(provider.GetRequiredService<IServiceScopeFactory>(), cancellation);

        var taskService = new Mock<IAsyncTaskService>();
        taskService.Setup(service => service.GetTaskStatusAsync("media-fault-task", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AsyncTaskStatus
            {
                TaskId = "media-fault-task",
                State = TaskState.Pending,
                Metadata = new TaskMetadata
                {
                    VirtualKeyId = account.KeyId,
                    ExtensionData = new Dictionary<string, object> { ["VirtualKey"] = "test-key" }
                }
            });
        taskService.Setup(service => service.TryClaimTaskAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AsyncTaskClaimResult.Claimed);
        taskService.Setup(service => service.MarkProviderInvocationStartedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        taskService.Setup(service => service.ExtendTaskLeaseAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        taskService.Setup(service => service.MarkProviderInvocationCompletedAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        taskService.Setup(service => service.UpdateTaskStatusAsync(
                It.IsAny<string>(), It.IsAny<TaskState>(), It.IsAny<int?>(), It.IsAny<object?>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var llmClient = new Mock<ILLMClient>();
        llmClient.Setup(client => client.CreateImageAsync(
                It.IsAny<ConduitLLM.Core.Models.ImageGenerationRequest>(),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageGenerationResponse
            {
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Data = [new ConduitLLM.Core.Models.ImageData { Url = "https://provider.invalid/generated.png" }]
            });
        var clientFactory = new Mock<ILLMClientFactory>();
        clientFactory.Setup(factory => factory.GetClientByProviderIdAsync(
                1, "provider-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(llmClient.Object);

        var mappingService = new Mock<IModelProviderMappingService>();
        mappingService.Setup(service => service.GetMappingByModelAliasAsync("fault-image-model"))
            .ReturnsAsync(new ModelProviderMapping
            {
                Id = 1,
                ModelAlias = "fault-image-model",
                ProviderModelId = "provider-model",
                ProviderId = 1,
                IsEnabled = true,
                Provider = new Provider
                {
                    Id = 1,
                    ProviderName = "Fault provider",
                    ProviderType = ProviderType.OpenAI,
                    IsEnabled = true
                }
            });

        var virtualKeyService = new Mock<IVirtualKeyService>();
        virtualKeyService.Setup(service => service.ValidateVirtualKeyAsync("test-key", null))
            .ReturnsAsync(VirtualKeyValidationOutcome.Success(new VirtualKey
            {
                Id = account.KeyId,
                VirtualKeyGroupId = account.GroupId,
                KeyName = "fault-key",
                KeyHash = "fault-hash",
                IsEnabled = true
            }));
        var costService = new Mock<ICostCalculationService>();
        costService.Setup(service => service.CalculateCostAsync(
                "provider-model", It.Is<Usage>(usage => usage.ImageCount == 1), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedCost);

        using var metrics = new MediaGenerationMetrics(new TestMeterFactory());
        var orchestrator = new ImageGenerationOrchestrator(
            clientFactory.Object,
            taskService.Object,
            Mock.Of<IMediaStorageService>(),
            bus,
            mappingService.Object,
            virtualKeyService.Object,
            costService.Object,
            Mock.Of<ICancellableTaskRegistry>(),
            Mock.Of<IWebhookNotificationService>(),
            new CancelAwareHttpClientFactory(),
            new MinimalParameterValidator(NullLogger<MinimalParameterValidator>.Instance),
            metrics,
            Mock.Of<IProviderErrorTrackingService>(),
            NullLogger<ImageGenerationOrchestrator>.Instance);

        await orchestrator.HandleAsync(new ImageGenerationRequested
        {
            TaskId = "media-fault-task",
            VirtualKeyId = account.KeyId,
            Request = new ConduitLLM.Core.Models.ImageGenerationRequest
            {
                Model = "fault-image-model",
                Prompt = "fault injection",
                N = 1,
                ResponseFormat = "url"
            }
        }, new TestEventContext(cancellation.Token));

        cancellation.IsCancellationRequested.Should().BeTrue();
        await fixture.AssertAccountedForAsync(account.GroupId, expectedCost);
        bus.ProcessedSpendEvents.Should().Be(1);
    }

    private sealed class ProcessingEventBus(IServiceScopeFactory scopeFactory, CancellationTokenSource cancellation) : IEventBus
    {
        public int ProcessedSpendEvents { get; private set; }

        public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
            where TEvent : class
        {
            if (@event is not SpendUpdateRequested spend) return;

            var processor = new SpendUpdateProcessor(
                scopeFactory,
                this,
                NullLogger<SpendUpdateProcessor>.Instance);
            await processor.HandleAsync(spend, new TestEventContext(CancellationToken.None));
            ProcessedSpendEvents++;
            cancellation.Cancel();
        }

        public async Task PublishBatchAsync<TEvent>(IEnumerable<TEvent> events, CancellationToken cancellationToken = default)
            where TEvent : class
        {
            foreach (var @event in events) await PublishAsync(@event, cancellationToken);
        }
    }

    private sealed class CancelAwareHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new CancelAwareHandler());

        private sealed class CancelAwareHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                cancellationToken.IsCancellationRequested
                    ? Task.FromCanceled<HttpResponseMessage>(cancellationToken)
                    : Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent([0x89, 0x50, 0x4e, 0x47])
                    });
        }
    }

    private sealed class TestEventContext(CancellationToken cancellationToken) : IEventContext
    {
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public Guid? MessageId { get; } = Guid.NewGuid();
        public string? CorrelationId { get; } = Guid.NewGuid().ToString("N");
        public bool TryGetHeader(string key, out object? value) { value = null; return false; }
        public Task PublishAsync<TEvent>(TEvent @event, CancellationToken token = default) where TEvent : class => Task.CompletedTask;
        public Task SchedulePublishAsync<TEvent>(DateTime deliveryTime, TEvent @event, CancellationToken token = default) where TEvent : class => Task.CompletedTask;
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        public Meter Create(string name, string? version = null) => new(name, version);
        public Meter Create(MeterOptions options) => new(options.Name, options.Version);
        public void Dispose() { }
    }
}
