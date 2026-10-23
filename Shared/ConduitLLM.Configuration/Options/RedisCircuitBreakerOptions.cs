using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ConduitLLM.Configuration.Options
{
    /// <summary>
    /// Configuration options for the Redis circuit breaker
    /// </summary>
    public class RedisCircuitBreakerOptions : IValidatableObject
    {
        /// <summary>
        /// Number of consecutive failures required to trip the circuit
        /// Default: 5
        /// </summary>
        [Range(1, 100)]
        public int FailureThreshold { get; set; } = 5;

        /// <summary>
        /// Time window in seconds for counting failures
        /// Default: 60 seconds
        /// </summary>
        [Range(1, 600)]
        public int FailureTimeWindowSeconds { get; set; } = 60;

        /// <summary>
        /// Duration in seconds the circuit remains open before attempting recovery
        /// Default: 30 seconds
        /// </summary>
        [Range(5, 300)]
        public int OpenDurationSeconds { get; set; } = 30;

        /// <summary>
        /// Number of successful operations required in half-open state to close the circuit
        /// Default: 3
        /// </summary>
        [Range(1, 10)]
        public int HalfOpenSuccessesRequired { get; set; } = 3;

        /// <summary>
        /// Maximum number of test operations allowed in half-open state
        /// Default: 5
        /// </summary>
        [Range(1, 20)]
        public int HalfOpenMaxAttempts { get; set; } = 5;

        /// <summary>
        /// Paths that bypass the circuit breaker (e.g., health checks, metrics, API documentation)
        /// </summary>
        public List<string> BypassPaths { get; set; } = new()
        {
            "/health",
            "/health/live",
            "/health/ready",
            "/metrics",
            "/openapi",
            "/scalar"
        };

        /// <summary>
        /// Whether to allow manual control of the circuit breaker (trip/reset)
        /// Default: true
        /// </summary>
        public bool EnableManualControl { get; set; } = true;

        /// <summary>
        /// Whether to automatically reset the circuit on application startup
        /// Default: true
        /// </summary>
        public bool ResetOnStartup { get; set; } = true;

        /// <summary>
        /// Whether to include detailed error information in responses
        /// Default: false (for security)
        /// </summary>
        public bool IncludeErrorDetails { get; set; } = false;

        /// <summary>
        /// Custom error message to return when circuit is open
        /// </summary>
        public string OpenCircuitMessage { get; set; } = "Service temporarily unavailable due to high error rate. Please try again later.";

        /// <summary>
        /// Whether to log detailed circuit breaker events
        /// Default: true
        /// </summary>
        public bool EnableDetailedLogging { get; set; } = true;

        /// <summary>
        /// Minimum time in milliseconds between health check probes
        /// Default: 5000 (5 seconds)
        /// </summary>
        [Range(1000, 60000)]
        public int HealthCheckIntervalMilliseconds { get; set; } = 5000;

        /// <summary>
        /// Timeout in milliseconds for Redis operations
        /// Default: 5000 (5 seconds)
        /// </summary>
        [Range(100, 30000)]
        public int OperationTimeoutMilliseconds { get; set; } = 5000;

        /// <summary>
        /// Gets the failure time window as a TimeSpan
        /// </summary>
        public TimeSpan GetFailureTimeWindow() => TimeSpan.FromSeconds(FailureTimeWindowSeconds);

        /// <summary>
        /// Gets the open duration as a TimeSpan
        /// </summary>
        public TimeSpan GetOpenDuration() => TimeSpan.FromSeconds(OpenDurationSeconds);

        /// <summary>
        /// Gets the health check interval as a TimeSpan
        /// </summary>
        public TimeSpan GetHealthCheckInterval() => TimeSpan.FromMilliseconds(HealthCheckIntervalMilliseconds);

        /// <summary>
        /// Gets the operation timeout as a TimeSpan
        /// </summary>
        public TimeSpan GetOperationTimeout() => TimeSpan.FromMilliseconds(OperationTimeoutMilliseconds);

        /// <summary>
        /// Validates the configuration
        /// </summary>
        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (HalfOpenSuccessesRequired > HalfOpenMaxAttempts)
            {
                yield return new ValidationResult(
                    $"HalfOpenSuccessesRequired ({HalfOpenSuccessesRequired}) cannot be greater than HalfOpenMaxAttempts ({HalfOpenMaxAttempts})",
                    new[] { nameof(HalfOpenSuccessesRequired), nameof(HalfOpenMaxAttempts) });
            }

            if (OpenDurationSeconds < 5)
            {
                yield return new ValidationResult(
                    "OpenDurationSeconds must be at least 5 seconds to prevent rapid cycling",
                    new[] { nameof(OpenDurationSeconds) });
            }

            if (BypassPaths != null)
            {
                foreach (var path in BypassPaths)
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        yield return new ValidationResult(
                            "BypassPaths cannot contain null or empty strings",
                            new[] { nameof(BypassPaths) });
                    }
                }
            }
        }
    }
}
