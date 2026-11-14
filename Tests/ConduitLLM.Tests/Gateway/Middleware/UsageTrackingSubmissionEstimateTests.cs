using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.UsageTracking;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.Tests.Gateway.Middleware;

[Trait("Category", "Unit")]
[Trait("Component", "Billing")]
public sealed class UsageTrackingSubmissionEstimateTests
{
    [Fact]
    public async Task Async_image_submission_failure_does_not_bill_its_staged_estimate()
    {
        using var request = new MediaRequest(image: true);
        request.Submission.Setup(service => service.SubmitAsync(
                It.IsAny<ImageGenerationRequested>(), It.IsAny<TaskMetadata>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Command was not accepted"));

        await request.InvokeAsync(async context =>
        {
            var result = await request.Images.CreateImageAsync(ImageRequest());
            context.Response.StatusCode = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;
        });

        Assert.Equal(StatusCodes.Status500InternalServerError, request.Context.Response.StatusCode);
        Assert.NotNull(request.Accounting.Snapshot().ProviderUsage);
        request.AssertNoCharge();
        Assert.Empty(request.Logs);
        request.Submission.Verify(service => service.SubmitAsync(
            It.IsAny<ImageGenerationRequested>(), It.IsAny<TaskMetadata>(), It.IsAny<CancellationToken>()), Times.Once);
        request.ClientFactory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Async_video_submission_exception_does_not_bill_its_staged_estimate()
    {
        using var request = new MediaRequest(image: false);
        var failure = new IOException("Command was not accepted");
        request.Submission.Setup(service => service.SubmitAsync(
                It.IsAny<VideoGenerationRequested>(), It.IsAny<TaskMetadata>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);

        var actual = await Assert.ThrowsAsync<IOException>(() => request.InvokeAsync(async _ =>
            await request.Videos.GenerateVideoAsync(VideoRequest())));

        Assert.Same(failure, actual);
        Assert.NotNull(request.Accounting.Snapshot().ProviderUsage);
        request.AssertNoCharge();
        Assert.Empty(request.Logs);
        request.Submission.Verify(service => service.SubmitAsync(
            It.IsAny<VideoGenerationRequested>(), It.IsAny<TaskMetadata>(), It.IsAny<CancellationToken>()), Times.Once);
        request.ClientFactory.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Accepted_async_submission_leaves_billing_with_the_worker(bool image)
    {
        using var request = new MediaRequest(image);
        request.Submission.Setup(service => service.SubmitAsync(
                It.IsAny<ImageGenerationRequested>(), It.IsAny<TaskMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("accepted-image");
        request.Submission.Setup(service => service.SubmitAsync(
                It.IsAny<VideoGenerationRequested>(), It.IsAny<TaskMetadata>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("accepted-video");

        await request.InvokeAsync(async context =>
        {
            var result = image
                ? await request.Images.CreateImageAsync(ImageRequest())
                : await request.Videos.GenerateVideoAsync(VideoRequest());
            context.Response.StatusCode = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;
        });

        request.AssertNoCharge();
        var log = Assert.Single(request.Logs);
        Assert.Equal(StatusCodes.Status202Accepted, log.StatusCode);
        Assert.Equal(0m, log.Cost);
        Assert.NotNull(log.Metadata);
        Assert.Equal(image ? "accepted-image" : "accepted-video", log.Metadata["taskId"].GetString());
        request.ClientFactory.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Completed_sync_image_without_provider_usage_is_billed_after_response_writing_fails()
    {
        using var request = new MediaRequest(image: true);
        var client = new Mock<ILLMClient>();
        client.Setup(provider => provider.CreateImageAsync(
                It.IsAny<ImageGenerationRequest>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageGenerationResponse { Created = 1, Data = [new ConduitLLM.Core.Models.ImageData()], Usage = null });
        request.ClientFactory.Setup(factory => factory.GetClientAsync("test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        var failure = new IOException("Response writing failed after provider completion");

        var actual = await Assert.ThrowsAsync<IOException>(() => request.InvokeAsync(async context =>
        {
            await request.Images.CreateImage(ImageRequest());
            Assert.Equal(UsageEvidenceSource.Estimated, request.Accounting.Snapshot().ProviderUsage!.Source);
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            throw failure;
        }));

        Assert.Same(failure, actual);
        request.Batch.Verify(service => service.QueueSpendUpdateAsync(42, MediaRequest.Price, It.IsAny<DateTime?>()), Times.Once);
        Assert.Equal(MediaRequest.Price, Assert.Single(request.Logs).Cost);
        client.Verify(provider => provider.CreateImageAsync(
            It.IsAny<ImageGenerationRequest>(), null, It.IsAny<CancellationToken>()), Times.Once);
        request.Submission.VerifyNoOtherCalls();
    }

    private static ImageGenerationRequest ImageRequest() => new() { Model = "test-model", Prompt = "image", N = 1 };
    private static VideoGenerationRequest VideoRequest() => new() { Model = "test-model", Prompt = "video", Duration = 10 };

    private sealed class MediaRequest : IDisposable
    {
        public const decimal Price = 0.25m;
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        public DefaultHttpContext Context { get; }
        public IRequestAccountingContext Accounting => Context.GetOrCreateRequestAccountingContext();
        public Mock<ICostCalculationService> Costs { get; } = new();
        public Mock<IBatchSpendUpdateService> Batch { get; } = new();
        public Mock<IMediaTaskSubmission> Submission { get; } = new();
        public Mock<ILLMClientFactory> ClientFactory { get; } = new(MockBehavior.Strict);
        public Mock<IVirtualKeyRuntimeService> Keys { get; } = new();
        public List<LogRequestDto> Logs { get; } = [];
        public ImagesEndpoints Images { get; }
        public VideosEndpoints Videos { get; }

        public MediaRequest(bool image)
        {
            Context = new DefaultHttpContext { RequestServices = _services };
            Context.Request.Path = image ? "/v1/images/generations/async" : "/v1/conduit/videos/generations/async";
            Context.Items["VirtualKeyId"] = 42;
            Context.Items["VirtualKey"] = "condt_test";
            Context.Response.ContentType = "application/json";
            var mappings = new Mock<IModelProviderMappingService>();
            mappings.Setup(service => service.GetMappingByModelAliasAsync("test-model"))
                .ReturnsAsync(new ModelProviderMapping
                {
                    ModelAlias = "test-model", ProviderModelId = "provider-model", ProviderId = 1,
                    Provider = new Provider { ProviderType = ProviderType.OpenAI },
                    ModelProviderTypeAssociation = new ModelProviderTypeAssociation
                    {
                        ModelCostId = 7, Model = new Model { SupportsImageGeneration = true }
                    }
                });
            Keys.Setup(service => service.GetVirtualKeyInfoForValidationAsync(42, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new VirtualKey { Id = 42, KeyHash = "test-hash" });
            var accessor = new HttpContextAccessor { HttpContext = Context };
            Images = new ImagesEndpoints(ClientFactory.Object, Mock.Of<IMediaStorageService>(),
                NullLogger<ImagesEndpoints>.Instance, mappings.Object, Mock.Of<IAsyncTaskService>(),
                Mock.Of<IEventPublisher>(), Keys.Object, Mock.Of<IMediaLifecycleService>(),
                Mock.Of<IProviderErrorTrackingService>(), null!, null!, accessor, Submission.Object);
            Videos = new VideosEndpoints(Mock.Of<IAsyncTaskService>(), Mock.Of<IOperationTimeoutProvider>(),
                Mock.Of<ICancellableTaskRegistry>(), NullLogger<VideosEndpoints>.Instance, mappings.Object,
                Mock.Of<IEventPublisher>(), accessor, Submission.Object);
            Costs.Setup(service => service.CalculateCostAsync(
                It.IsAny<string>(), It.IsAny<Usage>(), It.IsAny<CancellationToken>())).ReturnsAsync(Price);
            Costs.Setup(service => service.CalculateCostByIdAsync(
                It.IsAny<int>(), It.IsAny<Usage>(), It.IsAny<CancellationToken>())).ReturnsAsync(Price);
            Batch.SetupGet(service => service.IsHealthy).Returns(true);
            Batch.Setup(service => service.QueueSpendUpdateAsync(42, It.IsAny<decimal>(), It.IsAny<DateTime?>()))
                .Returns(Task.CompletedTask);
        }

        public Task InvokeAsync(RequestDelegate next)
        {
            var writer = new Mock<IRequestLogRuntimeWriter>();
            writer.Setup(service => service.LogRequestAsync(It.IsAny<LogRequestDto>()))
                .Callback<LogRequestDto>(Logs.Add).Returns(Task.CompletedTask);
            return new UsageTrackingMiddleware(next, NullLogger<UsageTrackingMiddleware>.Instance)
                .InvokeAsync(Context, Costs.Object, Batch.Object, writer.Object, Keys.Object,
                    Mock.Of<IBillingAuditService>(), Mock.Of<IToolCostCalculationService>());
        }

        public void AssertNoCharge()
        {
            Costs.Verify(service => service.CalculateCostAsync(
                It.IsAny<string>(), It.IsAny<Usage>(), It.IsAny<CancellationToken>()), Times.Never);
            Costs.Verify(service => service.CalculateCostByIdAsync(
                It.IsAny<int>(), It.IsAny<Usage>(), It.IsAny<CancellationToken>()), Times.Never);
            Batch.Verify(service => service.QueueSpendUpdateAsync(
                It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()), Times.Never);
            Keys.Verify(service => service.UpdateSpendAsync(
                It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<DateTime?>(), It.IsAny<string>()), Times.Never);
        }

        public void Dispose() => _services.Dispose();
    }
}
