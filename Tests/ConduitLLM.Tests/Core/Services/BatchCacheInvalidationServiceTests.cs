using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;

namespace ConduitLLM.Tests.Core.Services
{
    [Trait("Category", "Unit")]
    public class BatchCacheInvalidationServiceTests
    {
        private readonly Mock<IServiceProvider> _mockServiceProvider;
        private readonly Mock<IVirtualKeyCache> _mockVirtualKeyCache;
        private readonly Mock<IModelCostCache> _mockModelCostCache;
        private readonly Mock<ILogger<BatchCacheInvalidationService>> _mockLogger;
        private readonly BatchCacheInvalidationService _service;
        private readonly BatchInvalidationOptions _options;
        private readonly ObservedTimeProvider _clock = new();

        private sealed class ObservedTimeProvider : FakeTimeProvider
        {
            public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
            {
                var timer = base.CreateTimer(callback, state, dueTime, period);
                TimerCreated.TrySetResult();
                return timer;
            }
        }

        public BatchCacheInvalidationServiceTests()
        {
            _mockServiceProvider = new Mock<IServiceProvider>();
            _mockVirtualKeyCache = new Mock<IVirtualKeyCache>();
            _mockModelCostCache = new Mock<IModelCostCache>();
            _mockLogger = new Mock<ILogger<BatchCacheInvalidationService>>();
            
            _options = new BatchInvalidationOptions
            {
                Enabled = true,
                BatchWindow = TimeSpan.FromMilliseconds(100),
                MaxBatchSize = 50,
                EnableCoalescing = true
            };

            var mockOptions = new Mock<IOptions<BatchInvalidationOptions>>();
            mockOptions.Setup(x => x.Value).Returns(_options);

            // Setup service provider to return mocked caches
            var serviceScope = new Mock<IServiceScope>();
            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(x => x.CreateScope()).Returns(serviceScope.Object);
            serviceScope.Setup(x => x.ServiceProvider).Returns(_mockServiceProvider.Object);
            _mockServiceProvider.Setup(x => x.GetService(typeof(IServiceScopeFactory))).Returns(scopeFactory.Object);
            _mockServiceProvider.Setup(x => x.GetService(typeof(IVirtualKeyCache))).Returns(_mockVirtualKeyCache.Object);
            _mockServiceProvider.Setup(x => x.GetService(typeof(IModelCostCache))).Returns(_mockModelCostCache.Object);

            _service = new BatchCacheInvalidationService(
                _mockServiceProvider.Object,
                mockOptions.Object,
                _mockLogger.Object,
                _clock);
        }

