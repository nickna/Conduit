using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Billing;
using ConduitLLM.Gateway.Constants;
using ConduitLLM.Gateway.Middleware;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Gateway.UsageTracking;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;
using Npgsql;
using StackExchange.Redis;

namespace ConduitLLM.FaultInjectionTests;

[Collection(BillingFaultCollection.Name)]
public sealed class PartialPricingReservationTests(BillingFaultFixture fixture)
{
    private const decimal ReservedAmount = 0.25m;
    private static readonly DateTime BilledAt = new(2026, 7, 23, 23, 59, 59, DateTimeKind.Utc);
    private static readonly DateTime OriginalWindow = BilledAt.Date.AddHours(23);

    public static IEnumerable<object[]> PricingFailures()
    {
        foreach (var streaming in new[] { false, true })
        {
            yield return [streaming, "missing", "none", 0m, 0m, 0.05m];
            yield return [streaming, "mixed", "none", 0.05m, 0m, 0.05m];
            yield return [streaming, "mixed", "configured", 0.05m, 0.03m, 0.05m];
            yield return [streaming, "complete", "partial", 0.06m, 0.03m, 0.05m];
            yield return [streaming, "complete", "missing", 0.06m, 0m, 0.05m];
            yield return [streaming, "missing", "configured", 0m, 0.03m, 0.05m];
            yield return [streaming, "missing", "missing", 0m, 0m, 0m];
        }
    }

    [Theory(Timeout = 90_000)]
    [MemberData(nameof(PricingFailures))]
    public async Task PartialPricing_PreservesKnownChargesAndOpenHoldAcrossReplayAndFlush(
        bool streaming, string providerPrices, string toolPrices,
        decimal knownProviderCost, decimal knownToolCost, decimal functionCost)
    {
        var account = await fixture.SeedAccountAsync();
        await using var dataSource = NpgsqlDataSource.Create(fixture.PostgreSqlConnectionString);
        using var redisFactory = CreateRedisFactory();
        await using var batch = await StartProductionBatchAsync(dataSource, redisFactory);
        var reservation = new SpendReservationService(batch, NullLogger<SpendReservationService>.Instance);
        using var services = new ServiceCollection().AddSingleton<ISpendReservationService>(reservation)
            .BuildServiceProvider();
        var context = Context(services, account.KeyId, streaming);
        var accounting = context.GetOrCreateRequestAccountingContext();
        accounting.SetOperation(RequestOperation.ChatCompletion, account.KeyId, "partial-price-model");
        Assert.Equal(SpendReservationOutcome.Reserved,
            (await reservation.ReserveAsync(account.KeyId, accounting.BillingRequestId, ReservedAmount)).Outcome);
        accounting.RecordReservation(ReservedAmount);
        Assert.True(await reservation.MarkInvocationStartedAsync(account.KeyId, accounting.BillingRequestId));
        accounting.MarkInvocationStarted();

        var pricing = CreateProviderPricing(out var prices);
        var tools = await CreateToolPricingAsync(account.KeyId, toolPrices);
        var logs = new List<LogRequestDto>();
        var audits = new List<BillingAuditEvent>();
        var writer = RequestWriter(logs);
        var audit = AuditWriter(audits);
        var middleware = new UsageTrackingMiddleware(ctx =>
        {
            PublishEvidence(ctx, providerPrices, tools.Usage, functionCost);
            return Task.CompletedTask;
        }, NullLogger<UsageTrackingMiddleware>.Instance);

        await middleware.InvokeAsync(context, pricing, batch, writer.Object,
            Mock.Of<IVirtualKeyRuntimeService>(), audit.Object, tools.Service);

        var knownCost = knownProviderCost + knownToolCost + functionCost;
        var snapshot = accounting.Snapshot();
        Assert.True(snapshot.IsIndeterminate);
        Assert.False(snapshot.Reservation!.Closed);
        Assert.True(snapshot.Reservation.InvocationStarted);
        Assert.Equal(knownProviderCost, snapshot.ProviderPricing!.KnownCost);
        Assert.Equal(providerPrices == "complete" ? knownProviderCost : (decimal?)null,
            snapshot.ProviderPricing.Cost);
        if (toolPrices != "none")
        {
            Assert.Equal(knownToolCost, snapshot.ProviderToolPricing!.KnownCost);
            Assert.Equal(toolPrices == "configured" ? knownToolCost : (decimal?)null,
                snapshot.ProviderToolPricing.Cost);
        }
        Assert.Equal(functionCost, snapshot.FunctionExecutionCost);
        Assert.Equal(knownCost, Assert.Single(logs).Cost);
        if (providerPrices != "complete")
            Assert.Contains(audits, item => item.EventType == BillingAuditEventType.PricingCalculationFailed);
        var expectedMissingCalls = providerPrices switch { "mixed" => 1, "missing" => 3, _ => 0 };
        prices.Verify(service => service.GetCostByIdAsync(17, It.IsAny<CancellationToken>()),
            Times.Exactly(expectedMissingCalls));

        var evidence = await batch.GetSpendReservationReconciliationAsync(account.KeyId, snapshot.BillingRequestId);
        Assert.NotNull(evidence);
        Assert.Equal(knownCost, evidence.KnownAmount);
        Assert.Equal(ReservedAmount - knownCost, evidence.HeldAmount);
        Assert.False(evidence.Resolved);
        Assert.Equal(OriginalWindow, evidence.BillingWindowStartUtc);
        Assert.Equal(ReservedAmount, await batch.GetPendingSpendAsync(account.KeyId));
        await fixture.AssertAccountedForAsync(account.GroupId, knownCost);

        // A second finalizer may arrive in a later hour; neither accepted cost nor attribution changes.
        Assert.Equal(SpendReservationSettlementStatus.AlreadySettled,
            (await reservation.RecordKnownIndeterminateSpendAsync(account.KeyId, snapshot.BillingRequestId,
                knownCost, "same confirmed components replayed", BilledAt.AddDays(1))).Status);
        Assert.True(await batch.TryReserveSpendAsync(account.KeyId, ReservedAmount, snapshot.BillingRequestId));
        Assert.Equal(SpendReservationSettlementStatus.Conflict,
            (await reservation.SettleAsync(account.KeyId, snapshot.BillingRequestId, knownCost)).Status);
        await batch.ReleaseSpendReservationAsync(account.KeyId, snapshot.BillingRequestId);
        Assert.Equal(ReservedAmount, await batch.GetPendingSpendAsync(account.KeyId));

        await batch.FlushPendingUpdatesAsync();
        await batch.FlushPendingUpdatesAsync();
        await fixture.AssertAccountedForAsync(account.GroupId, knownCost);
        Assert.Equal(ReservedAmount - knownCost, await batch.GetPendingSpendAsync(account.KeyId));
        await AssertLedgerAsync(account.GroupId, knownCost);

        // The unresolved request and reduced hold survive a fresh service instance after known spend commits.
        await using var recovered = fixture.CreateBatchService();
        var recoveredEvidence = await recovered.GetSpendReservationReconciliationAsync(account.KeyId, snapshot.BillingRequestId);
        Assert.Equal(evidence, recoveredEvidence);
        Assert.Equal(SpendReservationSettlementStatus.AlreadySettled,
            (await recovered.RecordIndeterminateSpendReservationAsync(account.KeyId, snapshot.BillingRequestId,
                knownCost, "restarted finalizer", BilledAt.AddDays(2))).Status);
        await recovered.FlushPendingUpdatesAsync();
        await AssertLedgerAsync(account.GroupId, knownCost);
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        Assert.False(await redis.GetDatabase().HashExistsAsync(
            $"spend_reservations_settled:group:{account.GroupId}", snapshot.BillingRequestId));
        await batch.StopAsync(CancellationToken.None);
    }

