using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Serialization;
using ConduitLLM.Gateway.Services.SpendNotification;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Gateway.Services;

public class SpendNotificationShutdownTests
{
    public static IEnumerable<object[]> WorkCases() =>
        from work in new[] { "request", "record", "summary", "unusual", "periodic" }
        from stop in new[] { "normal", "deadline", "dispose-first" }
        select new object[] { work, stop };

    [Theory]
    [MemberData(nameof(WorkCases))]
    public async Task FinalShutdown_JoinsEveryOwnedWorkType_AndAdmitsNoWorkAfterward(string work, string stop)
    {
        var entered = Signal();
        var finished = Signal();
        var repository = new Mock<ISpendDataRepository>();
        var analyzer = new Mock<ISpendPatternAnalyzer>();
        var client = new Mock<IClientProxy>();
        if (work is "request" or "record")
            repository.Setup(value => value.RecordSpendingPatternAsync(It.IsAny<int>(), It.IsAny<decimal>(),
                    It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
                .Returns(() => { entered.TrySetResult(); return finished.Task; });
        if (work == "summary")
            client.Setup(value => value.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Returns(() => { entered.TrySetResult(); return finished.Task; });
        if (work == "unusual")
            analyzer.Setup(value => value.CheckUnusualSpendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(() => { entered.TrySetResult(); return finished.Task; });
        if (work == "periodic")
            analyzer.Setup(value => value.AnalyzeAllPatternsAsync(It.IsAny<CancellationToken>()))
                .Returns(() => { entered.TrySetResult(); return finished.Task; });
        using var service = Service(repository.Object, analyzer.Object, client.Object);
        await service.StartAsync(CancellationToken.None);
        Task? foreground = work switch
        {
            "request" => service.NotifySpendUpdateAsync(42, 1, 1, null, null, null),
            "summary" => service.SendSpendSummaryAsync(42, new SpendSummaryNotification()),
            "unusual" => service.CheckUnusualSpendingAsync(42),
            _ => null
        };
        if (work == "record") service.RecordSpend(42, 1);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var host = new CancellationTokenSource();
        if (stop == "deadline") host.Cancel();
        if (stop == "dispose-first") service.Dispose();
        var shutdown = service.ShutdownAsync(host.Token);
        Assert.Same(shutdown, service.ShutdownAsync(host.Token));
        Assert.False(shutdown.IsCompleted);
        repository.Verify(value => value.UnregisterInstanceAsync(It.IsAny<string>()), Times.Never());
        finished.SetResult();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        if (foreground is not null) await foreground;
        repository.Verify(value => value.UnregisterInstanceAsync(service.InstanceId), stop == "normal" ? Times.Once() : Times.Never());
        var calls = repository.Invocations.Count + analyzer.Invocations.Count + client.Invocations.Count;
        await service.NotifySpendUpdateAsync(42, 1, 1, null, null, null);
        service.RecordSpend(42, 1);
        await service.CheckUnusualSpendingAsync(42);
        await service.SendSpendSummaryAsync(42, new SpendSummaryNotification());
        await service.StartAsync(CancellationToken.None);
        service.Dispose();
        service.Dispose();
        await service.ShutdownAsync();
        Assert.Equal(calls, repository.Invocations.Count + analyzer.Invocations.Count + client.Invocations.Count);
    }

    [Fact]
    public async Task LeadershipLoss_StopsPeriodicGeneration_ButKeepsRequestsAndRestartAvailable()
    {
        var repository = new Mock<ISpendDataRepository>();
        using var service = Service(repository.Object);
        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);
        await service.NotifySpendUpdateAsync(42, 1, 1, null, null, null);
        await service.StartAsync(CancellationToken.None);
        await service.ShutdownAsync();
        repository.Verify(value => value.RecordSpendingPatternAsync(42, 1, 1, It.IsAny<CancellationToken>()), Times.Once());
        repository.Verify(value => value.RegisterInstanceAsync(service.InstanceId, It.IsAny<SpendNotificationInstanceData>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        repository.Verify(value => value.UnregisterInstanceAsync(service.InstanceId), Times.Once());
    }

    [Fact]
    public async Task HostStoppedPhase_AdmitsLateProducerNotification_ThenJoinsAndCloses()
    {
        var repository = new Mock<ISpendDataRepository>();
        using var service = Service(repository.Object);
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton<IHostedService>(new SpendNotificationLifecycleHostedService(service));
            services.AddSingleton<IHostedService>(new LateProducer(service));
        }).Build();
        await host.StartAsync();
        await host.StopAsync();
        repository.Verify(value => value.RecordSpendingPatternAsync(42, 1, 1, It.IsAny<CancellationToken>()), Times.Once());
        await service.NotifySpendUpdateAsync(42, 1, 1, null, null, null);
        repository.Verify(value => value.RecordSpendingPatternAsync(42, 1, 1, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task DisposeBeforeUnusedStop_IsIdempotentAndStartsNoRepositoryIo()
    {
        var repository = new Mock<ISpendDataRepository>();
        var service = Service(repository.Object);
        service.Dispose();
        await Task.WhenAll(service.ShutdownAsync(), service.ShutdownAsync());
        service.Dispose();
        Assert.Empty(repository.Invocations);
    }

    [Fact]
    public async Task CompletedAnalysisGeneration_ConcurrentStopAndDispose_JoinWithoutFurtherIo()
    {
        var repository = new Mock<ISpendDataRepository>();
        using var generation = new CancellationTokenSource();
        var entered = Signal();
        var analyzer = new Mock<ISpendPatternAnalyzer>();
        analyzer.Setup(value => value.AnalyzeAllPatternsAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                generation.Cancel();
                entered.TrySetResult();
                return Task.CompletedTask;
            });
        var service = Service(repository.Object, analyzer.Object);
        await service.StartAsync(generation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Await the actual producer's completion before exercising the completed-source
        // disposal branch, rather than depending on a timer or scheduler delay.
        var producer = (Task)typeof(DistributedSpendNotificationService)
            .GetField("_analysisTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;
        await producer.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(Task.Run(service.Dispose), Task.Run(() => service.StopAsync(CancellationToken.None)));
        await service.ShutdownAsync();
        await service.StopAsync(CancellationToken.None);
        service.Dispose();
        repository.Verify(value => value.UnregisterInstanceAsync(It.IsAny<string>()), Times.Never());
        analyzer.Verify(value => value.AnalyzeAllPatternsAsync(It.IsAny<CancellationToken>()), Times.Once());
    }

    [Theory]
    [InlineData("record-read")]
    [InlineData("record-write")]
    [InlineData("register-write")]
    public async Task ActualRepository_CancellationDuringIssuedSdkStep_PreventsEveryFollowOnCommand(string boundary)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        var read = new TaskCompletionSource<HashEntry[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = Signal();
        var field = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = new Mock<IDatabase>();
        database.Setup(value => value.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync([]);
        if (boundary == "record-read")
            database.Setup(value => value.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns(() => { entered.TrySetResult(); return read.Task; });
        if (boundary == "record-write")
            database.Setup(value => value.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), It.IsAny<CommandFlags>()))
                .Returns(() => { entered.TrySetResult(); return write.Task; });
        if (boundary == "register-write")
            database.Setup(value => value.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                    It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Returns(() => { entered.TrySetResult(); return field.Task; });
        var repository = new SpendDataRepository(database.Object, NullLogger<SpendDataRepository>.Instance);
        var operation = boundary == "register-write"
            ? repository.RegisterInstanceAsync("test", new SpendNotificationInstanceData("test", "machine", 1,
                DateTime.UtcNow, DateTime.UtcNow), cancellation.Token)
            : repository.RecordSpendingPatternAsync(42, 1, 1, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var commands = database.Invocations.Count;
        Assert.False(operation.IsCompleted);
        read.TrySetResult([]);
        write.TrySetResult();
        field.TrySetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(commands, database.Invocations.Count);
    }

    [Fact]
    public async Task ActualAnalyzer_CancellationDuringFirstKey_DoesNotReadNextKeyOrSendNotification()
    {
        using var cancellation = new CancellationTokenSource();
        var entered = Signal();
        var reply = new TaskCompletionSource<SpendingPatternData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Mock<ISpendDataRepository>();
        repository.Setup(value => value.GetAllPatternKeysAsync(It.IsAny<CancellationToken>())).ReturnsAsync([42, 43]);
        repository.Setup(value => value.GetSpendingPatternAsync(42))
            .Returns(() => { entered.TrySetResult(); return reply.Task; });
        var client = new Mock<IClientProxy>();
        var analyzer = new SpendPatternAnalyzer(Hub(client.Object), repository.Object, NullLogger<SpendPatternAnalyzer>.Instance);
        var operation = analyzer.AnalyzeAllPatternsAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        reply.SetResult(new SpendingPatternData { HourlyCount = 100, DailyCount = 240 });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        repository.Verify(value => value.GetSpendingPatternAsync(43), Times.Never());
        client.Verify(value => value.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    private sealed class LateProducer(DistributedSpendNotificationService service) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => service.NotifySpendUpdateAsync(42, 1, 1, null, null, null);
    }

    private static DistributedSpendNotificationService Service(ISpendDataRepository repository,
        ISpendPatternAnalyzer? analyzer = null, IClientProxy? client = null) => new(Hub(client ?? Mock.Of<IClientProxy>()),
            NullLogger<DistributedSpendNotificationService>.Instance, repository, Mock.Of<IBudgetAlertManager>(),
            analyzer ?? Mock.Of<ISpendPatternAnalyzer>(), TimeSpan.FromMilliseconds(10));

    private static IHubContext<SpendNotificationHub> Hub(IClientProxy client)
    {
        var clients = new Mock<IHubClients>();
        clients.Setup(value => value.Group(It.IsAny<string>())).Returns(client);
        var hub = new Mock<IHubContext<SpendNotificationHub>>();
        hub.SetupGet(value => value.Clients).Returns(clients.Object);
        return hub.Object;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
