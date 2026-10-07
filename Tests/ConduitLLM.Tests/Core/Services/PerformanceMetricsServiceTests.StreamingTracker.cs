using AwesomeAssertions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;

namespace ConduitLLM.Tests.Core.Services
{
    public partial class PerformanceMetricsServiceTests
    {
        [Fact]
        public void CreateStreamingTracker_CreatesValidTracker()
        {
            // Act
            var tracker = _service.CreateStreamingTracker("OpenAI", "gpt-4");

            // Assert
            Assert.NotNull(tracker);
            tracker.Should().BeAssignableTo<IStreamingMetricsTracker>();
        }

        [Fact]
        public void StreamingTracker_RecordFirstToken_RecordsTimeToFirstToken()
        {
            // Arrange
            var tracker = _service.CreateStreamingTracker("OpenAI", "gpt-4");
            _clock.Advance(TimeSpan.FromMilliseconds(50));

            // Act
            tracker.RecordFirstToken();
            var metrics = tracker.GetMetrics();

            // Assert
            Assert.NotNull(metrics.TimeToFirstTokenMs);
            Assert.True(metrics.TimeToFirstTokenMs >= 50);
            Assert.Equal("OpenAI", metrics.Provider);
            Assert.Equal("gpt-4", metrics.Model);
            Assert.True(metrics.Streaming);
        }

        [Fact]
        public void StreamingTracker_RecordFirstToken_OnlyRecordsOnce()
        {
            // Arrange
            var tracker = _service.CreateStreamingTracker("OpenAI", "gpt-4");
            
            // Act
            tracker.RecordFirstToken();
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            tracker.RecordFirstToken(); // Should be ignored
            var metrics = tracker.GetMetrics();

            // Assert
            Assert.NotNull(metrics.TimeToFirstTokenMs);
            Assert.True(metrics.TimeToFirstTokenMs < 100);
        }

        [Fact]
        public void StreamingTracker_RecordToken_TracksTokens()
        {
            // Arrange
            var tracker = _service.CreateStreamingTracker("OpenAI", "gpt-4");
            tracker.RecordFirstToken();

            // Act
            for (int i = 0; i < 10; i++)
            {
                _clock.Advance(TimeSpan.FromMilliseconds(10));
                tracker.RecordToken();
            }
            var metrics = tracker.GetMetrics();

            // Assert
            Assert.NotNull(metrics.TokensPerSecond);
            Assert.True(metrics.TokensPerSecond > 0);
        }

        [Fact]
        public void StreamingTracker_CalculatesInterTokenLatency()
        {
            // Arrange
            var tracker = _service.CreateStreamingTracker("OpenAI", "gpt-4");
            tracker.RecordFirstToken();

            // Act
            for (int i = 0; i < 5; i++)
            {
                _clock.Advance(TimeSpan.FromMilliseconds(20));
                tracker.RecordToken();
            }
            var metrics = tracker.GetMetrics();

            // Assert
            Assert.NotNull(metrics.AvgInterTokenLatencyMs);
            // Allow wider tolerance for timing-sensitive tests due to thread scheduling and system load
            Assert.True(metrics.AvgInterTokenLatencyMs >= 10, $"Inter-token latency {metrics.AvgInterTokenLatencyMs}ms was less than minimum expected 10ms");
            Assert.True(metrics.AvgInterTokenLatencyMs <= 100, $"Inter-token latency {metrics.AvgInterTokenLatencyMs}ms exceeded maximum expected 100ms");
        }

        [Fact]
        public void StreamingTracker_NoTokens_ReturnsBasicMetrics()
        {
            // Arrange
            var tracker = _service.CreateStreamingTracker("OpenAI", "gpt-4");

            // Act
            _clock.Advance(TimeSpan.FromMilliseconds(10));
            var metrics = tracker.GetMetrics();

            // Assert
            Assert.True(metrics.TotalLatencyMs >= 10);
            Assert.Null(metrics.TimeToFirstTokenMs);
            Assert.Null(metrics.TokensPerSecond);
            Assert.Null(metrics.AvgInterTokenLatencyMs);
        }

