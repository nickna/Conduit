using AwesomeAssertions;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Billing;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.UsageTracking;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace ConduitLLM.FaultInjectionTests;

[Collection(BillingFaultCollection.Name)]
public sealed class ZeroCostSpendReservationTests(BillingFaultFixture fixture)
{
    [Theory(Timeout = 90_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnforcedFreeChat_InvokesProviderAndSettlesZeroExactlyOnce(bool streaming)
    {
        var account = await fixture.SeedAccountAsync();
        await using var batch = await fixture.StartBatchServiceAsync();
        var reservations = new SpendReservationService(batch, NullLogger<SpendReservationService>.Instance);
        using var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(options => GatewayJsonOptions.Configure(options.SerializerOptions))
            .AddSingleton<ISpendReservationService>(reservations)
            .BuildServiceProvider();
        using var responseBody = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/v1/conduit/chat/completions";
        context.Response.Body = responseBody;
        context.Items["VirtualKeyId"] = account.KeyId;
        context.Items["ProviderType"] = "OpenAI";

        var client = new Mock<ILLMClient>();
        client.Setup(service => service.CreateChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse
            {
                Id = "free-response", Model = "free-model", Object = "chat.completion", Created = 0,
                Usage = FreeUsage(),
                Choices = [new Choice
                {
                    Index = 0, FinishReason = FinishReason.Stop,
                    Message = new Message { Role = MessageRole.Assistant, Content = "free answer" }
                }]
            });
        client.Setup(service => service.StreamChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()))
            .Returns(FreeStream());
        var factory = new Mock<ILLMClientFactory>();
        factory.Setup(service => service.GetClientAsync("free-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        var maximumSpend = new Mock<IChatSpendEstimator>();
        maximumSpend.Setup(service => service.EstimateMaximumCostAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatSpendEstimate(true, 0m, 1, 1, null));
        var settings = new Mock<IGlobalSettingsCacheService>();
        settings.Setup(service => service.GetMaxAgenticIterationsAsync()).ReturnsAsync(5);
        settings.Setup(service => service.GetDefaultAgenticModeEnabledAsync()).ReturnsAsync(true);
        var endpoints = new ChatEndpoints(
            new Conduit(factory.Object, NullLogger<Conduit>.Instance),
            NullLogger<ChatEndpoints>.Instance, Mock.Of<IModelProviderMappingService>(),
            GatewayJsonOptions.Create(), Mock.Of<IEventPublisher>(), settings.Object,
            Mock.Of<IUsageEstimationService>(), chatSpendEstimator: maximumSpend.Object,
            spendReservationService: reservations,
            billingAdmissionOptions: Options.Create(new BillingAdmissionOptions { Mode = BillingAdmissionMode.Enforce }),
            httpContextAccessor: new HttpContextAccessor { HttpContext = context });
        var cost = new Mock<ICostCalculationService>();
        cost.Setup(service => service.CalculateCostAsync("free-model", It.IsAny<Usage>())).ReturnsAsync(0m);
        var middleware = new UsageTrackingMiddleware(async httpContext =>
        {
            var result = await endpoints.CreateChatCompletion(new ChatCompletionRequest
            {
                Model = "free-model", Stream = streaming,
                Messages = [new Message { Role = MessageRole.User, Content = "hello" }]
            }, httpContext.RequestAborted);
            await result.ExecuteAsync(httpContext);
        }, NullLogger<UsageTrackingMiddleware>.Instance);

        await middleware.InvokeAsync(context, cost.Object, batch, Mock.Of<IRequestLogRuntimeWriter>(),
            Mock.Of<IVirtualKeyRuntimeService>(), Mock.Of<IBillingAuditService>(),
            Mock.Of<IToolCostCalculationService>());

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        factory.Verify(service => service.GetClientAsync("free-model", It.IsAny<CancellationToken>()), Times.Once);
        cost.Verify(service => service.CalculateCostAsync("free-model", It.IsAny<Usage>()), Times.Once);
        var snapshot = context.GetRequestAccountingSnapshot()!;
        snapshot.ProviderUsage.Should().NotBeNull();
        snapshot.IsIndeterminate.Should().BeFalse();
        snapshot.Reservation.Should().NotBeNull();
        snapshot.Reservation!.ReservedAmount.Should().Be(0m);
        snapshot.Reservation.InvocationStarted.Should().BeTrue();
        snapshot.Reservation.Closed.Should().BeTrue();
        (await reservations.SettleAsync(account.KeyId, snapshot.BillingRequestId, 0m))
            .Status.Should().Be(SpendReservationSettlementStatus.AlreadySettled);
        (await reservations.SettleAsync(account.KeyId, snapshot.BillingRequestId, 0.05m))
            .Status.Should().Be(SpendReservationSettlementStatus.Conflict);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var database = redis.GetDatabase();
        (await database.HashExistsAsync($"spend_reservations:group:{account.GroupId}", snapshot.BillingRequestId))
            .Should().BeFalse();
        (await database.HashExistsAsync($"spend_reservations_started:group:{account.GroupId}", snapshot.BillingRequestId))
            .Should().BeFalse();
        (await database.HashGetAsync($"spend_reservations_settled:group:{account.GroupId}", snapshot.BillingRequestId))
            .ToString().Should().Be("0");
        (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(0m);
        (await batch.FlushPendingUpdatesAsync()).Should().Be(0);
        await fixture.AssertAccountedForAsync(account.GroupId, 0m);
        await using var db = fixture.CreateDbContext();
        (await db.VirtualKeyGroups.SingleAsync(group => group.Id == account.GroupId)).Balance.Should().Be(100m);
        (await db.VirtualKeyGroupTransactions.CountAsync(transaction => transaction.VirtualKeyGroupId == account.GroupId))
            .Should().Be(0);
    }

    [Fact(Timeout = 90_000)]
    public async Task ZeroReservation_ReleaseAndExpiryRemoveLifecycleWithoutReleasingOtherHolds()
    {
        var account = await fixture.SeedAccountAsync();
        await using var batch = await fixture.StartBatchServiceAsync();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var database = redis.GetDatabase();
        var heldKey = $"spend_reservations:group:{account.GroupId}";
        var expiryKey = $"spend_reservation_expiry:group:{account.GroupId}";
        (await batch.TryReserveSpendAsync(account.KeyId, 0m, "release-free")).Should().BeTrue();
        (await database.HashGetAsync(heldKey, "release-free")).ToString().Should().Be("0");
        (await database.KeyExistsAsync($"reserved_spend:group:{account.GroupId}")).Should().BeFalse();
        (await batch.TryReserveSpendAsync(account.KeyId, 0.25m, "paid-hold")).Should().BeTrue();
        await batch.ReleaseSpendReservationAsync(account.KeyId, "release-free");
        await batch.ReleaseSpendReservationAsync(account.KeyId, "release-free");
        (await database.HashExistsAsync(heldKey, "release-free")).Should().BeFalse();
        (await database.SortedSetScoreAsync(expiryKey, "release-free")).Should().BeNull();
        (await batch.MarkSpendReservationInvocationStartedAsync(account.KeyId, "release-free")).Should().BeFalse();
        (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(0.25m);

        (await batch.TryReserveSpendAsync(account.KeyId, 0m, "expire-free")).Should().BeTrue();
        await database.SortedSetAddAsync(expiryKey, "expire-free", DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        (await batch.TryReserveSpendAsync(account.KeyId, 0m, "replacement-free")).Should().BeTrue();
        (await database.HashExistsAsync(heldKey, "expire-free")).Should().BeFalse();
        (await database.SortedSetScoreAsync(expiryKey, "expire-free")).Should().BeNull();
        (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(0.25m);
    }

    [Fact(Timeout = 90_000)]
    public async Task ZeroReservation_StillValidatesRequestIdentityAndAccount_RejectsNegativeAmount()
    {
        var account = await fixture.SeedAccountAsync();
        await using var batch = await fixture.StartBatchServiceAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => batch.TryReserveSpendAsync(account.KeyId, 0m, ""));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => batch.TryReserveSpendAsync(account.KeyId, -0.01m, "negative"));
        (await batch.TryReserveSpendAsync(int.MaxValue, 0m, "missing-account")).Should().BeFalse();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        (await redis.GetDatabase().HashLengthAsync($"spend_reservations:group:{account.GroupId}")).Should().Be(0);
        (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(0m);
    }

    [Fact(Timeout = 90_000)]
    public async Task ZeroEstimate_WithActualPaidUsage_SettlesOverEstimateAndDebitsOnce()
    {
        var account = await fixture.SeedAccountAsync();
        await using var batch = await fixture.StartBatchServiceAsync();
        const string requestId = "unexpected-paid-usage";
        (await batch.TryReserveSpendAsync(account.KeyId, 0m, requestId)).Should().BeTrue();
        (await batch.MarkSpendReservationInvocationStartedAsync(account.KeyId, requestId)).Should().BeTrue();
        (await batch.SettleSpendReservationAsync(account.KeyId, requestId, 0.05m)).Status
            .Should().Be(SpendReservationSettlementStatus.SettledOverEstimate);
        (await batch.SettleSpendReservationAsync(account.KeyId, requestId, 0.05m)).Status
            .Should().Be(SpendReservationSettlementStatus.AlreadySettled);
        (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(0.05m);
        (await batch.FlushPendingUpdatesAsync()).Should().Be(1);
        (await batch.FlushPendingUpdatesAsync()).Should().Be(0);
        await fixture.AssertAccountedForAsync(account.GroupId, 0.05m);
        await using var db = fixture.CreateDbContext();
        (await db.VirtualKeyGroups.SingleAsync(group => group.Id == account.GroupId)).Balance.Should().Be(99.95m);
    }

    [Fact(Timeout = 90_000)]
    public async Task ZeroHold_WithUnresolvedUsage_RemainsRecoverableUntilAuditedResolution()
    {
        var account = await fixture.SeedAccountAsync();
        await using var batch = await fixture.StartBatchServiceAsync();
        const string requestId = "unresolved-free-request";
        (await batch.TryReserveSpendAsync(account.KeyId, 0m, requestId)).Should().BeTrue();
        (await batch.MarkSpendReservationInvocationStartedAsync(account.KeyId, requestId)).Should().BeTrue();
        (await batch.RecordIndeterminateSpendReservationAsync(account.KeyId, requestId, 0m, "missing usage"))
            .Status.Should().Be(SpendReservationSettlementStatus.Settled);
        (await batch.RecordIndeterminateSpendReservationAsync(account.KeyId, requestId, 0m, "replay"))
            .Status.Should().Be(SpendReservationSettlementStatus.AlreadySettled);
        await batch.ReleaseSpendReservationAsync(account.KeyId, requestId);
        var evidence = await batch.GetSpendReservationReconciliationAsync(account.KeyId, requestId);
        evidence.Should().NotBeNull();
        evidence!.HeldAmount.Should().Be(0m);
        evidence.Resolved.Should().BeFalse();
        (await batch.SettleSpendReservationAsync(account.KeyId, requestId, 0m)).Status
            .Should().Be(SpendReservationSettlementStatus.Conflict);
        (await batch.ReconcileSpendReservationAsync(account.KeyId, requestId, 0m,
            SpendReconciliationDisposition.ProviderCostWaived, "operator", "free model verified"))
            .Status.Should().Be(SpendReservationSettlementStatus.Settled);
        (await batch.ReconcileSpendReservationAsync(account.KeyId, requestId, 0m,
            SpendReconciliationDisposition.ProviderCostWaived, "operator", "free model verified"))
            .Status.Should().Be(SpendReservationSettlementStatus.AlreadySettled);
        (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(0m);
        (await batch.FlushPendingUpdatesAsync()).Should().Be(0);
        await fixture.AssertAccountedForAsync(account.GroupId, 0m);
    }

    private static Usage FreeUsage() => new() { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 };

    private static async IAsyncEnumerable<ChatCompletionChunk> FreeStream()
    {
        yield return new ChatCompletionChunk
        {
            Id = "free-response", Model = "free-model", Object = "chat.completion.chunk", Usage = FreeUsage(),
            Choices = [new StreamingChoice
            {
                Index = 0, FinishReason = FinishReason.Stop, Delta = new DeltaContent { Content = "free answer" }
            }]
        };
        await Task.CompletedTask;
    }
}
