using System.Runtime.CompilerServices;
using System.Text;
using AwesomeAssertions;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Gateway.Billing;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.UsageTracking;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;

namespace ConduitLLM.FaultInjectionTests;

[Collection(BillingFaultCollection.Name)]
public sealed class StreamingReservationReconciliationTests(BillingFaultFixture fixture)
{
    [Theory(Timeout = 90_000)]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task MissingProviderUsage_FailedEstimate_PreservesKnownCostAndUnresolvedHold(
        int notificationFailure, bool estimateTimeout)
    {
        var account = await fixture.SeedAccountAsync();
        await using var batch = await fixture.StartBatchServiceAsync();
        var reservation = new SpendReservationService(batch, NullLogger<SpendReservationService>.Instance);
        using var services = new ServiceCollection().AddSingleton<ISpendReservationService>(reservation)
            .BuildServiceProvider();
        using var aborted = new CancellationTokenSource();
        using var responseStream = new NotificationStream(notificationFailure == 1);
        var context = new DefaultHttpContext { RequestServices = services, RequestAborted = aborted.Token };
        context.Request.Path = "/v1/conduit/chat/completions";
        context.Response.Body = responseStream;
        context.Items["VirtualKeyId"] = account.KeyId;
        context.Items["ProviderType"] = "OpenAI";

        var client = new Mock<ILLMClient>();
        client.SetupSequence(x => x.StreamChatCompletionAsync(
                It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()))
            .Returns(FunctionStream())
            .Returns(InterruptedStream(aborted));
        var factory = new Mock<ILLMClientFactory>();
        factory.Setup(x => x.GetClientAsync("fault-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        var discovery = new Mock<IFunctionDiscoveryService>();
        discovery.Setup(x => x.GetToolsForFunctionConfigurationsAsync(
                It.IsAny<List<int>>(), account.KeyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        discovery.Setup(x => x.GetFunctionNameToIdMappingAsync(
                It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, FunctionRoute> { ["lookup"] = new(1) });
        var orchestration = new Mock<IAgenticOrchestrationService>();
        orchestration.Setup(x => x.ExecuteToolCallsAsync(
                It.IsAny<List<ToolCall>>(), account.KeyId, It.IsAny<Dictionary<string, FunctionRoute>>(),
                It.IsAny<string>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (notificationFailure == 2) aborted.Cancel();
                return new AgenticExecutionResult
                {
                    AllSucceeded = true, TotalFunctionCost = 0.05m,
                    FunctionCallSummaries = [new FunctionCallSummary
                    {
                        Iteration = 1, FunctionName = "lookup", ToolCallId = "call-1",
                        Success = true, Cost = 0.05m, FunctionExecutionId = Guid.NewGuid()
                    }],
                    ToolResultMessages = [new Message { Role = "tool", Content = "found", ToolCallId = "call-1" }]
                };
            });
        var configs = new Mock<IFunctionConfigurationRepository>();
        configs.Setup(x => x.GetByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new FunctionConfiguration { Id = 1, ConfigurationName = "lookup", IsEnabled = true }]);
        var settings = new Mock<IGlobalSettingsCacheService>();
        settings.Setup(x => x.GetMaxAgenticIterationsAsync()).ReturnsAsync(5);
        settings.Setup(x => x.GetMinAgenticIterationsAsync()).ReturnsAsync(1);
        var maximumSpend = new Mock<IChatSpendEstimator>();
        maximumSpend.Setup(x => x.EstimateMaximumCostAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatSpendEstimate(true, 0.25m, 1, 1, null));
        var estimator = new Mock<IUsageEstimationService>();
        var estimate = estimator.Setup(x => x.EstimateUsageFromStreamingResponseAsync(
            "fault-model", It.IsAny<List<Message>>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CancellationToken>()));
        if (estimateTimeout)
            // Deliberately ignore cancellation: the endpoint must enforce its own time budget.
            estimate.Returns(new TaskCompletionSource<Usage>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        else
            estimate.ThrowsAsync(new InvalidOperationException("estimation unavailable"));

        var endpoint = new ChatEndpoints(
            new Conduit(factory.Object, NullLogger<Conduit>.Instance,
                functionDiscoveryService: discovery.Object, agenticOrchestrationService: orchestration.Object),
            NullLogger<ChatEndpoints>.Instance, Mock.Of<IModelProviderMappingService>(),
            GatewayJsonOptions.Create(), Mock.Of<IEventPublisher>(), settings.Object, estimator.Object,
            configs.Object, Options.Create(new UsageTrackingOptions { AccountingFinalizationTimeoutSeconds = 1 }),
            maximumSpend.Object, reservation,
            Options.Create(new BillingAdmissionOptions { Mode = BillingAdmissionMode.Enforce }),
            new HttpContextAccessor { HttpContext = context });
        var middleware = new UsageTrackingMiddleware(async ctx =>
            await endpoint.CreateChatCompletion(new ChatCompletionRequest
            {
                Model = "fault-model", Stream = true, MaxAgenticIterations = 3,
                EnableAgenticMode = true, FunctionConfigurationIds = [1],
                Messages = [new Message { Role = "user", Content = "look up" }]
            }, ctx.RequestAborted), NullLogger<UsageTrackingMiddleware>.Instance);

        await middleware.InvokeAsync(context, Mock.Of<ICostCalculationService>(), batch,
            Mock.Of<IRequestLogRuntimeWriter>(), Mock.Of<IVirtualKeyRuntimeService>(),
            Mock.Of<IBillingAuditService>(), Mock.Of<IToolCostCalculationService>());

        var snapshot = context.GetRequestAccountingSnapshot()!;
        snapshot.ProviderUsage.Should().BeNull();
        snapshot.FunctionExecutionCost.Should().Be(0.05m);
        snapshot.IsIndeterminate.Should().BeTrue();
        snapshot.Reservation!.Closed.Should().BeFalse();
        snapshot.ProviderInvocations.Should().Equal(notificationFailure == 2 ? [1] : [1, 2]);
        snapshot.StreamingProviderInvocations.Should().Equal(notificationFailure == 2 ? [1] : [1, 2]);
        snapshot.CompletedStreamingProviderInvocations.Should().Equal([1]);
        snapshot.HasIncompleteProviderUsage.Should().BeTrue();
        var evidence = await batch.GetSpendReservationReconciliationAsync(account.KeyId, snapshot.BillingRequestId);
        evidence.Should().NotBeNull();
        evidence!.KnownAmount.Should().Be(0.05m);
        evidence.HeldAmount.Should().Be(0.20m);
        evidence.Resolved.Should().BeFalse();
        (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(0.25m);
        (await reservation.RecordKnownIndeterminateSpendAsync(
            account.KeyId, snapshot.BillingRequestId, 0.05m, "replay")).Status
            .Should().Be(SpendReservationSettlementStatus.AlreadySettled);
        (await reservation.SettleAsync(account.KeyId, snapshot.BillingRequestId, 0.05m)).Status
            .Should().Be(SpendReservationSettlementStatus.Conflict);
        await fixture.AssertAccountedForAsync(account.GroupId, 0.05m);
        await batch.FlushPendingUpdatesAsync();
        await batch.FlushPendingUpdatesAsync();
        await fixture.AssertAccountedForAsync(account.GroupId, 0.05m);
        await using var db = fixture.CreateDbContext();
        (await db.VirtualKeyGroups.SingleAsync(x => x.Id == account.GroupId)).Balance.Should().Be(99.95m);
        (await db.VirtualKeyGroupTransactions.CountAsync(x => x.VirtualKeyGroupId == account.GroupId &&
            x.TransactionType == TransactionType.Debit)).Should().Be(1);
        estimator.Verify(x => x.EstimateUsageFromStreamingResponseAsync(
            "fault-model", It.IsAny<List<Message>>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<Tool>?>(), It.IsAny<CancellationToken>()), notificationFailure == 2 ? Times.Once() : Times.Never());
        orchestration.Verify(x => x.ExecuteToolCallsAsync(
            It.IsAny<List<ToolCall>>(), account.KeyId, It.IsAny<Dictionary<string, FunctionRoute>>(),
            It.IsAny<string>(), It.IsAny<Guid>(), 1, It.IsAny<CancellationToken>()), Times.Once);
        if (notificationFailure == 0) responseStream.CompletedNotifications.Should().Be(1);
        if (notificationFailure == 1) responseStream.FailedNotifications.Should().Be(1);
    }

    [Theory(Timeout = 90_000)]
    [InlineData(0.07, SpendReconciliationDisposition.AuthoritativeProviderCost)]
    [InlineData(0.0, SpendReconciliationDisposition.ProviderCostWaived)]
    public async Task IndeterminateReservation_ServiceRecovery_AuditedResolutionReleasesHoldOnce(
        double confirmedProviderCost, SpendReconciliationDisposition disposition)
    {
        var providerCost = (decimal)confirmedProviderCost;
        var account = await fixture.SeedAccountAsync();
        var requestId = Guid.NewGuid().ToString("N");
        var billedAt = DateTime.UtcNow.AddDays(-1).Date.AddHours(23).AddMinutes(59);
        await using (var initial = await fixture.StartBatchServiceAsync())
        {
            (await initial.TryReserveSpendAsync(account.KeyId, 0.25m, requestId)).Should().BeTrue();
            (await initial.MarkSpendReservationInvocationStartedAsync(account.KeyId, requestId)).Should().BeTrue();
            await initial.RecordIndeterminateSpendReservationAsync(account.KeyId, requestId, 0.05m, "usage missing", billedAt);
            await initial.FlushPendingUpdatesAsync();
        }

        await using var recovered = await fixture.StartBatchServiceAsync();
        var evidence = await recovered.GetSpendReservationReconciliationAsync(account.KeyId, requestId);
        evidence!.HeldAmount.Should().Be(0.20m);
        evidence.BillingWindowStartUtc.Should().Be(billedAt.Date.AddHours(23));
        (await recovered.RecordIndeterminateSpendReservationAsync(
            account.KeyId, requestId, 0.05m, "replay", DateTime.UtcNow)).Status
            .Should().Be(SpendReservationSettlementStatus.AlreadySettled);
        await recovered.ReleaseSpendReservationAsync(account.KeyId, requestId);
        (await recovered.GetPendingSpendAsync(account.KeyId)).Should().Be(0.20m);

        (await recovered.ReconcileSpendReservationAsync(
            account.KeyId, requestId, providerCost, disposition, "operator-1", "provider statement reviewed")).Status
            .Should().Be(SpendReservationSettlementStatus.Settled);
        (await recovered.ReconcileSpendReservationAsync(
            account.KeyId, requestId, providerCost, disposition, "operator-1", "provider statement reviewed")).Status
            .Should().Be(SpendReservationSettlementStatus.AlreadySettled);
        (await recovered.ReconcileSpendReservationAsync(
            account.KeyId, requestId, providerCost + 0.01m,
            SpendReconciliationDisposition.AuthoritativeProviderCost, "operator-1", "conflicting amount")).Status
            .Should().Be(SpendReservationSettlementStatus.Conflict);
        (await recovered.RecordIndeterminateSpendReservationAsync(
            account.KeyId, requestId, 0.05m, "late finalizer")).Status
            .Should().Be(SpendReservationSettlementStatus.AlreadySettled);
        await recovered.FlushPendingUpdatesAsync();
        await fixture.AssertAccountedForAsync(account.GroupId, 0.05m + providerCost);
        (await recovered.GetPendingSpendAsync(account.KeyId)).Should().Be(0m);
        evidence = await recovered.GetSpendReservationReconciliationAsync(account.KeyId, requestId);
        evidence!.Resolved.Should().BeTrue();
        evidence.HeldAmount.Should().Be(0m);
        evidence.ProviderAmount.Should().Be(providerCost);
        evidence.AuditActor.Should().Be("operator-1");
        evidence.AuditNote.Should().Be("provider statement reviewed");
        await using var db = fixture.CreateDbContext();
        var debits = await db.VirtualKeyGroupTransactions.Where(x => x.VirtualKeyGroupId == account.GroupId &&
            x.TransactionType == TransactionType.Debit).ToListAsync();
        debits.Sum(x => x.Amount).Should().Be(0.05m + providerCost);
        debits.Should().OnlyContain(x => x.BillingWindowStartUtc == billedAt.Date.AddHours(23));
    }

    [Fact(Timeout = 90_000)]
    public async Task Reconciliation_SiblingKeyInSameGroup_CannotMutateOrReadRequest()
    {
        var account = await fixture.SeedAccountAsync();
        await using var db = fixture.CreateDbContext();
        var sibling = new VirtualKey
        {
            KeyName = "sibling", KeyHash = Guid.NewGuid().ToString("N"), VirtualKeyGroupId = account.GroupId
        };
        db.Add(sibling);
        await db.SaveChangesAsync();
        await using var batch = await fixture.StartBatchServiceAsync();
        var request = Guid.NewGuid().ToString("N");
        await batch.TryReserveSpendAsync(account.KeyId, 0.25m, request);
        await batch.MarkSpendReservationInvocationStartedAsync(account.KeyId, request);
        await batch.RecordIndeterminateSpendReservationAsync(account.KeyId, request, 0.05m, "missing usage");

        (await batch.GetSpendReservationReconciliationAsync(sibling.Id, request)).Should().BeNull();
        (await batch.RecordIndeterminateSpendReservationAsync(sibling.Id, request, 0.06m, "wrong key")).Status
            .Should().Be(SpendReservationSettlementStatus.Conflict);
        (await batch.ReconcileSpendReservationAsync(sibling.Id, request, 0.01m,
            SpendReconciliationDisposition.AuthoritativeProviderCost, "operator", "wrong key")).Status
            .Should().Be(SpendReservationSettlementStatus.Conflict);
        var evidence = await batch.GetSpendReservationReconciliationAsync(account.KeyId, request);
        evidence!.KnownAmount.Should().Be(0.05m);
        evidence.HeldAmount.Should().Be(0.20m);
        evidence.Resolved.Should().BeFalse();
        await fixture.AssertAccountedForAsync(account.GroupId, 0.05m);
    }

    [Fact(Timeout = 90_000)]
    public async Task ConcurrentKnownUpdateAndReconciliation_SettledRecordMatchesAcceptedCharges()
    {
        var account = await fixture.SeedAccountAsync();
        await using var batch = await fixture.StartBatchServiceAsync();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        decimal expected = 0;
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var request = Guid.NewGuid().ToString("N");
            await batch.TryReserveSpendAsync(account.KeyId, 0.25m, request);
            await batch.MarkSpendReservationInvocationStartedAsync(account.KeyId, request);
            await batch.RecordIndeterminateSpendReservationAsync(account.KeyId, request, 0.05m, "missing usage");
            var update = batch.RecordIndeterminateSpendReservationAsync(account.KeyId, request, 0.10m, "late known cost");
            var resolve = batch.ReconcileSpendReservationAsync(account.KeyId, request, 0.07m,
                SpendReconciliationDisposition.AuthoritativeProviderCost, "operator", "confirmed statement");
            await Task.WhenAll(update, resolve);
            if ((await resolve).Status == SpendReservationSettlementStatus.Conflict)
                (await batch.ReconcileSpendReservationAsync(account.KeyId, request, 0.07m,
                    SpendReconciliationDisposition.AuthoritativeProviderCost, "operator", "confirmed statement")).Status
                    .Should().Be(SpendReservationSettlementStatus.Settled);
            var record = await batch.GetSpendReservationReconciliationAsync(account.KeyId, request);
            record!.Resolved.Should().BeTrue();
            var total = record.KnownAmount + record.ProviderAmount!.Value;
            var settled = await redis.GetDatabase().HashGetAsync($"spend_reservations_settled:group:{account.GroupId}", request);
            long.Parse(settled.ToString(), System.Globalization.CultureInfo.InvariantCulture).Should().Be((long)(total * 100_000_000m));
            expected += total;
        }
        await batch.FlushPendingUpdatesAsync();
        await fixture.AssertAccountedForAsync(account.GroupId, expected);
        (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(0m);
    }

    [Theory(Timeout = 90_000)]
    [InlineData("0.250000005", "0.05")]
    [InlineData("2000000", "1234567.89012345")]
    public async Task PartialSettlement_PreciseHoldAndLargeKnownUnits_PreservesExactEvidence(
        string reservationAmount, string knownAmount)
    {
        var held = decimal.Parse(reservationAmount, System.Globalization.CultureInfo.InvariantCulture);
        var known = decimal.Parse(knownAmount, System.Globalization.CultureInfo.InvariantCulture);
        var account = await fixture.SeedAccountAsync(3000000m);
        await using var batch = await fixture.StartBatchServiceAsync();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var request = Guid.NewGuid().ToString("N");
        await batch.TryReserveSpendAsync(account.KeyId, held, request);
        await batch.MarkSpendReservationInvocationStartedAsync(account.KeyId, request);
        var initialHeld = await redis.GetDatabase().HashGetAsync($"spend_reservations_started:group:{account.GroupId}", request);
        var actualHeld = decimal.Parse(initialHeld.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture);

        (await batch.RecordIndeterminateSpendReservationAsync(account.KeyId, request, known, "missing usage")).Status
            .Should().Be(SpendReservationSettlementStatus.Settled);
        var evidence = await batch.GetSpendReservationReconciliationAsync(account.KeyId, request);
        evidence!.KnownAmount.Should().Be(known);
        evidence.HeldAmount.Should().Be(actualHeld - known);
        var started = await redis.GetDatabase().HashGetAsync($"spend_reservations_started:group:{account.GroupId}", request);
        decimal.Parse(started.ToString(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(evidence.HeldAmount);
        (await batch.RecordIndeterminateSpendReservationAsync(account.KeyId, request, known, "replay")).Status
            .Should().Be(SpendReservationSettlementStatus.AlreadySettled);
        await batch.ReconcileSpendReservationAsync(account.KeyId, request, 0m,
            SpendReconciliationDisposition.ProviderCostWaived, "operator", "unavailable statement");
        var settled = await redis.GetDatabase().HashGetAsync($"spend_reservations_settled:group:{account.GroupId}", request);
        long.Parse(settled.ToString(), System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be((long)(known * 100_000_000m));
        (await redis.GetDatabase().HashExistsAsync($"spend_reservations_started:group:{account.GroupId}", request)).Should().BeFalse();
        await batch.FlushPendingUpdatesAsync();
        await fixture.AssertAccountedForAsync(account.GroupId, known);
    }

    [Fact(Timeout = 90_000)]
    public async Task Reconciliation_KnownAmountChangesAfterRead_RejectsStaleTotalAndFreshRetrySucceeds()
    {
        var account = await fixture.SeedAccountAsync();
        await using var batch = await fixture.StartBatchServiceAsync();
        var request = Guid.NewGuid().ToString("N");
        await batch.TryReserveSpendAsync(account.KeyId, 0.25m, request);
        await batch.MarkSpendReservationInvocationStartedAsync(account.KeyId, request);
        await batch.RecordIndeterminateSpendReservationAsync(account.KeyId, request, 0.05m, "missing usage");
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        var realDb = redis.GetDatabase();
        var proxyDb = new Mock<IDatabase>();
        proxyDb.Setup(x => x.HashGetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue field, CommandFlags flags) => realDb.HashGetAsync(key, field, flags));
        var mutateOnce = true;
        proxyDb.Setup(x => x.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns(async (string script, RedisKey[] keys, RedisValue[] values, CommandFlags flags) =>
            {
                if (mutateOnce)
                {
                    mutateOnce = false;
                    // This separate real batch connection commits AFTER the reconciler's
                    // snapshot read and BEFORE its Lua transition, making the race deterministic.
                    await batch.RecordIndeterminateSpendReservationAsync(account.KeyId, request, 0.10m, "late known cost");
                }
                return await realDb.ScriptEvaluateAsync(script, keys, values, flags);
            });
        var connection = new Mock<IConnectionMultiplexer>();
        connection.Setup(x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(proxyDb.Object);
        var factory = new Mock<RedisConnectionFactory>(Options.Create(new CacheOptions()),
            NullLogger<RedisConnectionFactory>.Instance);
        factory.Setup(x => x.GetConnectionAsync()).ReturnsAsync(connection.Object);
        var store = new Mock<IVirtualKeyRuntimeStore>();
        store.Setup(x => x.GetByIdAsync(account.KeyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VirtualKeyRuntimeRecord
            {
                Id = account.KeyId, VirtualKeyGroupId = account.GroupId,
                Group = new VirtualKeyGroupRuntimeRecord { Id = account.GroupId, Balance = 100m }
            });
        using var reconciler = new BatchSpendUpdateService(Mock.Of<IServiceScopeFactory>(), factory.Object,
            Options.Create(new BatchSpendingOptions { FlushIntervalSeconds = 3600, MaximumIntervalSeconds = 7200 }),
            NullLogger<BatchSpendUpdateService>.Instance, Mock.Of<IBillingAlertingService>(), runtimeStore: store.Object);

        (await reconciler.ReconcileSpendReservationAsync(account.KeyId, request, 0.07m,
            SpendReconciliationDisposition.AuthoritativeProviderCost, "operator", "confirmed statement")).Status
            .Should().Be(SpendReservationSettlementStatus.Conflict);
        var record = await batch.GetSpendReservationReconciliationAsync(account.KeyId, request);
        record!.KnownAmount.Should().Be(0.10m);
        record.Resolved.Should().BeFalse();
        await fixture.AssertAccountedForAsync(account.GroupId, 0.10m);
        (await reconciler.ReconcileSpendReservationAsync(account.KeyId, request, 0.07m,
            SpendReconciliationDisposition.AuthoritativeProviderCost, "operator", "confirmed statement")).Status
            .Should().Be(SpendReservationSettlementStatus.Settled);
        var settled = await realDb.HashGetAsync($"spend_reservations_settled:group:{account.GroupId}", request);
        settled.ToString().Should().Be("17000000");
        await batch.FlushPendingUpdatesAsync();
        await fixture.AssertAccountedForAsync(account.GroupId, 0.17m);
        (await batch.GetPendingSpendAsync(account.KeyId)).Should().Be(0m);
    }

    private static async IAsyncEnumerable<ChatCompletionChunk> FunctionStream()
    {
        yield return new ChatCompletionChunk
        {
            Model = "fault-model", Choices = [new StreamingChoice
            {
                Index = 0, FinishReason = "tool_calls", Delta = new DeltaContent
                {
                    ToolCalls = [new ToolCallChunk { Index = 0, Id = "call-1", Type = "function",
                        Function = new FunctionCallChunk { Name = "lookup", Arguments = "{}" } }]
                }
            }]
        };
        await Task.Yield();
    }

    private static async IAsyncEnumerable<ChatCompletionChunk> InterruptedStream(
        CancellationTokenSource aborted, [EnumeratorCancellation] CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        yield return new ChatCompletionChunk { Model = "fault-model", Choices = [new StreamingChoice
            { Index = 0, Delta = new DeltaContent { Content = "partial" } }] };
        await Task.Yield();
        aborted.Cancel();
        throw new OperationCanceledException(aborted.Token);
    }

    private sealed class NotificationStream(bool failCompleted) : MemoryStream
    {
        public int CompletedNotifications { get; private set; }
        public int FailedNotifications { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Encoding.UTF8.GetString(buffer.Span).Contains("\"status\":\"completed\"", StringComparison.Ordinal))
            {
                if (failCompleted)
                {
                    FailedNotifications++;
                    return ValueTask.FromException(new IOException("function notification failed"));
                }
                CompletedNotifications++;
            }
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
