using Prometheus;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Persistence.Interfaces;

namespace ConduitLLM.Gateway.Services
{
    /// <summary>
    /// Service for tracking business metrics including virtual key usage,
    /// model usage patterns, costs, and revenue tracking.
    /// </summary>
    public class BusinessMetricsService : PeriodicCollectorBackgroundService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger<BusinessMetricsService> _logger;
        private readonly HashSet<string> _costRateLabels = [];
        private readonly HashSet<string> _activeModelLabels = [];

        // Virtual Key metrics
        private static readonly Counter VirtualKeyRequests = Prometheus.Metrics
            .CreateCounter("conduit_virtualkey_requests_total", "Total requests per virtual key",
                new CounterConfiguration
                {
                    LabelNames = new[] { "virtual_key_id", "model", "status" }
                });

        private static readonly Gauge VirtualKeySpendTotal = Prometheus.Metrics
            .CreateGauge("conduit_virtualkey_spend_total", "Total spend per virtual key",
                new GaugeConfiguration
                {
                    LabelNames = new[] { "virtual_key_id" }
                });

        private static readonly Gauge VirtualKeyBudgetUtilization = Prometheus.Metrics
            .CreateGauge("conduit_virtualkey_budget_utilization_percent", "Budget utilization percentage per virtual key",
                new GaugeConfiguration
                {
                    LabelNames = new[] { "virtual_key_id" }
                });

        private static readonly Counter VirtualKeyBudgetExceeded = Prometheus.Metrics
            .CreateCounter("conduit_virtualkey_budget_exceeded_total", "Number of times budget was exceeded",
                new CounterConfiguration
                {
                    LabelNames = new[] { "virtual_key_id" }
                });

        // Model usage metrics
        private static readonly Counter ModelRequests = Prometheus.Metrics
            .CreateCounter("conduit_model_requests_total", "Total requests per model",
                new CounterConfiguration
                {
                    LabelNames = new[] { "model", "provider", "status" }
                });

        private static readonly Histogram ModelResponseTime = Prometheus.Metrics
            .CreateHistogram("conduit_model_response_time_seconds", "Model response time",
                new HistogramConfiguration
                {
                    LabelNames = new[] { "model", "provider" },
                    Buckets = Histogram.ExponentialBuckets(0.1, 2, 14) // 0.1s to ~820s
                });

        private static readonly Counter ModelTokensProcessed = Prometheus.Metrics
            .CreateCounter("conduit_model_tokens_total", "Total tokens processed",
                new CounterConfiguration
                {
                    LabelNames = new[] { "model", "provider", "token_type" } // token_type: prompt, completion
                });

        // Cost tracking metrics
        private static readonly Counter CostTotal = Prometheus.Metrics
            .CreateCounter("conduit_cost_total_dollars", "Total cost in dollars",
                new CounterConfiguration
                {
                    LabelNames = new[] { "provider", "model", "operation_type" }
                });

        private static readonly Gauge CostRate = Prometheus.Metrics
            .CreateGauge("conduit_cost_rate_dollars_per_minute", "Cost rate in dollars per minute",
                new GaugeConfiguration
                {
                    LabelNames = new[] { "provider" }
                });

        private static readonly Histogram CostPerRequest = Prometheus.Metrics
            .CreateHistogram("conduit_cost_per_request_dollars", "Cost per request in dollars",
                new HistogramConfiguration
                {
                    LabelNames = new[] { "model", "provider" },
                    Buckets = new[] { 0.001, 0.01, 0.1, 0.5, 1, 5, 10, 50, 100 }
                });

        // Provider metrics
        private static readonly Counter ProviderErrors = Prometheus.Metrics
            .CreateCounter("conduit_provider_errors_total", "Total provider errors",
                new CounterConfiguration
                {
                    LabelNames = new[] { "provider", "error_type" }
                });

        private static readonly Summary ProviderLatency = Prometheus.Metrics
            .CreateSummary("conduit_provider_latency_seconds", "Provider API latency",
                new SummaryConfiguration
                {
                    LabelNames = new[] { "provider", "operation" },
                    Objectives = new[]
                    {
                        new QuantileEpsilonPair(0.5, 0.05),
                        new QuantileEpsilonPair(0.9, 0.01),
                        new QuantileEpsilonPair(0.95, 0.005),
                        new QuantileEpsilonPair(0.99, 0.001)
                    },
                    MaxAge = TimeSpan.FromMinutes(5),
                    AgeBuckets = 5
                });

