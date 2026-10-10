using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Middleware
{
    /// <summary>
    /// Base class for HTTP metrics middleware that provides the common request processing
    /// pipeline. Subclasses own Prometheus metric definitions and recording (metrics are
    /// static and names must differ between services).
    /// </summary>
    public abstract class HttpMetricsMiddlewareBase
    {
        private readonly RequestDelegate _next;
        protected readonly ILogger Logger;

        protected HttpMetricsMiddlewareBase(RequestDelegate next, ILogger logger)
        {
            _next = next;
            Logger = logger;
        }

        /// <summary>
        /// Returns a normalized path for metric labels to reduce cardinality.
        /// </summary>
        protected abstract string GetNormalizedPath(HttpContext context);

        /// <summary>
        /// Returns true if metrics should be skipped for this request (e.g., health checks).
        /// </summary>
        protected abstract bool ShouldSkipMetrics(HttpContext context);

        /// <summary>
        /// Increments the active request gauge.
        /// </summary>
        protected abstract void IncrementActiveRequests(string method, string path);

        /// <summary>
        /// Decrements the active request gauge.
        /// </summary>
        protected abstract void DecrementActiveRequests(string method, string path);

        /// <summary>
        /// Records the request size metric.
        /// </summary>
        protected abstract void RecordRequestSize(string method, string path, long bytes);

        /// <summary>
        /// Records response metrics (request count, duration, response size, etc.).
        /// </summary>
        protected abstract void RecordResponseMetrics(
            string method, string path, int statusCode, double durationSeconds,
            long responseBytes, HttpContext context);

        /// <summary>
        /// Records an error metric.
        /// </summary>
        protected abstract void RecordError(string method, string path, int statusCode, string errorType);

        /// <summary>
        /// Called when an unhandled exception occurs. Override to adjust status code (e.g., 200 → 500).
        /// </summary>
        protected virtual void OnException(HttpContext context) { }

        /// <summary>
        /// Threshold in seconds for slow request warnings. 0 disables.
        /// </summary>
        protected virtual double SlowRequestWarningThresholdSeconds => 5.0;

        public async Task InvokeAsync(HttpContext context)
        {
            if (ShouldSkipMetrics(context))
            {
                await _next(context);
                return;
            }

            var path = GetNormalizedPath(context);
            var method = context.Request.Method;

            if (context.Request.ContentLength.HasValue)
            {
                RecordRequestSize(method, path, context.Request.ContentLength.Value);
            }

            var stopwatch = Stopwatch.StartNew();
            IncrementActiveRequests(method, path);

            var originalBodyStream = context.Response.Body;
            using var countingStream = new CountingStream(originalBodyStream);
            context.Response.Body = countingStream;

            try
            {
                await _next(context);
            }
            catch (TaskCanceledException)
            {
                context.Response.StatusCode = 499; // Client closed request
                RecordError(method, path, 499, "client_cancelled");
                throw;
            }
            catch (Exception ex)
            {
                var errorType = ex.GetType().Name;
                RecordError(method, path, context.Response.StatusCode, errorType);
                // The outer exception middleware owns logging and chooses the severity
                // after mapping the response. Metrics must not log the exception again.
                OnException(context);
                throw;
            }
            finally
            {
                context.Response.Body = originalBodyStream;
                DecrementActiveRequests(method, path);
                stopwatch.Stop();

                RecordResponseMetrics(
                    method, path, context.Response.StatusCode,
                    stopwatch.Elapsed.TotalSeconds, countingStream.BytesWritten, context);

                if (SlowRequestWarningThresholdSeconds > 0 && stopwatch.Elapsed.TotalSeconds > SlowRequestWarningThresholdSeconds)
                {
                    Logger.LogWarning(
                        "Slow request detected: {Method} {Path} took {Duration:F2}s with status {StatusCode}",
                        method, path, stopwatch.Elapsed.TotalSeconds, context.Response.StatusCode.ToString());
                }
            }
        }
    }
}
