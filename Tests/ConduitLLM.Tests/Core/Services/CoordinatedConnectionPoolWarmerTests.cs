using System;
using System.Threading;
using System.Threading.Tasks;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Options;
using ConduitLLM.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

namespace ConduitLLM.Tests.Core.Services
{
    /// <summary>
    /// Unit tests for CoordinatedConnectionPoolWarmer.
    /// Tests the coordinated warming behavior including leader/follower patterns,
    /// graceful degradation, and edge cases.
    /// </summary>
    [Trait("Category", "Unit")]
    public class CoordinatedConnectionPoolWarmerTests
    {
        private readonly Mock<IDistributedLockProvider> _lockServiceMock;
        private readonly Mock<IConnectionMultiplexer> _redisMock;
        private readonly Mock<ISubscriber> _subscriberMock;
        private readonly Mock<ILogger<CoordinatedConnectionPoolWarmer>> _loggerMock;
        private readonly ConnectionPoolWarmingOptions _options;
        private readonly ServiceCollection _services;
        private readonly IServiceProvider _serviceProvider;

        public CoordinatedConnectionPoolWarmerTests()
        {
            _lockServiceMock = new Mock<IDistributedLockProvider>();
            _redisMock = new Mock<IConnectionMultiplexer>();
            _subscriberMock = new Mock<ISubscriber>();
            _loggerMock = new Mock<ILogger<CoordinatedConnectionPoolWarmer>>();
            _options = new ConnectionPoolWarmingOptions
            {
                EnableCoordinatedWarming = true,
                SignalTimeout = TimeSpan.FromMilliseconds(100),
                StaggerDelay = TimeSpan.FromMilliseconds(10)
            };

            // Setup Redis subscriber
            _redisMock.Setup(r => r.GetSubscriber(It.IsAny<object>()))
                .Returns(_subscriberMock.Object);

            // Create a real service provider for tests that need scoping
            _services = new ServiceCollection();
            _services.AddLogging();
            _serviceProvider = _services.BuildServiceProvider();
        }

        [Fact]
        public async Task StartAsync_WhenNoConnectionsToWarm_SkipsWarming()
        {
            // Arrange - WebAdmin has 0 connections to warm
            var warmer = CreateWarmer("WebAdmin");

            // Act
            await warmer.StartAsync(CancellationToken.None);

            // Assert - Lock should never be acquired
            _lockServiceMock.Verify(
                l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task StartAsync_DetectedLoss_ReleasesWithoutPublishingSignal()
        {
            using var lost = new CancellationTokenSource();
            lost.Cancel();
            var ownership = new Mock<IDistributedLockOwnership>();
            ownership.SetupGet(value => value.HandleLostToken).Returns(lost.Token);
            _lockServiceMock.Setup(value => value.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ownership.Object);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateWarmer("CoreAPI").StartAsync(CancellationToken.None));
            _subscriberMock.Verify(value => value.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()), Times.Never);
            ownership.Verify(value => value.DisposeAsync(), Times.Once);
        }