        [Theory]
        [InlineData(true)] [InlineData(false)]
        public async Task ModelCostExpirationReachesActualBillingBeforeAcknowledgingQueue(bool bulk)
        {
            var billing = new Mock<ConduitLLM.Configuration.Interfaces.IModelCostService>();
            _mockServiceProvider.Setup(value => value.GetService(typeof(ConduitLLM.Configuration.Interfaces.IModelCostService))).Returns(billing.Object);
            billing.Setup(value => value.ClearCacheAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("required expiration failed"));
            var message = new ModelCostChanged { ModelCostId = 42 };
            await Assert.ThrowsAsync<InvalidOperationException>(() => bulk
                ? _service.QueueBulkInvalidationAsync(["42", "43"], message, CacheType.ModelCost)
                : _service.QueueInvalidationAsync("42", message, CacheType.ModelCost));
            Assert.Equal(0, (await _service.GetStatsAsync()).TotalQueued);
            billing.Setup(value => value.ClearCacheAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            if (bulk) await _service.QueueBulkInvalidationAsync(["42", "43"], message, CacheType.ModelCost);
            else await _service.QueueInvalidationAsync("42", message, CacheType.ModelCost);
            await _service.FlushAsync();
            billing.Verify(value => value.ClearCacheAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
            _mockModelCostCache.Verify(value => value.InvalidateModelCostAsync(42), Times.Once);
        }

        [Fact]
        public async Task QueueInvalidationAsync_Should_Queue_Request()
        {
            // Arrange
            var keyHash = "test-key-123";
            var @event = new VirtualKeyUpdated 
            { 
                KeyId = 123, 
                KeyHash = keyHash,
                ChangedProperties = new[] { "IsEnabled" }
            };

            // Act
            await _service.QueueInvalidationAsync(keyHash, @event, CacheType.VirtualKey);
            var stats = await _service.GetStatsAsync();

            // Assert
            Assert.Equal(1, stats.TotalQueued);
            Assert.Equal(1, stats.ByType[CacheType.VirtualKey].Queued);
        }

        [Fact]
        public async Task ProcessBatch_Should_Respect_MaxBatchSize()
        {
            // Arrange
            var options = new BatchInvalidationOptions 
            { 
                MaxBatchSize = 50,
                Enabled = true,
                BatchWindow = TimeSpan.FromSeconds(10) // Long window to prevent auto-processing
            };
            _service.Configure(options);

            var requests = Enumerable.Range(1, 150)
                .Select(i => new VirtualKeyCreated
                {
                    KeyId = i,
                    KeyHash = $"key-{i}",
                    KeyName = $"Key {i}"
                });

            // Act
            foreach (var request in requests)
            {
                await _service.QueueInvalidationAsync(request.KeyHash, request, CacheType.VirtualKey);
            }
            
            await _service.FlushAsync(); // Force immediate processing

            // Assert
            // Verify that InvalidateVirtualKeyAsync was called exactly 150 times
            // (batched into groups of 50, but ultimately each key is invalidated)
            _mockVirtualKeyCache.Verify(x => x.InvalidateVirtualKeyAsync(It.IsAny<string>()), Times.Exactly(150));
        }

        [Fact]
        public async Task Deduplication_Should_Remove_Duplicate_Requests()
        {
            // Arrange
            _service.Configure(new BatchInvalidationOptions 
            { 
                EnableCoalescing = true,
                Enabled = true,
                BatchWindow = TimeSpan.FromSeconds(10)
            });

            var @event = new VirtualKeyUpdated
            {
                KeyId = 1,
                KeyHash = "key1",
                ChangedProperties = new[] { "MaxBudget" }
            };

            // Act - Queue the same key multiple times
            await _service.QueueInvalidationAsync("key1", @event, CacheType.VirtualKey);
            await _service.QueueInvalidationAsync("key1", @event, CacheType.VirtualKey);
            await _service.QueueInvalidationAsync("key1", @event, CacheType.VirtualKey);
            await _service.QueueInvalidationAsync("key2", @event, CacheType.VirtualKey);

            await _service.FlushAsync();

            // Assert - Only 2 unique keys should be invalidated
            _mockVirtualKeyCache.Verify(x => x.InvalidateVirtualKeyAsync("key1"), Times.Once);
            _mockVirtualKeyCache.Verify(x => x.InvalidateVirtualKeyAsync("key2"), Times.Once);
            _mockVirtualKeyCache.Verify(x => x.InvalidateVirtualKeyAsync(It.IsAny<string>()), Times.Exactly(2));

            var stats = await _service.GetStatsAsync();
            Assert.Equal(4, stats.TotalQueued);
            Assert.Equal(2, stats.TotalProcessed);
            Assert.Equal(2, stats.TotalCoalesced); // 2 duplicates were coalesced
        }

        [Fact]
        public async Task Critical_Priority_Should_Trigger_Immediate_Processing()
        {
            // Arrange
            var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _mockVirtualKeyCache
                .Setup(x => x.InvalidateVirtualKeyAsync(It.IsAny<string>()))
                .Callback(() => processed.TrySetResult())
                .Returns(Task.CompletedTask);

            var @event = new VirtualKeyDeleted // Critical priority event
            {
                KeyId = 1,
                KeyHash = "critical-key",
                KeyName = "Critical Key"
            };

            // Act
            await _service.QueueInvalidationAsync(@event.KeyHash, @event, CacheType.VirtualKey);

            // Assert - Should process immediately without waiting for batch window
            await processed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task QueueBulkInvalidationAsync_Should_Queue_Multiple_Requests()
        {
            // Arrange
            var keys = new[] { "key1", "key2", "key3", "key4", "key5" };
            var @event = new ModelCostChanged
            {
                ModelCostId = 1,
                CostName = "Test Cost",
                ChangeType = "Updated"
            };

            // Act
            await _service.QueueBulkInvalidationAsync(keys, @event, CacheType.ModelCost);
            var stats = await _service.GetStatsAsync();

            // Assert
            Assert.Equal(5, stats.TotalQueued);
            Assert.Equal(5, stats.ByType[CacheType.ModelCost].Queued);
        }

        [Fact]
        public async Task Disabled_Service_Should_Process_Directly()
        {
            // Arrange
            _service.Configure(new BatchInvalidationOptions { Enabled = false });
            
            var @event = new VirtualKeyCreated
            {
                KeyId = 1,
                KeyHash = "direct-key",
                KeyName = "Direct Key"
            };

            // Act
            await _service.QueueInvalidationAsync(@event.KeyHash, @event, CacheType.VirtualKey);

            // Assert - Should call cache directly without batching
            _mockVirtualKeyCache.Verify(x => x.InvalidateVirtualKeyAsync(@event.KeyHash), Times.Once);
        }

        [Fact]
        public async Task GetQueueStatsAsync_Should_Return_Current_Queue_Depth()
        {
            // Arrange
            var events = Enumerable.Range(1, 10)
                .Select(i => new VirtualKeyCreated
                {
                    KeyId = i,
                    KeyHash = $"key-{i}",
                    KeyName = $"Key {i}"
                });

            // Act
            foreach (var e in events)
            {
                await _service.QueueInvalidationAsync(e.KeyHash, e, CacheType.VirtualKey);
            }

            var stats = await _service.GetQueueStatsAsync();

            // Assert
            Assert.Equal(10, stats.TotalQueueDepth);
            Assert.Equal(10, stats.QueueDepthByType[CacheType.VirtualKey]);
        }

        [Fact]
        public async Task Service_Should_Handle_Exceptions_Gracefully()
        {
            // Arrange
            _mockVirtualKeyCache
                .Setup(x => x.InvalidateVirtualKeyAsync(It.IsAny<string>()))
                .ThrowsAsync(new Exception("Redis connection failed"));

            var @event = new VirtualKeyCreated
            {
                KeyId = 1,
                KeyHash = "error-key",
                KeyName = "Error Key"
            };

            // Act
            await _service.QueueInvalidationAsync(@event.KeyHash, @event, CacheType.VirtualKey);
            await _service.FlushAsync();

            // Assert
            var stats = await _service.GetStatsAsync();
            Assert.Equal(1, stats.ByType[CacheType.VirtualKey].Errors);
            
            // Verify error was logged
            _mockLogger.Verify(
                x => x.Log(
                    LogLevel.Error,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to process batch")),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.Once);
        }

        [Fact]
        public async Task BatchWindow_Should_Trigger_Processing()
        {
            // Arrange
            var options = new BatchInvalidationOptions
            {
                Enabled = true,
                BatchWindow = TimeSpan.FromMilliseconds(100),
                MaxBatchSize = 100
            };
            _service.Configure(options);

            var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _mockVirtualKeyCache
                .Setup(x => x.InvalidateVirtualKeyAsync(It.IsAny<string>()))
                .Callback(() => processed.TrySetResult())
                .Returns(Task.CompletedTask);

            var @event = new VirtualKeyCreated
            {
                KeyId = 1,
                KeyHash = "timed-key",
                KeyName = "Timed Key"
            };

            // Start the service
            var cts = new CancellationTokenSource();
            var serviceTask = _service.StartAsync(cts.Token);
            await _clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Act
            await _service.QueueInvalidationAsync(@event.KeyHash, @event, CacheType.VirtualKey);

            Assert.False(processed.Task.IsCompleted);
            _clock.Advance(options.BatchWindow);
            await processed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Cleanup
            cts.Cancel();
            await _service.StopAsync(CancellationToken.None);
        }
    }
}
