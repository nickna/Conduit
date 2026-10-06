using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Services.SpendNotification;
using ConduitLLM.Tests.Helpers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.Tests.Gateway.Services;

public sealed class BudgetAlertManagerTests
{
    private readonly Mock<ISpendDataRepository> _repository = new();
    private readonly Mock<IClientProxy> _client = new();
    private readonly Mock<IHubContext<SpendNotificationHub>> _hub = new();

    public BudgetAlertManagerTests()
    {
        var clients = new Mock<IHubClients>();
        clients.Setup(value => value.Group(It.IsAny<string>())).Returns(_client.Object);
        _hub.SetupGet(value => value.Clients).Returns(clients.Object);
        _repository.Setup(value => value.MarkAlertSentAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<TimeSpan>())).ReturnsAsync(true);
    }

    private BudgetAlertManager Manager(IDistributedLockProvider provider) => new(_hub.Object, _repository.Object,
        provider, NullLogger<BudgetAlertManager>.Instance);

    [Fact]
    public async Task SimultaneousThresholds_HoldOwnershipThroughSlowSend_AndRetainMarkerAndCooldown()
    {
        var provider = new TestDistributedLockProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Setup(value => value.SendCoreAsync("BudgetAlert", It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, object[] _, CancellationToken _) =>
            {
                started.TrySetResult();
                await complete.Task;
            });
        var first = Manager(provider).CheckBudgetThresholdsAsync(42, 50, 100, 50);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Manager(provider).CheckBudgetThresholdsAsync(42, 50, 100, 50);
            _repository.Verify(value => value.MarkAlertSentAsync(42, 50, TimeSpan.FromHours(24)), Times.Once);
            _repository.Verify(value => value.SetAlertCooldownAsync(42, "budget:50", TimeSpan.FromHours(4)), Times.Once);
            _client.Verify(value => value.SendCoreAsync("BudgetAlert", It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { complete.TrySetResult(); await first; }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ExistingCooldownOrMarker_SuppressesDelivery(bool inCooldown, bool marker)
    {
        _repository.Setup(value => value.IsAlertInCooldownAsync(42, "budget:50")).ReturnsAsync(inCooldown);
        _repository.Setup(value => value.MarkAlertSentAsync(42, 50, It.IsAny<TimeSpan>())).ReturnsAsync(marker);
        await Manager(new TestDistributedLockProvider()).CheckBudgetThresholdsAsync(42, 50, 100, 50);
        _client.Verify(value => value.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DetectedLossDuringNonCancellableRedisWork_StopsBeforeMarkOrSend()
    {
        using var lost = new CancellationTokenSource();
        var handle = new Mock<IDistributedLockOwnership>();
        handle.SetupGet(value => value.HandleLostToken).Returns(lost.Token);
        var provider = new Mock<IDistributedLockProvider>();
        provider.Setup(value => value.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(handle.Object);
        _repository.Setup(value => value.IsAlertInCooldownAsync(It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(() => { lost.Cancel(); return false; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Manager(provider.Object).CheckBudgetThresholdsAsync(42, 80, 100, 80));
        _repository.Verify(value => value.MarkAlertSentAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<TimeSpan>()), Times.Never);
        _client.Verify(value => value.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()), Times.Never);
        handle.Verify(value => value.DisposeAsync(), Times.Once);
    }
}
