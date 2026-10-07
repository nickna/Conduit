using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Service for calculating performance metrics for LLM operations.
    /// </summary>
    public class PerformanceMetricsService : IPerformanceMetricsService
    {
        private readonly TimeProvider _timeProvider;

        public PerformanceMetricsService(TimeProvider? timeProvider = null)
        {
            _timeProvider = timeProvider ?? TimeProvider.System;
        }
        /// <summary>
        /// Calculates performance metrics for a completed chat completion.
        /// </summary>
        public PerformanceMetrics CalculateMetrics(
            ChatCompletionResponse response,
            TimeSpan elapsedTime,
            string provider,
            string model,
            bool streaming = false,
            int retryAttempts = 0)
        {
            var metrics = new PerformanceMetrics
            {
                TotalLatencyMs = (long)elapsedTime.TotalMilliseconds,
                Provider = provider,
                Model = model,
                Streaming = streaming,
                RetryAttempts = retryAttempts
            };

            // Overall tokens per second is the only throughput figure we can measure here.
            // PromptTokensPerSecond / CompletionTokensPerSecond require a measured
            // prompt/generation time split, which only the streaming tracker has
            // (via time-to-first-token) — they stay null on this path.
            if (response.Usage != null && elapsedTime.TotalSeconds > 0
                && response.Usage.CompletionTokens > 0)
            {
                metrics.TokensPerSecond = response.Usage.CompletionTokens / elapsedTime.TotalSeconds;
            }

            return metrics;
        }

        /// <summary>
        /// Creates a streaming metrics tracker.
        /// </summary>
        public IStreamingMetricsTracker CreateStreamingTracker(string provider, string model)
        {
            return new StreamingMetricsTracker(provider, model, _timeProvider);
        }

        /// <summary>
        /// Implementation of streaming metrics tracker.
        /// </summary>
        private class StreamingMetricsTracker : IStreamingMetricsTracker
        {
            private readonly TimeProvider _timeProvider;
            private readonly long _startedAt;
            private TimeSpan? _completedElapsed;
            private TimeSpan Elapsed => _completedElapsed ?? _timeProvider.GetElapsedTime(_startedAt);
            private readonly string _provider;
            private readonly string _model;
            private long? _timeToFirstTokenMs;
            private int _tokenCount;
            private readonly List<long> _interTokenLatencies;
            private long? _lastTokenTime;

            public StreamingMetricsTracker(string provider, string model, TimeProvider timeProvider)
            {
                _provider = provider;
                _model = model;
                _timeProvider = timeProvider;
                _startedAt = _timeProvider.GetTimestamp();
                _interTokenLatencies = new List<long>();
                _tokenCount = 0;
            }

            public void RecordFirstToken()
            {
                if (!_timeToFirstTokenMs.HasValue)
                {
                    _timeToFirstTokenMs = (long)Elapsed.TotalMilliseconds;
                    _lastTokenTime = _timeToFirstTokenMs;
                    _tokenCount = 1;
                }
            }

            public void RecordToken()
            {
                var currentTime = (long)Elapsed.TotalMilliseconds;
                if (_lastTokenTime.HasValue)
                {
                    _interTokenLatencies.Add(currentTime - _lastTokenTime.Value);
                }
                _lastTokenTime = currentTime;
                _tokenCount++;
            }

            public PerformanceMetrics GetMetrics(Usage? usage = null)
            {
                _completedElapsed ??= Elapsed;
                
                var metrics = new PerformanceMetrics
                {
                    TotalLatencyMs = (long)Elapsed.TotalMilliseconds,
                    TimeToFirstTokenMs = _timeToFirstTokenMs,
                    Provider = _provider,
                    Model = _model,
                    Streaming = true
                };

                // Calculate average inter-token latency
                if (_interTokenLatencies.Any())
                {
                    metrics.AvgInterTokenLatencyMs = _interTokenLatencies.Average();
                }

                // Calculate tokens per second
                var totalSeconds = Elapsed.TotalSeconds;
                if (totalSeconds > 0)
                {
                    if (usage?.CompletionTokens != null && usage.CompletionTokens > 0)
                    {
                        // Use actual token count from usage if available
                        metrics.TokensPerSecond = usage.CompletionTokens / totalSeconds;
                        
                        // For CompletionTokensPerSecond, exclude prompt processing time
                        // Generation time = total time - prompt processing time
                        if (_timeToFirstTokenMs.HasValue)
                        {
                            var generationSeconds = totalSeconds - (_timeToFirstTokenMs.Value / 1000.0);
                            if (generationSeconds > 0)
                            {
                                metrics.CompletionTokensPerSecond = usage.CompletionTokens / generationSeconds;
                            }
                            else
                            {
                                // Fallback if generation time is too small
                                metrics.CompletionTokensPerSecond = usage.CompletionTokens / totalSeconds;
                            }
                        }
                        else
                        {
                            // No time to first token recorded, use total time
                            metrics.CompletionTokensPerSecond = usage.CompletionTokens / totalSeconds;
                        }
                    }
                    else if (_tokenCount > 0)
                    {
                        // Fall back to our counted tokens
                        metrics.TokensPerSecond = _tokenCount / totalSeconds;
                    }

                    // Estimate prompt processing speed if usage data is available
                    if (usage?.PromptTokens != null && usage.PromptTokens > 0 && _timeToFirstTokenMs.HasValue)
                    {
                        var promptProcessingSeconds = _timeToFirstTokenMs.Value / 1000.0;
                        if (promptProcessingSeconds > 0)
                        {
                            metrics.PromptTokensPerSecond = usage.PromptTokens / promptProcessingSeconds;
                        }
                    }
                }

                return metrics;
            }
        }
    }
}