    [Theory(Timeout = 90_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialPricing_WithoutReservation_QueuesKnownComponentsInOriginalWindow(bool streaming)
    {
        var account = await fixture.SeedAccountAsync();
        await using var dataSource = NpgsqlDataSource.Create(fixture.PostgreSqlConnectionString);
        using var redisFactory = CreateRedisFactory();
        await using var batch = await StartProductionBatchAsync(dataSource, redisFactory);
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = Context(services, account.KeyId, streaming);
        var pricing = CreateProviderPricing(out _);
        var tools = await CreateToolPricingAsync(account.KeyId, "partial");
        var middleware = new UsageTrackingMiddleware(ctx =>
        {
            ctx.GetOrCreateRequestAccountingContext().SetOperation(
                RequestOperation.ChatCompletion, account.KeyId, "partial-price-model");
            PublishEvidence(ctx, "mixed", tools.Usage, 0.05m);
            return Task.CompletedTask;
        }, NullLogger<UsageTrackingMiddleware>.Instance);

        await middleware.InvokeAsync(context, pricing, batch, Mock.Of<IRequestLogRuntimeWriter>(),
            Mock.Of<IVirtualKeyRuntimeService>(), Mock.Of<IBillingAuditService>(), tools.Service);

        Assert.True(context.GetRequestAccountingSnapshot()!.IsIndeterminate);
        Assert.Null(context.GetRequestAccountingSnapshot()!.Reservation);
        Assert.Equal(0.13m, await batch.GetPendingSpendAsync(account.KeyId));
        await fixture.AssertAccountedForAsync(account.GroupId, 0.13m);
        await batch.FlushPendingUpdatesAsync();
        await batch.FlushPendingUpdatesAsync();
        await AssertLedgerAsync(account.GroupId, 0.13m);
        Assert.Equal(0m, await batch.GetPendingSpendAsync(account.KeyId));
        await batch.StopAsync(CancellationToken.None);
    }

    [Fact(Timeout = 90_000)]
    public async Task EmptyStream_BeforeInvocation_ReleasesUnstartedReservationWithoutReconciliation()
    {
        var account = await fixture.SeedAccountAsync();
        await using var dataSource = NpgsqlDataSource.Create(fixture.PostgreSqlConnectionString);
        using var redisFactory = CreateRedisFactory();
        await using var batch = await StartProductionBatchAsync(dataSource, redisFactory);
        var reservation = new SpendReservationService(batch, NullLogger<SpendReservationService>.Instance);
        using var services = new ServiceCollection().AddSingleton<ISpendReservationService>(reservation)
            .BuildServiceProvider();
        var context = Context(services, account.KeyId, streaming: true);
        var accounting = context.GetOrCreateRequestAccountingContext();
        accounting.SetOperation(RequestOperation.ChatCompletion, account.KeyId, "partial-price-model");
        Assert.Equal(SpendReservationOutcome.Reserved,
            (await reservation.ReserveAsync(account.KeyId, accounting.BillingRequestId, ReservedAmount)).Outcome);
        accounting.RecordReservation(ReservedAmount);
        var middleware = new UsageTrackingMiddleware(_ => Task.CompletedTask,
            NullLogger<UsageTrackingMiddleware>.Instance);

        await middleware.InvokeAsync(context, Mock.Of<ICostCalculationService>(), batch,
            Mock.Of<IRequestLogRuntimeWriter>(), Mock.Of<IVirtualKeyRuntimeService>(),
            Mock.Of<IBillingAuditService>(), Mock.Of<IToolCostCalculationService>());

        var snapshot = accounting.Snapshot();
        Assert.True(snapshot.Reservation!.Closed);
        Assert.False(snapshot.Reservation.InvocationStarted);
        Assert.False(snapshot.IsIndeterminate);
        Assert.Equal(0m, await batch.GetPendingSpendAsync(account.KeyId));
        Assert.Null(await batch.GetSpendReservationReconciliationAsync(account.KeyId, snapshot.BillingRequestId));
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnectionString);
        Assert.False(await redis.GetDatabase().HashExistsAsync(
            $"spend_reservations_indeterminate:group:{account.GroupId}", snapshot.BillingRequestId));
        await batch.FlushPendingUpdatesAsync();
        await AssertLedgerAsync(account.GroupId, 0m);
        await batch.StopAsync(CancellationToken.None);
    }

    private static DefaultHttpContext Context(IServiceProvider services, int keyId, bool streaming)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/v1/conduit/chat/completions";
        context.Response.ContentType = streaming ? "text/event-stream" : "application/json";
        context.Response.Body = new MemoryStream();
        context.Items[HttpContextKeys.VirtualKeyId] = keyId;
        context.Items["ProviderType"] = "OpenAI";
        context.Items[HttpContextKeys.ModelCostId] = 17;
        context.Items["Conduit.BillingOccurredAtUtc"] = BilledAt;
        context.Items[HttpContextKeys.ProviderBillingPolicy] = new ProviderCostBillingPolicy
        {
            TrustProviderReportedCost = true, MarkupMultiplier = 1m
        };
        return context;
    }

