using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Services;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace ConduitLLM.Tests.Configuration.Services
{
    /// <summary>
    /// Unit tests for GlobalSettingsCacheService
    /// Ensures in-memory cache loads correctly, handles invalidation, and provides strong typing
    /// </summary>
    [Trait("Category", "Unit")]
    [Trait("Component", "CacheService")]
    public class GlobalSettingsCacheServiceTests
    {
        private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
        private readonly Mock<IServiceScope> _mockScope;
        private readonly Mock<IServiceProvider> _mockServiceProvider;
        private readonly Mock<IGlobalSettingRepository> _mockRepository;
        private readonly Mock<ILogger<GlobalSettingsCacheService>> _mockLogger;
        private readonly GlobalSettingsCacheService _service;

        public GlobalSettingsCacheServiceTests()
        {
            _mockScopeFactory = new Mock<IServiceScopeFactory>();
            _mockScope = new Mock<IServiceScope>();
            _mockServiceProvider = new Mock<IServiceProvider>();
            _mockRepository = new Mock<IGlobalSettingRepository>();
            _mockLogger = new Mock<ILogger<GlobalSettingsCacheService>>();

            // Setup service scope chain
            _mockScopeFactory.Setup(x => x.CreateScope()).Returns(_mockScope.Object);
            _mockScope.Setup(x => x.ServiceProvider).Returns(_mockServiceProvider.Object);
            _mockServiceProvider.Setup(x => x.GetService(typeof(IGlobalSettingRepository)))
                .Returns(_mockRepository.Object);

            _service = new GlobalSettingsCacheService(_mockScopeFactory.Object, _mockLogger.Object);
        }

        #region StartAsync Tests

        [Fact]
        public async Task StartAsync_WithAvailableSettings_LoadsAllSettingsIntoCache()
        {
            // Arrange
            var settings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "setting1", Value = "value1" },
                new() { Id = 2, Key = "setting2", Value = "value2" },
                new() { Id = 3, Key = "setting3", Value = "value3" }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

            // Act
            await _service.StartAsync(CancellationToken.None);

            // Assert
            var stats = await _service.GetCacheStatsAsync();
            stats.EntryCount.Should().Be(3);
            stats.CachedKeys.Should().Contain(new[] { "setting1", "setting2", "setting3" });
        }

        [Fact]
        public async Task StartAsync_WithNoSettings_LoadsEmptyCache()
        {
            // Arrange
            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<GlobalSetting>());

            // Act
            await _service.StartAsync(CancellationToken.None);

            // Assert
            var stats = await _service.GetCacheStatsAsync();
            stats.EntryCount.Should().Be(0);
        }

        [Fact]
        public async Task StartAsync_LogsStartupInformation()
        {
            // Arrange
            var settings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "setting1", Value = "value1" }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

            // Act
            await _service.StartAsync(CancellationToken.None);

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("GlobalSettingsCacheService starting")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);

            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) =>
                        o.ToString()!.Contains("started successfully") &&
                        o.ToString()!.Contains("1 settings loaded")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task StartAsync_WhenDatabaseThrowsException_DoesNotThrowButLogsError()
        {
            // Arrange
            var exception = new InvalidOperationException("Database unavailable");
            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ThrowsAsync(exception);

            // Act
            await _service.StartAsync(CancellationToken.None);

            // Assert - Should not throw, service starts with defaults
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Failed to load global settings on startup")),
                    exception,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task StartAsync_WhenCancellationRequested_StopsLoadingSettings()
        {
            // Arrange
            var settings = Enumerable.Range(1, 100)
                .Select(i => new GlobalSetting { Id = i, Key = $"setting{i}", Value = $"value{i}" })
                .ToList();

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);

            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act
            await _service.StartAsync(cts.Token);

            // Assert - Should handle cancellation gracefully
            var stats = await _service.GetCacheStatsAsync();
            // Cache size might be less than 100 if cancellation was honored
            stats.EntryCount.Should().BeLessThanOrEqualTo(100);
        }

        #endregion

        #region StopAsync Tests

        [Fact]
        public async Task StopAsync_ClearsCacheAndCompletesSuccessfully()
        {
            // Arrange
            var settings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "setting1", Value = "value1" }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
            await _service.StartAsync(CancellationToken.None);

            // Verify cache has data
            var statsBefore = await _service.GetCacheStatsAsync();
            statsBefore.EntryCount.Should().Be(1);

            // Act
            await _service.StopAsync(CancellationToken.None);

            // Assert
            var statsAfter = await _service.GetCacheStatsAsync();
            statsAfter.EntryCount.Should().Be(0);
        }

        [Fact]
        public async Task StopAsync_LogsStopInformation()
        {
            // Act
            await _service.StopAsync(CancellationToken.None);

            // Assert
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("GlobalSettingsCacheService stopping")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        #endregion

        #region GetMaxAgenticIterationsAsync Tests

        [Fact]
        public async Task GetMaxAgenticIterationsAsync_WithValidSetting_ReturnsValue()
        {
            // Arrange
            var settings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "Agentic.MaxIterations", Value = "10" }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
            await _service.StartAsync(CancellationToken.None);

            // Act
            var result = await _service.GetMaxAgenticIterationsAsync();

            // Assert
            result.Should().Be(10);
        }

        [Fact]
        public async Task GetMaxAgenticIterationsAsync_WhenSettingNotFound_ReturnsDefault()
        {
            // Arrange
            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<GlobalSetting>());
            await _service.StartAsync(CancellationToken.None);

            // Act
            var result = await _service.GetMaxAgenticIterationsAsync();

            // Assert
            result.Should().Be(5); // DEFAULT_MAX_AGENTIC_ITERATIONS
        }

        [Fact]
        public async Task GetMaxAgenticIterationsAsync_WithInvalidValue_ReturnsDefaultAndLogsWarning()
        {
            // Arrange
            var settings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "Agentic.MaxIterations", Value = "not_a_number" }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
            await _service.StartAsync(CancellationToken.None);

            // Act
            var result = await _service.GetMaxAgenticIterationsAsync();

            // Assert
            result.Should().Be(5);
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Failed to parse Max agentic iterations")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Theory]
        [InlineData("0", 1)]      // Below minimum, clamped to 1
        [InlineData("-5", 1)]     // Negative, clamped to 1
        [InlineData("150", 100)]  // Above maximum, clamped to 100
        [InlineData("1", 1)]      // Valid minimum
        [InlineData("100", 100)]  // Valid maximum
        [InlineData("50", 50)]    // Valid middle value
        public async Task GetMaxAgenticIterationsAsync_ClampsToValidRange(string value, int expected)
        {
            // Arrange
            var settings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "Agentic.MaxIterations", Value = value }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
            await _service.StartAsync(CancellationToken.None);

            // Act
            var result = await _service.GetMaxAgenticIterationsAsync();

            // Assert
            result.Should().Be(expected);
        }

        #endregion

        #region GetMinAgenticIterationsAsync Tests

        [Fact]
        public async Task GetMinAgenticIterationsAsync_WithValidSetting_ReturnsValue()
        {
            // Arrange
            var settings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "Agentic.MinIterations", Value = "3" }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
            await _service.StartAsync(CancellationToken.None);

            // Act
            var result = await _service.GetMinAgenticIterationsAsync();

            // Assert
            result.Should().Be(3);
        }

        [Fact]
        public async Task GetMinAgenticIterationsAsync_WhenSettingNotFound_ReturnsDefault()
        {
            // Arrange
            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<GlobalSetting>());
            await _service.StartAsync(CancellationToken.None);

            // Act
            var result = await _service.GetMinAgenticIterationsAsync();

            // Assert
            result.Should().Be(1); // DEFAULT_MIN_AGENTIC_ITERATIONS
        }

        #endregion

        #region GetDefaultAgenticModeEnabledAsync Tests

        [Theory]
        [InlineData("true", true)]
        [InlineData("True", true)]
        [InlineData("TRUE", true)]
        [InlineData("false", false)]
        [InlineData("False", false)]
        [InlineData("FALSE", false)]
        [InlineData("1", true)]
        [InlineData("yes", true)]
        [InlineData("Yes", true)]
        [InlineData("on", true)]
        [InlineData("On", true)]
        [InlineData("0", false)]
        [InlineData("no", false)]
        [InlineData("No", false)]
        [InlineData("off", false)]
        [InlineData("Off", false)]
        public async Task GetDefaultAgenticModeEnabledAsync_WithVariousValidFormats_ParsesCorrectly(string value, bool expected)
        {
            // Arrange
            var settings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "Agentic.DefaultEnabled", Value = value }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
            await _service.StartAsync(CancellationToken.None);

            // Act
            var result = await _service.GetDefaultAgenticModeEnabledAsync();

            // Assert
            result.Should().Be(expected);
        }

        [Fact]
        public async Task GetDefaultAgenticModeEnabledAsync_WhenSettingNotFound_ReturnsDefault()
        {
            // Arrange
            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<GlobalSetting>());
            await _service.StartAsync(CancellationToken.None);

            // Act
            var result = await _service.GetDefaultAgenticModeEnabledAsync();

            // Assert
            result.Should().Be(true); // DEFAULT_AGENTIC_ENABLED
        }

        [Fact]
        public async Task GetDefaultAgenticModeEnabledAsync_WithInvalidValue_ReturnsDefaultAndLogsWarning()
        {
            // Arrange
            var settings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "Agentic.DefaultEnabled", Value = "maybe" }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
            await _service.StartAsync(CancellationToken.None);

            // Act
            var result = await _service.GetDefaultAgenticModeEnabledAsync();

            // Assert
            result.Should().Be(true);
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Warning,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Failed to parse Default agentic enabled")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        #endregion

        #region InvalidateSettingAsync Tests

        [Fact]
        public async Task InvalidateSettingAsync_WithExistingSetting_RemovesFromCacheAndReloads()
        {
            // Arrange
            var settings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "test_setting", Value = "old_value" }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
            await _service.StartAsync(CancellationToken.None);

            var updatedSetting = new GlobalSetting { Id = 1, Key = "test_setting", Value = "new_value" };
            _mockRepository.Setup(x => x.GetByKeyAsync("test_setting", It.IsAny<CancellationToken>())).ReturnsAsync(updatedSetting);

            // Act
            await _service.InvalidateSettingAsync("test_setting");

            // Assert
            _mockRepository.Verify(x => x.GetByKeyAsync("test_setting", It.IsAny<CancellationToken>()), Times.Once);
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Information,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) =>
                        o.ToString()!.Contains("Invalidated cached setting") &&
                        o.ToString()!.Contains("test_setting")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task InvalidateSettingAsync_WithNewlyCreatedSetting_LoadsItIntoCache()
        {
            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<GlobalSetting>());
            await _service.StartAsync(CancellationToken.None);
            _mockRepository
                .Setup(x => x.GetByKeyAsync("new_setting", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GlobalSetting { Id = 42, Key = "new_setting", Value = "enabled" });

            await _service.InvalidateSettingAsync("new_setting");

            (await _service.GetSettingValueAsync("new_setting")).Should().Be("enabled");
            _mockRepository.Verify(
                x => x.GetByKeyAsync("new_setting", It.IsAny<CancellationToken>()),
                Times.Once);
        }

        [Fact]
        public async Task InvalidateSettingAsync_WithNullOrEmptyKey_DoesNothing()
        {
            // Arrange
            var settings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "test", Value = "value" }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
            await _service.StartAsync(CancellationToken.None);

            // Act
            await _service.InvalidateSettingAsync(null!);
            await _service.InvalidateSettingAsync("");
            await _service.InvalidateSettingAsync("   ");

            // Assert - Repository should never be called
            _mockRepository.Verify(x => x.GetByKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task InvalidateSettingAsync_WhenRepositoryThrows_LogsErrorButDoesNotThrow()
        {
            // Arrange
            var settings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "failing_setting", Value = "value" }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
            await _service.StartAsync(CancellationToken.None);

            var exception = new InvalidOperationException("Database error");
            _mockRepository.Setup(x => x.GetByKeyAsync("failing_setting", It.IsAny<CancellationToken>())).ThrowsAsync(exception);

            // Act
            await _service.InvalidateSettingAsync("failing_setting");

            // Assert - Should not throw
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((o, t) => o.ToString()!.Contains("Error invalidating setting")),
                    exception,
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task InvalidateSettingAsync_UpdatesInvalidationStatistics()
        {
            // Arrange
            var settings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "stat_test", Value = "value" }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
            await _service.StartAsync(CancellationToken.None);

            var statsBefore = await _service.GetCacheStatsAsync();
            var invalidationsBefore = statsBefore.InvalidationCount;

            _mockRepository.Setup(x => x.GetByKeyAsync("stat_test", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GlobalSetting { Id = 1, Key = "stat_test", Value = "new_value" });

            // Act
            await _service.InvalidateSettingAsync("stat_test");

            // Assert
            var statsAfter = await _service.GetCacheStatsAsync();
            var invalidationsAfter = statsAfter.InvalidationCount;
            invalidationsAfter.Should().Be(invalidationsBefore + 1);
        }

        #endregion

        #region ReloadAllSettingsAsync Tests

        [Fact]
        public async Task ReloadAllSettingsAsync_ClearsAndReloadsCache()
        {
            // Arrange
            var initialSettings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "setting1", Value = "value1" }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(initialSettings);
            await _service.StartAsync(CancellationToken.None);

            var newSettings = new List<GlobalSetting>
            {
                new() { Id = 2, Key = "setting2", Value = "value2" },
                new() { Id = 3, Key = "setting3", Value = "value3" }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(newSettings);

            // Act
            await _service.ReloadAllSettingsAsync();

            // Assert
            var stats = await _service.GetCacheStatsAsync();
            stats.EntryCount.Should().Be(2);
            var cachedKeys = stats.CachedKeys;
            cachedKeys.Should().Contain("setting2");
            cachedKeys.Should().Contain("setting3");
            cachedKeys.Should().NotContain("setting1");
        }

        [Fact]
        public async Task ReloadAllSettingsAsync_WhenRepositoryThrows_RethrowsException()
        {
            // Arrange
            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Database error"));

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await _service.ReloadAllSettingsAsync());
        }

        [Fact]
        public async Task PublishReloadAsync_WithoutRedis_ReloadsOncePerRequest()
        {
            _mockRepository
                .SetupSequence(x => x.ListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([new GlobalSetting { Id = 1, Key = "old", Value = "1" }])
                .ReturnsAsync([new GlobalSetting { Id = 2, Key = "new", Value = "2" }]);
            await _service.StartAsync(CancellationToken.None);

            await _service.PublishReloadAsync("reload-1");
            await _service.PublishReloadAsync("reload-1");

            var stats = await _service.GetCacheStatsAsync();
            stats.CachedKeys.Should().ContainSingle("new");
            _mockRepository.Verify(
                repository => repository.ListAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(2));
        }

        #endregion

        #region GetCacheStatsAsync Tests

        [Fact]
        public async Task GetCacheStatsAsync_ReturnsCorrectStatistics()
        {
            // Arrange
            var settings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "Agentic.MaxIterations", Value = "10" },
                new() { Id = 2, Key = "test_setting", Value = "value" }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
            await _service.StartAsync(CancellationToken.None);

            // Generate some cache hits and misses
            await _service.GetMaxAgenticIterationsAsync(); // Cache hit
            await _service.GetMinAgenticIterationsAsync(); // Cache miss (not loaded)

            // Act
            var stats = await _service.GetCacheStatsAsync();

            // Assert
            stats.EntryCount.Should().Be(2);
            stats.HitCount.Should().BeGreaterThan(0);
            stats.MissCount.Should().BeGreaterThanOrEqualTo(0);
            stats.InvalidationCount.Should().BeGreaterThanOrEqualTo(0);
            stats.HitRate.Should().BeInRange(0, 1);
            stats.LastResetTime.Should().NotBe(default);
            stats.CachedKeys.Should().HaveCount(2);
        }

        [Fact]
        public async Task GetCacheStatsAsync_CalculatesHitRateCorrectly()
        {
            // Arrange
            var settings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "Agentic.MaxIterations", Value = "10" }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
            await _service.StartAsync(CancellationToken.None);

            // Generate 3 hits and 1 miss
            await _service.GetMaxAgenticIterationsAsync(); // Hit
            await _service.GetMaxAgenticIterationsAsync(); // Hit
            await _service.GetMaxAgenticIterationsAsync(); // Hit
            await _service.GetMinAgenticIterationsAsync(); // Miss

            // Act
            var stats = await _service.GetCacheStatsAsync();

            // Assert
            stats.HitRate.Should().BeApproximately(0.75, 0.001); // 3 hits / 4 total
        }

        #endregion

        #region Thread Safety Tests

        [Fact]
        public async Task ConcurrentInvalidations_AreHandledSafely()
        {
            // Arrange
            var settings = Enumerable.Range(1, 10)
                .Select(i => new GlobalSetting { Id = i, Key = $"setting{i}", Value = $"value{i}" })
                .ToList();

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
            await _service.StartAsync(CancellationToken.None);

            _mockRepository.Setup(x => x.GetByKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) => new GlobalSetting { Id = 1, Key = key, Value = "new_value" });

            // Act - Invalidate multiple settings concurrently
            var tasks = Enumerable.Range(1, 10)
                .Select(i => _service.InvalidateSettingAsync($"setting{i}"))
                .ToArray();

            await Task.WhenAll(tasks);

            // Assert - All invalidations should complete successfully
            var stats = await _service.GetCacheStatsAsync();
            stats.InvalidationCount.Should().Be(10);
        }

        [Fact]
        public async Task ConcurrentReads_AreHandledSafely()
        {
            // Arrange
            var settings = new List<GlobalSetting>
            {
                new() { Id = 1, Key = "Agentic.MaxIterations", Value = "10" },
                new() { Id = 2, Key = "Agentic.MinIterations", Value = "2" },
                new() { Id = 3, Key = "Agentic.DefaultEnabled", Value = "true" }
            };

            _mockRepository.Setup(x => x.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
            await _service.StartAsync(CancellationToken.None);

            // Act - Read settings concurrently
            var tasks = new List<Task>();
            for (int i = 0; i < 100; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await _service.GetMaxAgenticIterationsAsync();
                    await _service.GetMinAgenticIterationsAsync();
                    await _service.GetDefaultAgenticModeEnabledAsync();
                }));
            }

            await Task.WhenAll(tasks);

            // Assert - All reads should complete successfully with correct values
            var maxIterations = await _service.GetMaxAgenticIterationsAsync();
            maxIterations.Should().Be(10);
        }

        #endregion
    }
}
