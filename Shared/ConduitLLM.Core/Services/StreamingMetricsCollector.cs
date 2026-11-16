using System.Diagnostics;
using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Collects and tracks performance metrics during streaming operations.
    /// </summary>
    /// <remarks>Owned by one sequential streaming context; concurrent recording is not supported.</remarks>
    public class StreamingMetricsCollector
    {
        private readonly string _requestId;
        private readonly Stopwatch _stopwatch;
        private readonly string _model;
        private readonly string _provider;
        
        private long? _timeToFirstTokenMs;
        private int _tokensGenerated;
        private long _recordedTimestampCount;
        private long _firstRecordedTimestampMs;
        private long _lastRecordedTimestampMs;
        private DateTime _lastEmissionTime;
        private readonly TimeSpan _emissionInterval;

        public StreamingMetricsCollector(
            string requestId, 
            string model, 
            string provider,
            TimeSpan? emissionInterval = null)
        {
            _requestId = requestId ?? throw new ArgumentNullException(nameof(requestId));
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _stopwatch = Stopwatch.StartNew();
            _lastEmissionTime = DateTime.UtcNow;
            _emissionInterval = emissionInterval ?? TimeSpan.FromSeconds(1);
        }

        /// <summary>
        /// Whether the first-token marker was recorded. Ordinary token records do not set this marker.
        /// </summary>
        public bool HasRecordedFirstToken => _timeToFirstTokenMs.HasValue;

        /// <summary>
        /// Records that the first token has been received.
        /// </summary>
        public void RecordFirstToken()
        {
            if (!_timeToFirstTokenMs.HasValue)
            {
                _timeToFirstTokenMs = _stopwatch.ElapsedMilliseconds;
                RecordTimestamp(_timeToFirstTokenMs.Value);
                _tokensGenerated = 1;
            }
        }

        /// <summary>
        /// Records that a token has been generated.
        /// </summary>
        public void RecordToken()
        {
            _tokensGenerated++;
            RecordTimestamp(_stopwatch.ElapsedMilliseconds);
        }

        private void RecordTimestamp(long elapsedMs)
        {
            if (_recordedTimestampCount == 0)
                _firstRecordedTimestampMs = elapsedMs;
            _lastRecordedTimestampMs = elapsedMs;
            _recordedTimestampCount++;
        }

        // Adjacent intervals telescope to last minus first. Timestamp count is independent
        // of TokensGenerated: RecordFirstToken historically resets that reported count even
        // when ordinary tokens have already been recorded, while retaining their intervals.
        private double? AverageInterTokenLatencyMs => _recordedTimestampCount > 1
            ? (_lastRecordedTimestampMs - _firstRecordedTimestampMs) / (double)(_recordedTimestampCount - 1)
            : null;

        /// <summary>
        /// Determines if metrics should be emitted based on the emission interval.
        /// </summary>
        public bool ShouldEmitMetrics()
        {
            var now = DateTime.UtcNow;
            if (now - _lastEmissionTime >= _emissionInterval)
            {
                _lastEmissionTime = now;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Gets the current streaming metrics.
        /// </summary>
        public StreamingMetrics GetMetrics()
        {
            var elapsedMs = _stopwatch.ElapsedMilliseconds;
            var elapsedSeconds = elapsedMs / 1000.0;
            
            var metrics = new StreamingMetrics
            {
                RequestId = _requestId,
                ElapsedMs = elapsedMs,
                TokensGenerated = _tokensGenerated,
                TimeToFirstTokenMs = _timeToFirstTokenMs,
                CurrentTokensPerSecond = elapsedSeconds > 0 ? _tokensGenerated / elapsedSeconds : 0
            };

            metrics.AvgInterTokenLatencyMs = AverageInterTokenLatencyMs;

            return metrics;
        }

        /// <summary>
        /// Gets the final performance metrics.
        /// </summary>
        public PerformanceMetrics GetFinalMetrics(Usage? usage = null)
        {
            _stopwatch.Stop();

            var totalSeconds = _stopwatch.Elapsed.TotalSeconds;
            var metrics = new PerformanceMetrics
            {
                TotalLatencyMs = _stopwatch.ElapsedMilliseconds,
                TimeToFirstTokenMs = _timeToFirstTokenMs,
                Provider = _provider,
                Model = _model,
                Streaming = true
            };

            // ⚠️ IMPORTANT: Token counting fallback logic
            // ===========================================
            // PREFERRED: Use actual token count from provider usage data (accurate)
            // FALLBACK: Use _tokensGenerated (INACCURATE - counts chunks, not tokens!)
            //
            // With stream_options.include_usage=true, most providers now return usage data.
            // If usage is null here, it means the provider doesn't support stream_options
            // or there was an error capturing the usage from the final chunk.
            var completionTokens = usage?.CompletionTokens ?? _tokensGenerated;
            
            // For streaming metrics, we always calculate if we have tokens
            // This ensures tests and real scenarios work correctly
            if (totalSeconds > 0 && completionTokens > 0)
            {
                // Always calculate tokens/second for streaming responses
                metrics.TokensPerSecond = completionTokens / totalSeconds;
                
                // For CompletionTokensPerSecond, exclude prompt processing time if available
                // Generation time = total time - prompt processing time
                if (_timeToFirstTokenMs.HasValue)
                {
                    var generationSeconds = totalSeconds - (_timeToFirstTokenMs.Value / 1000.0);
                    if (generationSeconds > 0)
                    {
                        metrics.CompletionTokensPerSecond = completionTokens / generationSeconds;
                    }
                    else
                    {
                        // Fallback if generation time is too small or negative
                        metrics.CompletionTokensPerSecond = completionTokens / totalSeconds;
                    }
                }
                else
                {
                    // No time to first token recorded, use total time
                    metrics.CompletionTokensPerSecond = completionTokens / totalSeconds;
                }
            }

            // Calculate prompt tokens per second if we have usage data
            if (usage?.PromptTokens != null && _timeToFirstTokenMs.HasValue)
            {
                var promptProcessingSeconds = _timeToFirstTokenMs.Value / 1000.0;
                if (promptProcessingSeconds > 0)
                {
                    metrics.PromptTokensPerSecond = usage.PromptTokens / promptProcessingSeconds;
                }
            }

            metrics.AvgInterTokenLatencyMs = AverageInterTokenLatencyMs;

            // Add token usage data if available
            if (usage != null)
            {
                metrics.PromptTokens = usage.PromptTokens;
                metrics.CompletionTokens = usage.CompletionTokens;
                metrics.TotalTokens = usage.TotalTokens;
            }

            return metrics;
        }
    }

    /// <summary>
    /// Represents metrics collected during streaming operations.
    /// </summary>
    public class StreamingMetrics
    {
        /// <summary>
        /// Unique identifier for the request.
        /// </summary>
        public string RequestId { get; set; } = string.Empty;

        /// <summary>
        /// Total elapsed time in milliseconds.
        /// </summary>
        public long ElapsedMs { get; set; }

        /// <summary>
        /// Number of tokens generated so far.
        /// </summary>
        public int TokensGenerated { get; set; }

        /// <summary>
        /// Current tokens per second rate.
        /// </summary>
        public double CurrentTokensPerSecond { get; set; }

        /// <summary>
        /// Time to first token in milliseconds.
        /// </summary>
        public long? TimeToFirstTokenMs { get; set; }

        /// <summary>
        /// Average inter-token latency in milliseconds.
        /// </summary>
        public double? AvgInterTokenLatencyMs { get; set; }

        /// <summary>
        /// Number of tokens in the prompt (from provider usage data).
        /// </summary>
        public int? PromptTokens { get; set; }

        /// <summary>
        /// Number of tokens in the completion (from provider usage data).
        /// </summary>
        public int? CompletionTokens { get; set; }

        /// <summary>
        /// Total number of tokens used (from provider usage data).
        /// </summary>
        public int? TotalTokens { get; set; }
    }
}