    private static void PublishEvidence(HttpContext context, string providerPrices,
        ProviderToolUsage? tools, decimal functionCost)
    {
        var accounting = context.GetOrCreateRequestAccountingContext();
        decimal?[] reportedCosts = providerPrices switch
        {
            "mixed" => [0.02m, null, 0.03m],
            "complete" => [0.02m, 0.01m, 0.03m],
            _ => [null, null, null]
        };
        accounting.RecordProviderUsage(new Usage { PromptTokens = 3, CompletionTokens = 3, TotalTokens = 6 },
            "partial-price-model", UsageEvidenceSource.Provider);
        accounting.RecordProviderCalls(reportedCosts.Select((cost, index) => new ProviderCallUsage
        {
            Iteration = index + 1,
            Usage = new Usage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2,
                ProviderReportedCostUsd = cost }
        }));
        if (functionCost > 0m)
            accounting.RecordFunctionExecutions([new ToolExecutionEvent
            {
                Status = "completed", Iteration = 1, FunctionName = "lookup", ToolCallId = "call-1",
                FunctionExecutionId = Guid.NewGuid(), Cost = functionCost
            }], functionCost);
        if (tools is not null) accounting.RecordProviderToolUsage(tools);
    }

    private static CostCalculationService CreateProviderPricing(out Mock<IModelCostService> prices)
    {
        prices = new Mock<IModelCostService>();
        prices.Setup(service => service.GetCostByIdAsync(17, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModelCost?)null);
        return new CostCalculationService(prices.Object, NullLogger<CostCalculationService>.Instance);
    }

    private async Task<(ToolCostCalculationService Service, ProviderToolUsage? Usage)> CreateToolPricingAsync(
        int keyId, string toolPrices)
    {
        var configuredName = $"configured-{keyId}";
        if (toolPrices is "configured" or "partial")
        {
            await using var db = fixture.CreateDbContext();
            db.ProviderTools.Add(new ProviderTool
            {
                Provider = ProviderType.OpenAI, ToolName = configuredName,
                BillingUnit = "requests", CostPerUnit = 0.03m, IsActive = true
            });
            await db.SaveChangesAsync();
        }
        var contextFactory = new Mock<IDbContextFactory<ConduitDbContext>>();
        contextFactory.Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => fixture.CreateDbContext());
        var pricing = new ToolCostCalculationService(contextFactory.Object,
            NullLogger<ToolCostCalculationService>.Instance);
        if (toolPrices == "none") return (pricing, null);
        var tools = new List<ProviderToolUsageItem>();
        if (toolPrices is "configured" or "partial")
            tools.Add(new ProviderToolUsageItem { ToolName = configuredName, Count = 1 });
        if (toolPrices is "missing" or "partial")
            tools.Add(new ProviderToolUsageItem { ToolName = $"missing-{keyId}", Count = 1 });
        return (pricing, new ProviderToolUsage { Tools = tools });
    }

    private async Task AssertLedgerAsync(int groupId, decimal knownCost)
    {
        await using var db = fixture.CreateDbContext();
        var group = await db.VirtualKeyGroups.SingleAsync(item => item.Id == groupId);
        Assert.Equal(100m - knownCost, group.Balance);
        Assert.Equal(knownCost, group.LifetimeSpent);
        var debits = await db.VirtualKeyGroupTransactions.Where(item => item.VirtualKeyGroupId == groupId &&
            item.TransactionType == TransactionType.Debit).ToListAsync();
        Assert.Equal(knownCost == 0m ? 0 : 1, debits.Count);
        Assert.Equal(knownCost, debits.Sum(item => item.Amount));
        Assert.All(debits, item =>
        {
            Assert.Equal(OriginalWindow, item.BillingWindowStartUtc);
            Assert.False(string.IsNullOrWhiteSpace(item.IdempotencyKey));
        });
    }

    private RedisConnectionFactory CreateRedisFactory() => new(
        Options.Create(new CacheOptions { RedisConnectionString = fixture.RedisConnectionString }),
        NullLogger<RedisConnectionFactory>.Instance);

    private static async Task<BatchSpendUpdateService> StartProductionBatchAsync(
        NpgsqlDataSource dataSource, RedisConnectionFactory redisFactory)
    {
        var startup = new StartupFlushLogger();
        var batch = new BatchSpendUpdateService(Mock.Of<IServiceScopeFactory>(), redisFactory,
            Options.Create(new BatchSpendingOptions { FlushIntervalSeconds = 3600, MaximumIntervalSeconds = 7200 }),
            startup, Mock.Of<IBillingAlertingService>(), runtimeStore: new NpgsqlVirtualKeyRuntimeStore(dataSource));
        try
        {
            await batch.StartAsync(CancellationToken.None);
            await startup.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(batch.IsHealthy);
            return batch;
        }
        catch
        {
            await batch.DisposeAsync();
            throw;
        }
    }

    private sealed class StartupFlushLogger : ILogger<BatchSpendUpdateService>
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (message == "No pending spend updates to flush" ||
                message.StartsWith("Flushed ", StringComparison.Ordinal) &&
                message.EndsWith("pending updates from previous session", StringComparison.Ordinal))
                Completed.TrySetResult();
            else if (message == "Error flushing pending updates on startup")
                Completed.TrySetException(exception ?? new InvalidOperationException(message));
        }
    }

    private static Mock<IRequestLogRuntimeWriter> RequestWriter(List<LogRequestDto> logs)
    {
        var writer = new Mock<IRequestLogRuntimeWriter>();
        writer.Setup(service => service.LogRequestAsync(It.IsAny<LogRequestDto>()))
            .Callback<LogRequestDto>(logs.Add).Returns(Task.CompletedTask);
        return writer;
    }

    private static Mock<IBillingAuditService> AuditWriter(List<BillingAuditEvent> audits)
    {
        var audit = new Mock<IBillingAuditService>();
        audit.Setup(service => service.LogBillingEvent(It.IsAny<BillingAuditEvent>()))
            .Callback<BillingAuditEvent>(audits.Add);
        return audit;
    }
}