        // Active entities metrics
        private static readonly Gauge ActiveVirtualKeys = Prometheus.Metrics
            .CreateGauge("conduit_virtualkeys_active_count", "Number of active virtual keys");

        private static readonly Gauge ActiveModels = Prometheus.Metrics
            .CreateGauge("conduit_models_active_count", "Number of active model mappings",
                new GaugeConfiguration
                {
                    LabelNames = new[] { "provider" }
                });

        // SLA metrics
        private static readonly Counter SLAViolations = Prometheus.Metrics
            .CreateCounter("conduit_sla_violations_total", "Total SLA violations",
                new CounterConfiguration
                {
                    LabelNames = new[] { "sla_type", "model" } // sla_type: latency, availability, error_rate
                });

        public BusinessMetricsService(
            IServiceScopeFactory serviceScopeFactory,
            ILogger<BusinessMetricsService> logger)
            : base(logger, TimeSpan.FromMinutes(1))
        {
            _serviceScopeFactory = serviceScopeFactory;
            _logger = logger;
        }

        protected override Task CollectOnceAsync(CancellationToken cancellationToken) =>
            CollectMetricsAsync(cancellationToken);

        protected override void OnCollectionFailed(Exception exception)
        {
            MetricsCollectionInstrumentation.RecordFailure("business");
            _logger.LogError(exception, "Error collecting business metrics");
        }

        internal async Task CollectMetricsAsync(CancellationToken cancellationToken = default)
        {
            using var collectionTimer = MetricsCollectionInstrumentation.Measure("business");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            using var scope = _serviceScopeFactory.CreateScope();

            var tasks = new[]
            {
                CollectModelUsageMetrics(scope, cancellationToken),
                CollectCostMetrics(scope, cancellationToken),
                CollectActiveEntityMetrics(scope, cancellationToken)
            };

            await Task.WhenAll(tasks);

            stopwatch.Stop();
            _logger.LogDebug("Business metrics collection cycle completed in {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
        }

        private async Task CollectModelUsageMetrics(
            IServiceScope scope,
            CancellationToken cancellationToken)
        {
            // NOTE: Model/provider counters (conduit_model_requests_total, conduit_model_tokens_total)
            // are updated in REAL-TIME via static methods called from UsageTrackingMiddleware.
            // This background method only collects supplementary gauge metrics.
            //
            // DO NOT increment counters here - it would cause double-counting since the middleware
            // already records each request as it happens.

            try
            {
                // Get model usage statistics for the last 5 minutes to calculate current rates
                var fiveMinutesAgo = DateTime.UtcNow.AddMinutes(-5);
                var store = scope.ServiceProvider.GetRequiredService<IGatewayMetricsStore>();
                var modelStats = await store.GetModelUsageAsync(fiveMinutesAgo, cancellationToken);

                _logger.LogDebug("Collected model usage metrics: {Count} model/provider combinations in last 5 minutes",
                    modelStats.Count);

                // Observe average response times (histograms are safe to update periodically)
                foreach (var stat in modelStats)
                {
                    if (stat.AverageResponseTimeMs > 0)
                    {
                        ModelResponseTime.WithLabels(stat.Model, stat.Provider)
                            .Observe(stat.AverageResponseTimeMs / 1000.0); // Convert ms to seconds
                    }
                }
            }
            catch (Exception ex)
            {
                MetricsCollectionInstrumentation.RecordFailure("business/model_usage");
                _logger.LogError(ex, "Error collecting model usage metrics");
            }
        }

        private async Task CollectCostMetrics(
            IServiceScope scope,
            CancellationToken cancellationToken)
        {
            // NOTE: Cost counters (conduit_cost_total_dollars) are updated in REAL-TIME via
            // static methods called from UsageTrackingMiddleware.
            // This background method only updates the CostRate gauge for rate calculations.

            try
            {
                // Calculate cost rate per provider using the ProviderType field
                var fiveMinutesAgo = DateTime.UtcNow.AddMinutes(-5);
                var store = scope.ServiceProvider.GetRequiredService<IGatewayMetricsStore>();
                var costByProvider = await store.GetProviderCostsAsync(fiveMinutesAgo, cancellationToken);

                var currentLabels = costByProvider
                    .Select(providerCost => providerCost.Provider)
                    .ToHashSet(StringComparer.Ordinal);
                RemoveStaleLabels(CostRate, _costRateLabels, currentLabels);

                foreach (var providerCost in costByProvider)
                {
                    var provider = providerCost.Provider;
                    var costPerMinute = (double)(providerCost.TotalCost / 5); // 5-minute window

                    // Update the rate gauge (this is safe to update periodically)
                    CostRate.WithLabels(provider).Set(costPerMinute);
                }
                ReplaceLabels(_costRateLabels, currentLabels);

                _logger.LogDebug("Collected cost metrics: {Count} providers with costs in last 5 minutes",
                    costByProvider.Count);
            }
            catch (Exception ex)
            {
                MetricsCollectionInstrumentation.RecordFailure("business/cost");
                _logger.LogError(ex, "Error collecting cost metrics");
            }
        }

        internal async Task CollectActiveEntityMetrics(
            IServiceScope scope,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var store = scope.ServiceProvider.GetRequiredService<IGatewayMetricsStore>();
                var activeEntities = await store.GetActiveEntitiesAsync(
                    DateTime.UtcNow,
                    cancellationToken);
                ActiveVirtualKeys.Set(activeEntities.ActiveVirtualKeyCount);

                var mappingsByProvider = activeEntities.MappingsByProvider;

                var currentLabels = mappingsByProvider
                    .Select(group => group.ProviderId.ToString())
                    .ToHashSet(StringComparer.Ordinal);
                RemoveStaleLabels(ActiveModels, _activeModelLabels, currentLabels);

                foreach (var group in mappingsByProvider)
                {
                    ActiveModels.WithLabels(group.ProviderId.ToString()).Set(group.Count);
                }
                ReplaceLabels(_activeModelLabels, currentLabels);
            }
            catch (Exception ex)
            {
                MetricsCollectionInstrumentation.RecordFailure("business/active_entities");
                _logger.LogError(ex, "Error collecting active entity metrics");
            }
        }