        [Fact]
        public void StreamingTracker_WithUsageData_UsesActualTokenCounts()
        {
            // Arrange
            var tracker = _service.CreateStreamingTracker("OpenAI", "gpt-4");
            tracker.RecordFirstToken();
            tracker.RecordToken();
            tracker.RecordToken();

            var usage = new Usage
            {
                PromptTokens = 100,
                CompletionTokens = 50,
                TotalTokens = 150
            };

            // Act
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            var metrics = tracker.GetMetrics(usage);

            // Assert
            Assert.NotNull(metrics.TokensPerSecond);
            Assert.NotNull(metrics.CompletionTokensPerSecond);
            Assert.Equal(metrics.TokensPerSecond, metrics.CompletionTokensPerSecond);
            // Should use usage.CompletionTokens (50) not our count (3)
            Assert.True(metrics.TokensPerSecond > 100); // 50 tokens in ~100ms
        }

        [Fact]
        public void StreamingTracker_PromptTokensPerSecond_RequiresTimeToFirstToken()
        {
            // Arrange
            var tracker = _service.CreateStreamingTracker("OpenAI", "gpt-4");
            // Don't record first token
            
            var usage = new Usage
            {
                PromptTokens = 100,
                CompletionTokens = 50,
                TotalTokens = 150
            };

            // Act
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            var metrics = tracker.GetMetrics(usage);

            // Assert
            Assert.Null(metrics.PromptTokensPerSecond);
            Assert.NotNull(metrics.TokensPerSecond); // Completion tokens should still work
        }

        [Fact]
        public void StreamingTracker_VeryFastTokenGeneration_HandlesHighThroughput()
        {
            // Arrange
            var tracker = _service.CreateStreamingTracker("OpenAI", "gpt-4");
            tracker.RecordFirstToken();

            // Act - Record many tokens very quickly
            for (int i = 0; i < 100; i++)
            {
                tracker.RecordToken();
            }
            _clock.Advance(TimeSpan.FromMilliseconds(10)); // Ensure some elapsed time
            var metrics = tracker.GetMetrics();

            // Assert
            Assert.NotNull(metrics.TokensPerSecond);
            Assert.True(metrics.TokensPerSecond > 1000); // Should be very high
        }

        [Fact]
        public void StreamingTracker_SingleToken_NoInterTokenLatency()
        {
            // Arrange
            var tracker = _service.CreateStreamingTracker("OpenAI", "gpt-4");
            
            // Act
            tracker.RecordFirstToken();
            var metrics = tracker.GetMetrics();

            // Assert
            Assert.Null(metrics.AvgInterTokenLatencyMs);
        }

        [Fact]
        public void StreamingTracker_MultipleCalls_StopsTimerOnFirstGetMetrics()
        {
            // Arrange
            var tracker = _service.CreateStreamingTracker("OpenAI", "gpt-4");
            tracker.RecordFirstToken();

            // Act
            var firstMetrics = tracker.GetMetrics();
            _clock.Advance(TimeSpan.FromMilliseconds(100)); // Wait
            var secondMetrics = tracker.GetMetrics();

            // Assert
            Assert.Equal(firstMetrics.TotalLatencyMs, secondMetrics.TotalLatencyMs);
        }

        [Fact]
        public void StreamingTracker_FirstTokenAtZero_PreservesFirstInterval()
        {
            var tracker = _service.CreateStreamingTracker("OpenAI", "gpt-4");
            tracker.RecordFirstToken();
            _clock.Advance(TimeSpan.FromMilliseconds(10));
            tracker.RecordToken();
            _clock.Advance(TimeSpan.FromMilliseconds(30));
            tracker.RecordToken();
            Assert.Equal(20, tracker.GetMetrics().AvgInterTokenLatencyMs);
        }

        [Fact(Skip = "StreamingMetricsTracker is not thread-safe by design - it's meant to be used from a single streaming context")]
        public async Task StreamingTracker_ConcurrentRecording_NotThreadSafe()
        {
            // This test documents that the tracker is not thread-safe
            // In real usage, tokens should be recorded from a single thread
            
            // Arrange
            var tracker = _service.CreateStreamingTracker("OpenAI", "gpt-4");
            tracker.RecordFirstToken();
            
            const int threadCount = 10;
            const int tokensPerThread = 100;
            var tasks = new Task[threadCount];

            // Act
            for (int i = 0; i < threadCount; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    for (int j = 0; j < tokensPerThread; j++)
                    {
                        tracker.RecordToken();
                    }
                });
            }
            
            await Task.WhenAll(tasks);
            var metrics = tracker.GetMetrics();

            // Assert
            // The actual token count might not match expected due to race conditions
            // This is expected behavior - the tracker is designed for single-threaded use
            Assert.NotNull(metrics.TokensPerSecond);
        }
    }
}
