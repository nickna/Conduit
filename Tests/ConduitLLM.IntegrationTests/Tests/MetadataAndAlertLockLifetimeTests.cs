using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Models.ProviderSync;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Services.SpendNotification;
using ConduitLLM.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("Postgres advisory locks")]
[Trait("Category", "Integration")]
[Trait("Component", "DistributedLock")]
public sealed class MetadataAndAlertLockLifetimeTests(PostgresLockTestContainerFixture fixture)
{
    private PostgresDistributedLockProvider Provider() => new(fixture.ConnectionString,
        NullLogger<PostgresDistributedLockProvider>.Instance);

    [Fact]
    public async Task ScheduledSync_ActualSessionLoss_PropagatesCancellationThroughAsyncRelease()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detection = new Mock<IOpenRouterDriftDetectionService>();
        detection.Setup(service => service.RunSyncAsync("Schedule", It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new ProviderSyncRunDto { Status = "Completed" };
            });
        await using var services = new ServiceCollection().AddScoped(_ => detection.Object).BuildServiceProvider();
        var scheduler = new OpenRouterMetadataSyncService(services.GetRequiredService<IServiceScopeFactory>(), Provider(),
            Options.Create(new OpenRouterSyncOptions()), NullLogger<OpenRouterMetadataSyncService>.Instance);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = scheduler.RunScheduledSyncAsync(shutdown.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(await Provider().TryAcquireAsync("openrouter:metadata-sync:leader"));
            await LockTestSession.TerminateAsync(fixture.ConnectionString, "openrouter:metadata-sync:leader");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
            await using var successor = await Provider().TryAcquireAsync("openrouter:metadata-sync:leader");
            Assert.NotNull(successor);
        }
        finally { shutdown.Cancel(); try { await run; } catch (OperationCanceledException) { } }
    }

    [Fact]
    public async Task AlertSend_BeyondFormerFiveSecondLease_AndUncooperativeCancellation_RetainsHealthyOwnership()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Mock<ISpendDataRepository>();
        repository.Setup(value => value.MarkAlertSentAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<TimeSpan>())).ReturnsAsync(true);
        var client = new Mock<IClientProxy>();
        client.Setup(value => value.SendCoreAsync("BudgetAlert", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, object[] _, CancellationToken _) => { started.TrySetResult(); await complete.Task; });
        var clients = new Mock<IHubClients>();
        clients.Setup(value => value.Group(It.IsAny<string>())).Returns(client.Object);
        var hub = new Mock<IHubContext<SpendNotificationHub>>();
        hub.SetupGet(value => value.Clients).Returns(clients.Object);
        BudgetAlertManager Manager() => new(hub.Object, repository.Object, Provider(), NullLogger<BudgetAlertManager>.Instance);
        using var cancellation = new CancellationTokenSource();
        var first = Manager().CheckBudgetThresholdsAsync(42, 50, 100, 50, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(5200));
            cancellation.Cancel();
            await Manager().CheckBudgetThresholdsAsync(42, 50, 100, 50);
            Assert.Null(await Provider().TryAcquireAsync(RedisKeys.Lock.AlertThreshold("42", "50")));
            repository.Verify(value => value.MarkAlertSentAsync(42, 50, TimeSpan.FromHours(24)), Times.Once);
            client.Verify(value => value.SendCoreAsync("BudgetAlert", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            complete.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }
        await using var successor = await Provider().TryAcquireAsync(RedisKeys.Lock.AlertThreshold("42", "50"));
        Assert.NotNull(successor);
    }
}
