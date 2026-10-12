using System.Runtime.CompilerServices;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core;
using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Billing;
using ConduitLLM.Gateway.Constants;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

public class ChatContextRoutingTests
{
    public static IEnumerable<object[]> BillingCases()
    {
        foreach (var path in new[] { "/v1/chat/completions", "/v1/conduit/chat/completions" })
        foreach (var streaming in new[] { false, true })
        foreach (var replacement in new[] { "built-in", "custom" })
            yield return [path, streaming, replacement];
    }

    [Theory]
    [MemberData(nameof(BillingCases))]
    public async Task ReplacedRequest_BillsAndLogsSelectedRoute(string path, bool streaming, string replacement)
    {
        var request = Request(streaming);
        var usage = new Usage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 };
        var client = new Mock<ILLMClient>();
        client.Setup(service => service.CreateChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse
            {
                Id = "response", Created = 0, Object = "chat.completion", Model = "routed-model", Usage = usage,
                Choices = [new Choice { Index = 0, FinishReason = FinishReason.Stop,
                    Message = new Message { Role = MessageRole.Assistant, Content = "done" } }]
            });
        client.Setup(service => service.StreamChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()))
            .Returns(Chunks(usage));
        var factory = Factory(client.Object, request);
        var mappings = Mappings();
        var conduit = new Conduit(factory.Object, NullLogger<Conduit>.Instance, Manager(replacement),
            mappings.Object, Options.Create(new ContextManagementOptions()));
        var modelCosts = new Mock<IModelCostService>();
        modelCosts.Setup(service => service.GetCostByIdAsync(17, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelCost { Id = 17, IsActive = true,
                InputCostPerMillionTokens = 2.5m, OutputCostPerMillionTokens = 10m });
        modelCosts.Setup(service => service.GetCostByIdAsync(23, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelCost { Id = 23, IsActive = true,
                InputCostPerMillionTokens = 50m, OutputCostPerMillionTokens = 100m });
        var pricing = new CostCalculationService(modelCosts.Object, NullLogger<CostCalculationService>.Instance);
        var reservations = new Mock<ISpendReservationService>();
        reservations.Setup(service => service.ReserveAsync(42, It.IsAny<string>(), 0.25m))
            .ReturnsAsync(new SpendReservationResult(SpendReservationOutcome.Reserved, 0.25m));
        reservations.Setup(service => service.MarkInvocationStartedAsync(42, It.IsAny<string>())).ReturnsAsync(true);
        decimal? settled = null;
        reservations.Setup(service => service.SettleAsync(42, It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<DateTime?>()))
            .Callback<int, string, decimal, DateTime?>((_, _, cost, _) => settled = cost)
            .ReturnsAsync(new SpendReservationSettlementResult(SpendReservationSettlementStatus.Settled, 0m));
        using var services = new ServiceCollection().AddLogging()
            .ConfigureHttpJsonOptions(options => GatewayJsonOptions.Configure(options.SerializerOptions))
            .AddSingleton<ISpendReservationService>(reservations.Object).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        context.Items[HttpContextKeys.VirtualKeyId] = 42;
        context.Response.Body = new MemoryStream();
        var maximumSpend = new Mock<IChatSpendEstimator>();
        maximumSpend.Setup(service => service.EstimateMaximumCostAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatSpendEstimate(true, 0.25m, 10, 128, null));
        var endpoint = new ChatEndpoints(conduit, NullLogger<ChatEndpoints>.Instance, mappings.Object,
            GatewayJsonOptions.Create(), Mock.Of<IEventPublisher>(), Mock.Of<IGlobalSettingsCacheService>(),
            Mock.Of<IUsageEstimationService>(), chatSpendEstimator: maximumSpend.Object,
            spendReservationService: reservations.Object,
            billingAdmissionOptions: Options.Create(new BillingAdmissionOptions { Mode = BillingAdmissionMode.Enforce }),
            httpContextAccessor: new HttpContextAccessor { HttpContext = context }, costCalculationService: pricing);
        var logs = new List<LogRequestDto>();
        var writer = new Mock<IRequestLogRuntimeWriter>();
        writer.Setup(service => service.LogRequestAsync(It.IsAny<LogRequestDto>()))
            .Callback<LogRequestDto>(logs.Add).Returns(Task.CompletedTask);
        var middleware = new UsageTrackingMiddleware(async ctx =>
        {
            var result = await endpoint.CreateChatCompletion(request);
            await result.ExecuteAsync(ctx);
        }, NullLogger<UsageTrackingMiddleware>.Instance);

        await middleware.InvokeAsync(context, pricing, Mock.Of<IBatchSpendUpdateService>(), writer.Object,
            Mock.Of<IVirtualKeyRuntimeService>(), Mock.Of<IBillingAuditService>(), Mock.Of<IToolCostCalculationService>());

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(0.001m, settled);
        var log = Assert.Single(logs);
        Assert.Equal(settled, log.Cost);
        Assert.Equal(2, log.ProviderId);
        Assert.Equal(23, log.ModelProviderMappingId);
        Assert.Equal(23, context.Items[HttpContextKeys.ModelCostId]);
        Assert.Equal(23, request.SelectedMappingId);
        Assert.True(request.RoutingAffinityUsed);
        Assert.Equal("selected-route", request.RoutingDecisionReason);
        Assert.Equal(2, request.RoutingFailoverCount);
        Assert.Equal(2, request.Messages.Count);
        modelCosts.Verify(service => service.GetCostByIdAsync(23, It.IsAny<CancellationToken>()), Times.Once);
        modelCosts.Verify(service => service.GetCostByIdAsync(17, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BuiltInTrimming_PreservesRuntimeInputsForDirectContextManagerCall()
    {
        var request = Request(false);
        var managed = await Manager("built-in").ManageContextAsync(request, 100);

        Assert.NotSame(request, managed);
        Assert.Single(managed.Messages);
        AssertRuntimeInputs(request, managed);
        Assert.Equal(2, request.Messages.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Replacement_CopiesRoutingBackAfterProviderFailureOrCancellation(bool streaming, bool cancelled)
    {
        var request = Request(streaming);
        using var cancellation = new CancellationTokenSource();
        if (cancelled) cancellation.Cancel();
        var failure = cancelled ? (Exception)new OperationCanceledException(cancellation.Token)
            : new LLMCommunicationException("provider failed before output");
        var client = new Mock<ILLMClient>();
        client.Setup(service => service.CreateChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        client.Setup(service => service.StreamChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>())).Returns(FailedChunks(failure));
        var conduit = new Conduit(Factory(client.Object, request).Object, NullLogger<Conduit>.Instance,
            Manager("custom"), Mappings().Object, Options.Create(new ContextManagementOptions()));

        async Task Execute()
        {
            if (streaming)
                await foreach (var _ in conduit.StreamChatCompletionAsync(request, cancellationToken: cancellation.Token)) { }
            else
                await conduit.CreateChatCompletionAsync(request, cancellationToken: cancellation.Token);
        }
        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(Execute));
        Assert.Equal(23, request.SelectedMappingId);
        Assert.True(request.RoutingAffinityUsed);
        Assert.Equal("selected-route", request.RoutingDecisionReason);
        Assert.Equal(2, request.RoutingFailoverCount);
        Assert.Equal(2, request.Messages.Count);
    }

    [Fact]
    public async Task StreamDisposal_CopiesProviderFinalRoutingStateBackToOriginal()
    {
        var request = Request(true);
        var client = new Mock<ILLMClient>();
        client.Setup(service => service.StreamChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()))
            .Returns((ChatCompletionRequest active, string? _, CancellationToken token) => DisposeChunks(active, token));
        var conduit = new Conduit(Factory(client.Object, request).Object, NullLogger<Conduit>.Instance,
            Manager("custom"), Mappings().Object, Options.Create(new ContextManagementOptions()));
        var enumerator = conduit.StreamChatCompletionAsync(request).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(23, request.SelectedMappingId);
        Assert.Equal(2, request.RoutingFailoverCount);

        await enumerator.DisposeAsync();

        Assert.Equal("disposed", request.RoutingDecisionReason);
        Assert.Equal(3, request.RoutingFailoverCount);
        Assert.Equal(2, request.Messages.Count);
    }

    private static ChatCompletionRequest Request(bool streaming) => new()
    {
        Model = "routed-model", Stream = streaming,
        Messages = [new Message { Role = MessageRole.User, Content = "old" },
            new Message { Role = MessageRole.User, Content = "current" }],
        PromptCachingIntent = new PromptCachingIntent { Strategy = PromptCachingStrategy.Automatic,
            PromptCacheKey = "resolved-key" },
        RoutingAffinityKey = "opaque-affinity", SelectedMappingId = 9,
        RoutingDecisionReason = "initial", RoutingFailoverCount = 1
    };

    private static void AssertRuntimeInputs(ChatCompletionRequest original, ChatCompletionRequest active)
    {
        Assert.Same(original.PromptCachingIntent, active.PromptCachingIntent);
        Assert.Equal(original.RoutingAffinityKey, active.RoutingAffinityKey);
        Assert.Equal(original.SelectedMappingId, active.SelectedMappingId);
        Assert.Equal(original.RoutingAffinityUsed, active.RoutingAffinityUsed);
        Assert.Equal(original.RoutingDecisionReason, active.RoutingDecisionReason);
        Assert.Equal(original.RoutingFailoverCount, active.RoutingFailoverCount);
    }

    private static Mock<ILLMClientFactory> Factory(ILLMClient client, ChatCompletionRequest original)
    {
        var factory = new Mock<ILLMClientFactory>();
        factory.Setup(service => service.GetClientForChatAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((active, _) =>
            {
                Assert.NotSame(original, active);
                Assert.Single(active.Messages);
                AssertRuntimeInputs(original, active);
                active.SelectedMappingId = 23;
                active.RoutingAffinityUsed = true;
                active.RoutingDecisionReason = "selected-route";
                active.RoutingFailoverCount = 2;
            }).ReturnsAsync(client);
        return factory;
    }

    private static Mock<IModelProviderMappingService> Mappings()
    {
        var mappings = new Mock<IModelProviderMappingService>();
        mappings.Setup(service => service.GetMappingByModelAliasAsync("routed-model"))
            .ReturnsAsync(new ModelProviderMapping { Id = 17, ProviderId = 1,
                Provider = new Provider { Id = 1, ProviderType = ProviderType.OpenAI },
                ModelProviderTypeAssociation = new ModelProviderTypeAssociation { ModelCostId = 17, MaxInputTokens = 100 } });
        mappings.Setup(service => service.GetMappingByIdAsync(23))
            .ReturnsAsync(new ModelProviderMapping { Id = 23, ProviderId = 2,
                Provider = new Provider { Id = 2, ProviderType = ProviderType.OpenAI },
                ModelProviderTypeAssociation = new ModelProviderTypeAssociation { ModelCostId = 23 } });
        return mappings;
    }

    private static IContextManager Manager(string replacement)
    {
        if (replacement == "built-in")
        {
            var counter = new Mock<ITokenCounter>();
            counter.Setup(service => service.EstimateTokenCountAsync("routed-model", It.IsAny<List<Message>>(), null))
                .ReturnsAsync((string _, List<Message> messages, IReadOnlyList<Tool>? _) =>
                    new TokenCount(messages.Count * 100, TokenCountFidelity.Exact));
            return new ContextManager(counter.Object, NullLogger<ContextManager>.Instance);
        }
        var manager = new Mock<IContextManager>();
        manager.Setup(service => service.ManageContextAsync(It.IsAny<ChatCompletionRequest>(), 100))
            .ReturnsAsync((ChatCompletionRequest request, int? _) => new ChatCompletionRequest
            { Model = request.Model, Messages = request.Messages.TakeLast(1).ToList(), Stream = request.Stream });
        return manager.Object;
    }

    private static async IAsyncEnumerable<ChatCompletionChunk> Chunks(Usage usage)
    {
        await Task.Yield();
        yield return new ChatCompletionChunk { Id = "chunk", Created = 0, Model = "routed-model", Usage = usage,
            Choices = [new StreamingChoice { Index = 0, FinishReason = FinishReason.Stop,
                Delta = new DeltaContent { Content = "done" } }] };
    }

    private static async IAsyncEnumerable<ChatCompletionChunk> FailedChunks(Exception failure)
    {
        await Task.Yield();
        throw failure;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<ChatCompletionChunk> DisposeChunks(ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken token)
    {
        try
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            yield return new ChatCompletionChunk { Id = "chunk", Created = 0, Model = request.Model, Choices = [] };
        }
        finally
        {
            request.RoutingDecisionReason = "disposed";
            request.RoutingFailoverCount = 3;
        }
    }
}
