using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Extensions;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Services.SpendNotification;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Gateway.Services;

public sealed class SpendNotificationCompositionTests
{
    private static ServiceCollection Services()
    {
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(value => value.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(Mock.Of<IDatabase>());
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(redis.Object);
        services.AddSingleton(Mock.Of<IHubContext<SpendNotificationHub>>());
        services.AddSingleton(Mock.Of<IDistributedLockProvider>());
        services.AddSingleton(Mock.Of<ILeaderElectionService>());
        services.AddSpendNotificationServices();
        return services;
    }

    private static ServiceProvider Build(ServiceCollection services) => services.BuildServiceProvider(
        new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

    [Fact]
    public void ProductionGraph_ResolvesSingletonCollaboratorsAndHostedService()
    {
        var services = Services();
        using var provider = Build(services);
        using var scope = provider.CreateScope();

        Assert.IsType<SpendDataRepository>(provider.GetRequiredService<ISpendDataRepository>());
        Assert.IsType<BudgetAlertManager>(provider.GetRequiredService<IBudgetAlertManager>());
        Assert.IsType<SpendPatternAnalyzer>(provider.GetRequiredService<ISpendPatternAnalyzer>());
        Assert.Same(provider.GetRequiredService<ISpendDataRepository>(), scope.ServiceProvider.GetRequiredService<ISpendDataRepository>());
        Assert.Same(provider.GetRequiredService<IBudgetAlertManager>(), scope.ServiceProvider.GetRequiredService<IBudgetAlertManager>());
        Assert.Same(provider.GetRequiredService<ISpendPatternAnalyzer>(), scope.ServiceProvider.GetRequiredService<ISpendPatternAnalyzer>());
        Assert.IsType<DistributedSpendNotificationService>(provider.GetRequiredService<ISpendNotificationService>());
        Assert.Same(provider.GetRequiredService<ISpendNotificationService>(), scope.ServiceProvider.GetRequiredService<ISpendNotificationService>());
        Assert.DoesNotContain(services, value => value.ServiceType == typeof(DistributedSpendNotificationService));
        var hosted = provider.GetServices<IHostedService>().ToArray();
        Assert.Collection(hosted,
            service => Assert.IsType<SpendNotificationLifecycleHostedService>(service),
            service => Assert.IsType<LeaderElectedServiceWrapper>(service));
        Assert.Single(services, value => value.ServiceType == typeof(ISpendDataRepository));
        Assert.Single(services, value => value.ServiceType == typeof(IBudgetAlertManager));
        Assert.Single(services, value => value.ServiceType == typeof(ISpendPatternAnalyzer));
    }

    [Fact]
    public void ProductionGraph_ScopedLockProviderIsRejectedDuringBuild()
    {
        var services = Services();
        services.RemoveAll<IDistributedLockProvider>();
        services.AddScoped(_ => Mock.Of<IDistributedLockProvider>());

        var exception = Assert.Throws<AggregateException>(() => Build(services));

        Assert.Contains("Cannot consume scoped service", exception.ToString());
        Assert.Contains(nameof(IDistributedLockProvider), exception.ToString());
    }

    [Fact]
    public async Task HostedService_UsesInjectedCollaboratorsForLifecycleAndSpendUpdates()
    {
        var services = Services();
        var repository = new Mock<ISpendDataRepository>();
        var budget = new Mock<IBudgetAlertManager>();
        var patterns = new Mock<ISpendPatternAnalyzer>();
        var clients = new Mock<IHubClients>();
        clients.Setup(value => value.Group(It.IsAny<string>())).Returns(Mock.Of<IClientProxy>());
        var hub = new Mock<IHubContext<SpendNotificationHub>>();
        hub.SetupGet(value => value.Clients).Returns(clients.Object);
        services.RemoveAll<IHubContext<SpendNotificationHub>>();
        services.AddSingleton(hub.Object);
        services.RemoveAll<ISpendDataRepository>();
        services.AddSingleton(repository.Object);
        services.RemoveAll<IBudgetAlertManager>();
        services.AddSingleton(budget.Object);
        services.RemoveAll<ISpendPatternAnalyzer>();
        services.AddSingleton(patterns.Object);
        using var provider = Build(services);
        var service = (DistributedSpendNotificationService)provider.GetRequiredService<ISpendNotificationService>();

        await service.StartAsync(CancellationToken.None);
        await service.NotifySpendUpdateAsync(42, 5, 75, 100, "example", "provider");
        await service.StopAsync(CancellationToken.None);
        await service.ShutdownAsync(CancellationToken.None);

        repository.Verify(value => value.RegisterInstanceAsync(service.InstanceId,
            It.IsAny<ConduitLLM.Gateway.Serialization.SpendNotificationInstanceData>(), It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(value => value.RecordSpendingPatternAsync(42, 5, 75, It.IsAny<CancellationToken>()), Times.Once);
        budget.Verify(value => value.CheckBudgetThresholdsAsync(42, 75, 100, 75, It.IsAny<CancellationToken>()), Times.Once);
        patterns.Verify(value => value.CheckUnusualSpendingAsync(42, It.IsAny<CancellationToken>()), Times.Once);
        repository.Verify(value => value.UnregisterInstanceAsync(service.InstanceId), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedisOutage_AfterComposition_StillDeliversSpendNotification(bool timeout)
    {
        var services = Services();
        Exception failure = timeout
            ? new RedisTimeoutException("offline", CommandStatus.WaitingToBeSent)
            : new RedisConnectionException(ConnectionFailureType.UnableToConnect, "offline");
        var database = new Mock<IDatabase>();
        database.Setup(value => value.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(failure);
        database.Setup(value => value.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(),
                It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(failure);
        database.Setup(value => value.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(failure);
        var redis = new Mock<IConnectionMultiplexer>();
        redis.Setup(value => value.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        services.RemoveAll<IConnectionMultiplexer>();
        services.AddSingleton(redis.Object);
        var client = new Mock<IClientProxy>();
        var clients = new Mock<IHubClients>();
        clients.Setup(value => value.Group("vkey-42")).Returns(client.Object);
        var hub = new Mock<IHubContext<SpendNotificationHub>>();
        hub.SetupGet(value => value.Clients).Returns(clients.Object);
        services.RemoveAll<IHubContext<SpendNotificationHub>>();
        services.AddSingleton(hub.Object);
        using var provider = Build(services);
        var service = (DistributedSpendNotificationService)provider.GetRequiredService<ISpendNotificationService>();

        await service.StartAsync(CancellationToken.None);
        await service.NotifySpendUpdateAsync(42, 5, 75, null, "example", "provider");
        await service.StopAsync(CancellationToken.None);
        await service.ShutdownAsync(CancellationToken.None);

        client.Verify(value => value.SendCoreAsync("SpendUpdate", It.IsAny<object[]>(),
            It.IsAny<CancellationToken>()), Times.Once);
        database.Verify(value => value.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostedService_UnexpectedLifecycleFailure_Propagates(bool shutdown)
    {
        var services = Services();
        var failure = new ArgumentException("unexpected repository failure");
        var repository = new Mock<ISpendDataRepository>();
        if (shutdown)
        {
            repository.Setup(value => value.UnregisterInstanceAsync(It.IsAny<string>())).ThrowsAsync(failure);
        }
        else
        {
            repository.Setup(value => value.RegisterInstanceAsync(It.IsAny<string>(),
                It.IsAny<ConduitLLM.Gateway.Serialization.SpendNotificationInstanceData>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        }
        services.RemoveAll<ISpendDataRepository>();
        services.AddSingleton(repository.Object);
        using var provider = Build(services);
        var service = (DistributedSpendNotificationService)provider.GetRequiredService<ISpendNotificationService>();

        if (shutdown)
        {
            await service.StartAsync(CancellationToken.None);
        }
        var actual = await Assert.ThrowsAsync<ArgumentException>(() => shutdown
            ? service.ShutdownAsync(CancellationToken.None)
            : service.StartAsync(CancellationToken.None));

        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task ProductionGraph_CapturesOneDisposableOwnerForTheBorrowedNotificationService()
    {
        var services = Services();
        services.Replace(ServiceDescriptor.Singleton<ISpendNotificationService, DisposalCountingNotificationService>());
        var provider = Build(services);
        var service = (DisposalCountingNotificationService)provider.GetRequiredService<ISpendNotificationService>();
        var hosted = provider.GetServices<IHostedService>().ToArray();
        Assert.Equal(2, hosted.Length);
        foreach (var adapter in hosted.Reverse()) await adapter.StopAsync(CancellationToken.None);
        foreach (var adapter in hosted.OfType<IHostedLifecycleService>().Reverse())
            await adapter.StoppedAsync(CancellationToken.None);
        Assert.Equal(0, service.Disposals);

        await provider.DisposeAsync();

        Assert.Equal(1, service.Disposals);
    }

    public sealed class DisposalCountingNotificationService(IHubContext<SpendNotificationHub> hub,
        ILogger<DistributedSpendNotificationService> logger, ISpendDataRepository repository,
        IBudgetAlertManager budget, ISpendPatternAnalyzer patterns)
        : DistributedSpendNotificationService(hub, logger, repository, budget, patterns), IDisposable
    {
        public int Disposals { get; private set; }
        void IDisposable.Dispose()
        {
            Disposals++;
            base.Dispose();
        }
    }
}
