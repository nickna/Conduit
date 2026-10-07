using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace ConduitLLM.Tests.Core.Services;

public sealed class AsyncTaskRetentionTests
{
    [Fact]
    public void AddAsyncTaskServices_ResolvesHybridServiceFromHostContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistributedMemoryCache();
        services.AddSingleton(Mock.Of<IAsyncTaskRuntimeStore>());
        services.AddAsyncTaskServices();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<IAsyncTaskService>();

        Assert.IsType<HybridAsyncTaskService>(service);
    }

    [Fact]
    public async Task CleanupOldTasks_DrainsEveryDeleteBatchAndReportsBothCounts()
    {
        var store = new Mock<IAsyncTaskRuntimeStore>();
        store
            .Setup(candidate => candidate.ArchiveOldTasksAsync(
                TimeSpan.FromDays(1),
                TimeSpan.FromDays(7),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        store
            .SetupSequence(candidate => candidate.GetTaskIdsForCleanupAsync(
                TimeSpan.FromDays(30),
                2,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(["one", "two"])
            .ReturnsAsync(["three"])
            .ReturnsAsync([]);
        store
            .Setup(candidate => candidate.BulkDeleteAsync(
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<string> ids, CancellationToken _) => ids.Count);

        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        using var provider = services.BuildServiceProvider();
        var service = new HybridAsyncTaskService(
            store.Object,
            provider.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
            NullLogger<HybridAsyncTaskService>.Instance);

        var result = await service.CleanupOldTasksAsync(new AsyncTaskRetentionPolicy(
            TimeSpan.FromDays(1),
            TimeSpan.FromDays(30),
            TimeSpan.FromDays(7),
            BatchSize: 2));

        Assert.Equal(new AsyncTaskCleanupResult(3, 3), result);
        store.Verify(
            candidate => candidate.GetTaskIdsForCleanupAsync(
                TimeSpan.FromDays(30),
                2,
                It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

}