        [Fact]
        public async Task StartAsync_WarmingFailure_DoesNotPublishSuccessSignal()
        {
            var factory = new Mock<Microsoft.EntityFrameworkCore.IDbContextFactory<ConduitLLM.Configuration.ConduitDbContext>>();
            factory.Setup(value => value.CreateDbContextAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("isolated DB outage"));
            using var services = new ServiceCollection().AddSingleton(factory.Object).BuildServiceProvider();
            var ownership = new Mock<IDistributedLockOwnership>();
            _lockServiceMock.Setup(value => value.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>())).ReturnsAsync(ownership.Object);
            using var warmer = new CoordinatedConnectionPoolWarmer(services, _lockServiceMock.Object, _redisMock.Object, _loggerMock.Object, _options, "CoreAPI");
            await warmer.StartAsync(CancellationToken.None);
            _subscriberMock.Verify(value => value.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()), Times.Never);
            ownership.Verify(value => value.DisposeAsync(), Times.Once);
        }

        [Fact]
        public async Task StartAsync_WhenCoordinationDisabled_DoesNotAcquireLock()
        {
            // Arrange
            var options = new ConnectionPoolWarmingOptions
            {
                EnableCoordinatedWarming = false,
                SignalTimeout = TimeSpan.FromMilliseconds(100)
            };

            var warmer = new CoordinatedConnectionPoolWarmer(
                _serviceProvider,
                _lockServiceMock.Object,
                _redisMock.Object,
                _loggerMock.Object,
                options,
                "CoreAPI");

            // Act
            await warmer.StartAsync(CancellationToken.None);

            // Assert - Lock should never be acquired when coordination is disabled
            _lockServiceMock.Verify(
                l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task StartAsync_WhenRedisUnavailable_DoesNotAcquireLock()
        {
            // Arrange - Create warmer without Redis
            var warmer = new CoordinatedConnectionPoolWarmer(
                _serviceProvider,
                _lockServiceMock.Object,
                null, // No Redis
                _loggerMock.Object,
                _options,
                "CoreAPI");

            // Act
            await warmer.StartAsync(CancellationToken.None);

            // Assert - Lock should never be acquired when Redis is unavailable
            _lockServiceMock.Verify(
                l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task StartAsync_WhenLockServiceUnavailable_DoesNotThrow()
        {
            // Arrange - Create warmer without lock service
            var warmer = new CoordinatedConnectionPoolWarmer(
                _serviceProvider,
                null, // No lock service
                _redisMock.Object,
                _loggerMock.Object,
                _options,
                "CoreAPI");

            // Act & Assert - Should not throw
            var exception = await Record.ExceptionAsync(() => warmer.StartAsync(CancellationToken.None));
            Assert.Null(exception);
        }

        [Fact]
        public async Task StartAsync_AsLeader_AcquiresLockAndPublishesSignal()
        {
            // Arrange
            var lockMock = new Mock<IDistributedLockOwnership>();
            _lockServiceMock
                .Setup(l => l.TryAcquireAsync(
                    It.Is<string>(s => s.Contains("CoreAPI")),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(lockMock.Object);

            var warmer = CreateWarmer("CoreAPI");

            // Act
            await warmer.StartAsync(CancellationToken.None);

            // Assert - Lock acquired
            _lockServiceMock.Verify(
                l => l.TryAcquireAsync(
                    It.Is<string>(s => s.Contains("CoreAPI")),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);

            // Lock should be released
            lockMock.Verify(l => l.DisposeAsync(), Times.Once);

            // Signal should be published
            _subscriberMock.Verify(
                s => s.PublishAsync(
                    It.Is<RedisChannel>(c => c.ToString().Contains("CoreAPI")),
                    It.IsAny<RedisValue>(),
                    It.IsAny<CommandFlags>()),
                Times.Once);
        }

        [Fact]
        public async Task StartAsync_AsFollower_SubscribesToChannel()
        {
            // Arrange - Lock acquisition fails (follower scenario)
            _lockServiceMock
                .Setup(l => l.TryAcquireAsync(
                    It.IsAny<string>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((IDistributedLockOwnership?)null);

            var warmer = CreateWarmer("CoreAPI");

            // Act - This will timeout since no signal is sent
            await warmer.StartAsync(CancellationToken.None);

            // Assert - Should have subscribed
            _subscriberMock.Verify(
                s => s.SubscribeAsync(
                    It.Is<RedisChannel>(c => c.ToString().Contains("CoreAPI")),
                    It.IsAny<CommandFlags>()),
                Times.Once);
        }

        [Fact]
        public async Task StartAsync_WhenCancelled_StopsGracefully()
        {
            // Arrange
            var cts = new CancellationTokenSource();
            cts.Cancel();

            var warmer = CreateWarmer("CoreAPI");

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warmer.StartAsync(cts.Token));
        }

        [Fact]
        public async Task StopAsync_CompletesSuccessfully()
        {
            // Arrange
            var warmer = CreateWarmer("CoreAPI");

            // Act & Assert - Just verify it doesn't throw
            var exception = await Record.ExceptionAsync(() => warmer.StopAsync(CancellationToken.None));
            Assert.Null(exception);
        }

        [Fact]
        public void Constructor_WithNullServiceProvider_ThrowsArgumentNullException()
        {
            // Arrange & Act & Assert
            Assert.Throws<ArgumentNullException>(() => new CoordinatedConnectionPoolWarmer(
                null!,
                _lockServiceMock.Object,
                _redisMock.Object,
                _loggerMock.Object,
                _options,
                "CoreAPI"));
        }

        [Fact]
        public void Constructor_WithNullLogger_ThrowsArgumentNullException()
        {
            // Arrange & Act & Assert
            Assert.Throws<ArgumentNullException>(() => new CoordinatedConnectionPoolWarmer(
                _serviceProvider,
                _lockServiceMock.Object,
                _redisMock.Object,
                null!,
                _options,
                "CoreAPI"));
        }

        [Fact]
        public void Constructor_WithNullOptions_ThrowsArgumentNullException()
        {
            // Arrange & Act & Assert
            Assert.Throws<ArgumentNullException>(() => new CoordinatedConnectionPoolWarmer(
                _serviceProvider,
                _lockServiceMock.Object,
                _redisMock.Object,
                _loggerMock.Object,
                null!,
                "CoreAPI"));
        }

        [Theory]
        [InlineData("CoreAPI")]
        [InlineData("AdminAPI")]
        [InlineData("Unknown")]
        public async Task StartAsync_AcquiresLockWithServiceTypeInKey(string serviceType)
        {
            // Arrange
            var lockMock = new Mock<IDistributedLockOwnership>();
            _lockServiceMock
                .Setup(l => l.TryAcquireAsync(
                    It.IsAny<string>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(lockMock.Object);

            var warmer = CreateWarmer(serviceType);

            // Act
            await warmer.StartAsync(CancellationToken.None);

            // Assert - Lock key should include service type
            _lockServiceMock.Verify(
                l => l.TryAcquireAsync(
                    It.Is<string>(s => s.EndsWith($":{serviceType}")),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task StartAsync_AsLeader_ReleasesLockEvenWhenWarmingFails()
        {
            // Arrange
            var lockMock = new Mock<IDistributedLockOwnership>();
            _lockServiceMock
                .Setup(l => l.TryAcquireAsync(
                    It.IsAny<string>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(lockMock.Object);

            // The warmer will try to warm connections but DbContextFactory won't be available
            // This simulates a failure during warming
            var warmer = CreateWarmer("CoreAPI");

            // Act - Warming will log warnings but not throw
            await warmer.StartAsync(CancellationToken.None);

            // Assert - Lock should still be released
            lockMock.Verify(l => l.DisposeAsync(), Times.Once);
        }

        [Fact]
        public async Task StartAsync_WebAdmin_DoesNotAttemptWarming()
        {
            // Arrange - WebAdmin has 0 connections configured
            var warmer = CreateWarmer("WebAdmin");

            // Act
            await warmer.StartAsync(CancellationToken.None);

            // Assert - No lock acquisition, no pub/sub
            _lockServiceMock.Verify(
                l => l.TryAcquireAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()),
                Times.Never);

            _subscriberMock.Verify(
                s => s.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()),
                Times.Never);
        }

        [Fact]
        public async Task StartAsync_UsesImmediateAcquisition()
        {
            // Arrange
            var options = new ConnectionPoolWarmingOptions
            {
                EnableCoordinatedWarming = true,
                SignalTimeout = TimeSpan.FromMilliseconds(100)
            };

            var lockMock = new Mock<IDistributedLockOwnership>();
            _lockServiceMock
                .Setup(l => l.TryAcquireAsync(
                    It.IsAny<string>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(lockMock.Object);

            var warmer = new CoordinatedConnectionPoolWarmer(
                _serviceProvider,
                _lockServiceMock.Object,
                _redisMock.Object,
                _loggerMock.Object,
                options,
                "CoreAPI");

            // Act
            await warmer.StartAsync(CancellationToken.None);

            // Assert - Acquisition never waits for another warmer.
            _lockServiceMock.Verify(
                l => l.TryAcquireAsync(
                    It.IsAny<string>(),
                    TimeSpan.Zero,
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task StartAsync_PublishesSignalToCorrectChannel()
        {
            // Arrange
            var lockMock = new Mock<IDistributedLockOwnership>();
            _lockServiceMock
                .Setup(l => l.TryAcquireAsync(
                    It.IsAny<string>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(lockMock.Object);

            var warmer = CreateWarmer("AdminAPI");

            // Act
            await warmer.StartAsync(CancellationToken.None);

            // Assert - Signal published to channel with service type
            _subscriberMock.Verify(
                s => s.PublishAsync(
                    It.Is<RedisChannel>(c => c.ToString().Contains("AdminAPI")),
                    It.IsAny<RedisValue>(),
                    It.IsAny<CommandFlags>()),
                Times.Once);
        }

        #region Helper Methods

        private CoordinatedConnectionPoolWarmer CreateWarmer(string serviceType)
        {
            return new CoordinatedConnectionPoolWarmer(
                _serviceProvider,
                _lockServiceMock.Object,
                _redisMock.Object,
                _loggerMock.Object,
                _options,
                serviceType);
        }

        #endregion
    }
}