        private static void RemoveStaleLabels(
            Gauge gauge,
            HashSet<string> previousLabels,
            HashSet<string> currentLabels)
        {
            foreach (var staleLabel in previousLabels.Except(currentLabels))
            {
                gauge.RemoveLabelled(staleLabel);
            }
        }

        private static void ReplaceLabels(HashSet<string> target, HashSet<string> source)
        {
            target.Clear();
            target.UnionWith(source);
        }

        // Static methods to be called by application code
        public static void RecordVirtualKeyRequest(string virtualKeyId, string model, string status)
        {
            VirtualKeyRequests.WithLabels(virtualKeyId, model, status).Inc();
        }

        public static void RecordModelRequest(string model, string provider, string status)
        {
            ModelRequests.WithLabels(model, provider, status).Inc();
        }

        public static void RecordProviderError(string provider, string errorType)
        {
            ProviderErrors.WithLabels(provider, errorType).Inc();
        }

        public static void RecordProviderLatency(string provider, string operation, double latencySeconds)
        {
            ProviderLatency.WithLabels(provider, operation).Observe(latencySeconds);
        }

        public static void RecordCost(string provider, string model, string operationType, double costDollars)
        {
            CostTotal.WithLabels(provider, model, operationType).Inc(costDollars);
            CostPerRequest.WithLabels(model, provider).Observe(costDollars);
        }

        public static void RecordTokens(string model, string provider, int promptTokens, int completionTokens,
            int? cachedInputTokens = null, int? cachedWriteTokens = null)
        {
            if (promptTokens > 0)
            {
                ModelTokensProcessed.WithLabels(model, provider, "prompt").Inc(promptTokens);
            }
            if (completionTokens > 0)
            {
                ModelTokensProcessed.WithLabels(model, provider, "completion").Inc(completionTokens);
            }
            if (cachedInputTokens.HasValue && cachedInputTokens.Value > 0)
            {
                ModelTokensProcessed.WithLabels(model, provider, "cached_input").Inc(cachedInputTokens.Value);
            }
            if (cachedWriteTokens.HasValue && cachedWriteTokens.Value > 0)
            {
                ModelTokensProcessed.WithLabels(model, provider, "cached_write").Inc(cachedWriteTokens.Value);
            }
        }

        public static void RecordResponseTime(string model, string provider, double responseTimeSeconds)
        {
            if (responseTimeSeconds > 0)
            {
                ModelResponseTime.WithLabels(model, provider).Observe(responseTimeSeconds);
            }
        }

        public static void RecordSLAViolation(string slaType, string model)
        {
            SLAViolations.WithLabels(slaType, model).Inc();
        }
    }
}
