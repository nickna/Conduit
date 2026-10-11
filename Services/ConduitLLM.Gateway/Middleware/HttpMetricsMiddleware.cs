using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Middleware;
using Prometheus;

namespace ConduitLLM.Gateway.Middleware
{
    /// <summary>
    /// Middleware for tracking HTTP request metrics using Prometheus.
    /// Provides comprehensive metrics for monitoring Gateway API performance at scale.
    /// </summary>
    public class HttpMetricsMiddleware : HttpMetricsMiddlewareBase
    {
        // Prometheus metrics
        private static readonly Counter RequestsTotal = Prometheus.Metrics
            .CreateCounter("conduit_http_requests_total", "Total number of HTTP requests",
                new CounterConfiguration
                {
                    LabelNames = new[] { "method", "endpoint", "status_code", "virtual_key_id" }
                });

        private static readonly Histogram RequestDuration = Prometheus.Metrics
            .CreateHistogram("conduit_http_request_duration_seconds", "HTTP request duration in seconds",
                new HistogramConfiguration
                {
                    LabelNames = new[] { "method", "endpoint", "status_code" },
                    Buckets = Histogram.ExponentialBuckets(0.001, 2, 16) // 1ms to ~65s
                });

        private static readonly Gauge ActiveRequests = Prometheus.Metrics
            .CreateGauge("conduit_http_requests_active", "Number of active HTTP requests",
                new GaugeConfiguration
                {
                    LabelNames = new[] { "method", "endpoint" }
                });

        private static readonly Histogram RequestSize = Prometheus.Metrics
            .CreateHistogram("conduit_http_request_size_bytes", "HTTP request size in bytes",
                new HistogramConfiguration
                {
                    LabelNames = new[] { "method", "endpoint" },
                    Buckets = Histogram.ExponentialBuckets(100, 2, 16) // 100 bytes to ~6.5MB
                });

        private static readonly Histogram ResponseSize = Prometheus.Metrics
            .CreateHistogram("conduit_http_response_size_bytes", "HTTP response size in bytes",
                new HistogramConfiguration
                {
                    LabelNames = new[] { "method", "endpoint", "status_code" },
                    Buckets = Histogram.ExponentialBuckets(100, 2, 16) // 100 bytes to ~6.5MB
                });

        private static readonly Counter RateLimitHits = Prometheus.Metrics
            .CreateCounter("conduit_rate_limit_exceeded_total", "Total number of rate limit exceeded responses",
                new CounterConfiguration
                {
                    LabelNames = new[] { "endpoint", "virtual_key_id" }
                });

        private static readonly Summary RequestDurationSummary = Prometheus.Metrics
            .CreateSummary("conduit_http_request_duration_summary", "Summary of HTTP request durations",
                new SummaryConfiguration
                {
                    LabelNames = new[] { "method", "endpoint" },
                    Objectives = new[]
                    {
                        new QuantileEpsilonPair(0.5, 0.05),   // p50
                        new QuantileEpsilonPair(0.9, 0.01),   // p90
                        new QuantileEpsilonPair(0.95, 0.005), // p95
                        new QuantileEpsilonPair(0.99, 0.001)  // p99
                    },
                    MaxAge = TimeSpan.FromMinutes(5),
                    AgeBuckets = 5
                });

        private static readonly Counter ErrorsTotal = Prometheus.Metrics
            .CreateCounter("conduit_http_errors_total", "Total number of HTTP errors",
                new CounterConfiguration
                {
                    LabelNames = new[] { "method", "endpoint", "status_code", "error_type" }
                });

        public HttpMetricsMiddleware(RequestDelegate next, ILogger<HttpMetricsMiddleware> logger)
            : base(next, logger) { }

        protected override bool ShouldSkipMetrics(HttpContext context)
        {
            return context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
        }

        protected override string GetNormalizedPath(HttpContext context)
        {
            var pathValue = context.Request.Path.Value ?? "/";

            // Replace GUIDs with {id}
            pathValue = System.Text.RegularExpressions.Regex.Replace(
                pathValue,
                @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
                "{id}");

            // Replace numeric IDs with {id}
            pathValue = System.Text.RegularExpressions.Regex.Replace(
                pathValue,
                @"\/\d+",
                "/{id}");

            // Common API endpoints
            if (pathValue.StartsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
                return "/v1/chat/completions";
            if (pathValue.StartsWith("/v1/conduit/chat/completions", StringComparison.OrdinalIgnoreCase))
                return "/v1/conduit/chat/completions";
            if (pathValue.StartsWith("/v1/embeddings", StringComparison.OrdinalIgnoreCase))
                return "/v1/embeddings";
            if (pathValue.StartsWith("/v1/models", StringComparison.OrdinalIgnoreCase))
                return "/v1/models";
            if (pathValue.StartsWith("/v1/images/generations", StringComparison.OrdinalIgnoreCase))
                return "/v1/images/generations";
            if (pathValue.StartsWith("/v1/conduit/videos/generations", StringComparison.OrdinalIgnoreCase))
                return "/v1/conduit/videos/generations";
            if (pathValue.StartsWith("/v1/audio", StringComparison.OrdinalIgnoreCase))
                return "/v1/audio/{operation}";

            return pathValue.ToLowerInvariant();
        }

        protected override void IncrementActiveRequests(string method, string path)
            => ActiveRequests.WithLabels(method, path).Inc();

        protected override void DecrementActiveRequests(string method, string path)
            => ActiveRequests.WithLabels(method, path).Dec();

        protected override void RecordRequestSize(string method, string path, long bytes)
            => RequestSize.WithLabels(method, path).Observe(bytes);

        protected override void RecordError(string method, string path, int statusCode, string errorType)
            => ErrorsTotal.WithLabels(method, path, statusCode.ToString(), errorType).Inc();

        protected override void OnException(HttpContext context)
        {
            if (context.Response.StatusCode == 200)
            {
                context.Response.StatusCode = 500;
            }
        }

        protected override void RecordResponseMetrics(
            string method, string path, int statusCode, double durationSeconds,
            long responseBytes, HttpContext context)
        {
            var statusCodeStr = statusCode.ToString();
            var virtualKeyId = GetVirtualKeyId(context);

            ResponseSize.WithLabels(method, path, statusCodeStr).Observe(responseBytes);
            RequestsTotal.WithLabels(method, path, statusCodeStr, virtualKeyId).Inc();
            RequestDuration.WithLabels(method, path, statusCodeStr).Observe(durationSeconds);
            RequestDurationSummary.WithLabels(method, path).Observe(durationSeconds);

            if (statusCode == 429)
            {
                RateLimitHits.WithLabels(path, virtualKeyId).Inc();
            }
        }

        private static string GetVirtualKeyId(HttpContext context)
        {
            var virtualKeyId = context.User?.FindFirst("VirtualKeyId")?.Value;
            if (!string.IsNullOrEmpty(virtualKeyId))
                return virtualKeyId;

            if (context.Items.TryGetValue("VirtualKeyId", out var keyId) && keyId is string strKeyId)
                return strKeyId;

            return "anonymous";
        }
    }
}
