using Prometheus;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Persistence.Interfaces;

namespace ConduitLLM.Gateway.Services
{
    /// <summary>
    /// Service for tracking task processing metrics including queue depths,
    /// processing times, and success/failure rates.
    /// </summary>
    public class TaskProcessingMetricsService : PeriodicCollectorBackgroundService
    {
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ILogger<TaskProcessingMetricsService> _logger;
        private readonly HashSet<(string TaskType, string Status)> _taskQueueLabels = [];
        private readonly HashSet<string> _taskWaitLabels = [];
        private readonly HashSet<string> _virtualKeySpendLabels = [];

        // Task queue metrics
        private static readonly Gauge TaskQueueDepth = Prometheus.Metrics
            .CreateGauge("conduit_tasks_queue_depth", "Number of tasks in queue",
                new GaugeConfiguration
                {
                    LabelNames = new[] { "task_type", "status" }
                });

        private static readonly Histogram TaskProcessingDuration = Prometheus.Metrics
            .CreateHistogram("conduit_task_processing_duration_seconds", "Task processing duration in seconds",
                new HistogramConfiguration
                {
                    LabelNames = new[] { "task_type", "provider", "status" },
                    Buckets = Histogram.ExponentialBuckets(0.1, 2, 16) // 0.1s to ~1 hour
                });

        private static readonly Counter TasksCompleted = Prometheus.Metrics
            .CreateCounter("conduit_tasks_completed_total", "Total number of completed tasks",
                new CounterConfiguration
                {
                    LabelNames = new[] { "task_type", "provider", "status" }
                });

        private static readonly Counter TaskRetries = Prometheus.Metrics
            .CreateCounter("conduit_task_retries_total", "Total number of task retries",
                new CounterConfiguration
                {
                    LabelNames = new[] { "task_type", "provider" }
                });

        private static readonly Gauge TasksInProgress = Prometheus.Metrics
            .CreateGauge("conduit_tasks_in_progress", "Number of tasks currently being processed",
                new GaugeConfiguration
                {
                    LabelNames = new[] { "task_type", "provider" }
                });

        private static readonly Gauge TaskWaitTime = Prometheus.Metrics
            .CreateGauge("conduit_task_wait_time_seconds", "Age in seconds of the oldest pending task",
                new GaugeConfiguration
                {
                    LabelNames = new[] { "task_type" }
                });

        private static readonly Counter WebhookDeliveries = Prometheus.Metrics
            .CreateCounter("conduit_webhook_deliveries_total", "Total number of webhook delivery attempts",
                new CounterConfiguration
                {
                    LabelNames = new[] { "status", "retry_count" }
                });

        private static readonly Histogram WebhookDeliveryDuration = Prometheus.Metrics
            .CreateHistogram("conduit_webhook_delivery_duration_seconds", "Webhook delivery duration",
                new HistogramConfiguration
                {
                    LabelNames = new[] { "status" },
                    Buckets = Histogram.ExponentialBuckets(0.01, 2, 14) // 10ms to ~82s
                });

        private static readonly Gauge VirtualKeySpendRate = Prometheus.Metrics
            .CreateGauge("conduit_virtualkey_spend_rate", "Virtual key spend rate per minute",
                new GaugeConfiguration
                {
                    LabelNames = new[] { "virtual_key_id" }
                });

        private static readonly Counter TaskCancellations = Prometheus.Metrics
            .CreateCounter("conduit_task_cancellations_total", "Total number of task cancellations",
                new CounterConfiguration
                {
                    LabelNames = new[] { "task_type", "reason" }
                });

        private static readonly Histogram BatchProcessingSize = Prometheus.Metrics
            .CreateHistogram("conduit_batch_processing_size", "Number of items processed in batch operations",
                new HistogramConfiguration
                {
                    LabelNames = new[] { "operation_type" },
                    Buckets = new[] { 1.0, 5.0, 10.0, 25.0, 50.0, 100.0, 250.0, 500.0, 1000.0 }
                });

        public TaskProcessingMetricsService(
            IServiceScopeFactory serviceScopeFactory,
            ILogger<TaskProcessingMetricsService> logger)
            : base(logger, TimeSpan.FromSeconds(30))
        {
            _serviceScopeFactory = serviceScopeFactory;
            _logger = logger;
        }

        protected override Task CollectOnceAsync(CancellationToken cancellationToken) =>
            CollectMetricsAsync(cancellationToken);

        protected override void OnCollectionFailed(Exception exception)
        {
            MetricsCollectionInstrumentation.RecordFailure("task_processing");
            _logger.LogError(exception, "Error collecting task processing metrics");
        }

        internal async Task CollectMetricsAsync(CancellationToken cancellationToken = default)
        {
            using var collectionTimer = MetricsCollectionInstrumentation.Measure("task_processing");
            using var scope = _serviceScopeFactory.CreateScope();

            // Collect async task metrics
            await CollectAsyncTaskMetrics(scope, cancellationToken);

            // Collect image generation task metrics
            await CollectImageGenerationMetrics(scope, cancellationToken);

            // Collect video generation task metrics
            await CollectVideoGenerationMetrics(scope, cancellationToken);

            // Collect virtual key spend metrics
            await CollectVirtualKeySpendMetrics(scope, cancellationToken);
        }

        internal async Task CollectAsyncTaskMetrics(
            IServiceScope scope,
            CancellationToken cancellationToken = default)
        {
            try
            {
                // Queue depth only includes non-terminal tasks. Grouping and counting stay in the
                // database, so collection cost is bounded by the number of task-type/state groups.
                var store = scope.ServiceProvider.GetRequiredService<IGatewayMetricsStore>();
                var queueMetrics = await store.GetTaskQueueMetricsAsync(cancellationToken);
                var queueDepths = queueMetrics.QueueDepths;

                var currentQueueLabels = queueDepths
                    .Select(depth => (TaskType: depth.TaskType, Status: GetStatusFromState(depth.State)))
                    .ToHashSet();
                foreach (var staleLabel in _taskQueueLabels.Except(currentQueueLabels))
                {
                    TaskQueueDepth.RemoveLabelled(staleLabel.TaskType, staleLabel.Status);
                }

                foreach (var depth in queueDepths)
                {
                    TaskQueueDepth.WithLabels(depth.TaskType, GetStatusFromState(depth.State)).Set(depth.Count);
                }
                ReplaceLabels(_taskQueueLabels, currentQueueLabels);

                // Oldest pending age is a stable queue-wait signal that requires one grouped MIN
                // query rather than loading or sampling an unbounded set of task rows.
                var oldestPendingTasks = queueMetrics.OldestPendingTasks;

                var currentWaitLabels = oldestPendingTasks
                    .Select(task => task.TaskType)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var staleLabel in _taskWaitLabels.Except(currentWaitLabels))
                {
                    TaskWaitTime.RemoveLabelled(staleLabel);
                }

                var now = DateTime.UtcNow;
                foreach (var task in oldestPendingTasks)
                {
                    var waitSeconds = Math.Max(0, (now - task.OldestCreatedAt).TotalSeconds);
                    TaskWaitTime.WithLabels(task.TaskType).Set(waitSeconds);
                }
                ReplaceLabels(_taskWaitLabels, currentWaitLabels);
            }
            catch (Exception ex)
            {
                MetricsCollectionInstrumentation.RecordFailure("task_processing/async_tasks");
                _logger.LogError(ex, "Error collecting async task metrics");
            }
        }

        private async Task CollectImageGenerationMetrics(
            IServiceScope scope,
            CancellationToken cancellationToken)
        {
            try
            {
                // Get image generation task statistics from AsyncTasks
                var now = DateTime.UtcNow;
                var oneHourAgo = now.AddHours(-1);
                var store = scope.ServiceProvider.GetRequiredService<IGatewayMetricsStore>();
                var imageStats = await store.GetGenerationTaskMetricsAsync(
                    "image_generation",
                    oneHourAgo,
                    cancellationToken);

                foreach (var stat in imageStats)
                {
                    var status = GetStatusFromState(stat.State);
                    if (status == "processing")
                    {
                        TasksInProgress.WithLabels("image", "unknown").Set(stat.Count);
                    }

                    if (stat.AverageDurationSeconds.HasValue)
                    {
                        TaskProcessingDuration.WithLabels("image", "unknown", status)
                            .Observe(stat.AverageDurationSeconds.Value);
                    }
                }

                if (!imageStats.Any(stat => GetStatusFromState(stat.State) == "processing"))
                {
                    TasksInProgress.RemoveLabelled("image", "unknown");
                }
            }
            catch (Exception ex)
            {
                MetricsCollectionInstrumentation.RecordFailure("task_processing/images");
                _logger.LogError(ex, "Error collecting image generation metrics");
            }
        }

        private async Task CollectVideoGenerationMetrics(
            IServiceScope scope,
            CancellationToken cancellationToken)
        {
            try
            {
                // Get video generation task statistics from AsyncTasks
                var now = DateTime.UtcNow;
                var oneHourAgo = now.AddHours(-1);
                var store = scope.ServiceProvider.GetRequiredService<IGatewayMetricsStore>();
                var videoStats = await store.GetGenerationTaskMetricsAsync(
                    "video_generation",
                    oneHourAgo,
                    cancellationToken);

                foreach (var stat in videoStats)
                {
                    var status = GetStatusFromState(stat.State);
                    if (status == "processing")
                    {
                        TasksInProgress.WithLabels("video", "unknown").Set(stat.Count);
                    }

                    if (stat.AverageDurationSeconds.HasValue)
                    {
                        TaskProcessingDuration.WithLabels("video", "unknown", status)
                            .Observe(stat.AverageDurationSeconds.Value);
                    }
                }

                if (!videoStats.Any(stat => GetStatusFromState(stat.State) == "processing"))
                {
                    TasksInProgress.RemoveLabelled("video", "unknown");
                }
            }
            catch (Exception ex)
            {
                MetricsCollectionInstrumentation.RecordFailure("task_processing/videos");
                _logger.LogError(ex, "Error collecting video generation metrics");
            }
        }

        private static string GetStatusFromState(int state)
        {
            // AsyncTaskState enum: 0=Pending, 1=Processing, 2=Completed, 3=Failed, 4=Cancelled
            return state switch
            {
                0 => "pending",
                1 => "processing",
                2 => "completed",
                3 => "failed",
                4 => "cancelled",
                5 => "timed_out",
                6 => "indeterminate",
                _ => "unknown"
            };
        }

        private async Task CollectVirtualKeySpendMetrics(
            IServiceScope scope,
            CancellationToken cancellationToken)
        {
            try
            {
                // Get spend rate for top virtual keys in the last minute. Aggregate and limit in
                // the database so a busy minute never materializes every spend-history row.
                var now = DateTime.UtcNow;
                var oneMinuteAgo = now.AddMinutes(-1);
                var store = scope.ServiceProvider.GetRequiredService<IGatewayMetricsStore>();
                var spendByKey = await store.GetTopVirtualKeySpendAsync(
                    oneMinuteAgo,
                    now,
                    100,
                    cancellationToken);

                var currentLabels = spendByKey
                    .Select(spend => spend.VirtualKeyId.ToString())
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var staleLabel in _virtualKeySpendLabels.Except(currentLabels))
                {
                    VirtualKeySpendRate.RemoveLabelled(staleLabel);
                }

                foreach (var spend in spendByKey)
                {
                    VirtualKeySpendRate.WithLabels(spend.VirtualKeyId.ToString()).Set((double)spend.TotalSpend);
                }
                ReplaceLabels(_virtualKeySpendLabels, currentLabels);
            }
            catch (Exception ex)
            {
                MetricsCollectionInstrumentation.RecordFailure("task_processing/virtual_key_spend");
                _logger.LogError(ex, "Error collecting virtual key spend metrics");
            }
        }

        private static void ReplaceLabels<T>(HashSet<T> target, HashSet<T> source)
            where T : notnull
        {
            target.Clear();
            target.UnionWith(source);
        }

        // Static methods to be called by task processing code
        public static void RecordTaskStarted(string taskType, string provider)
        {
            TasksInProgress.WithLabels(taskType, provider).Inc();
        }

        public static void RecordTaskCompleted(string taskType, string provider, string status, double durationSeconds)
        {
            TasksInProgress.WithLabels(taskType, provider).Dec();
            TasksCompleted.WithLabels(taskType, provider, status).Inc();
            TaskProcessingDuration.WithLabels(taskType, provider, status).Observe(durationSeconds);
        }

        public static void RecordTaskRetry(string taskType, string provider)
        {
            TaskRetries.WithLabels(taskType, provider).Inc();
        }

        public static void RecordTaskCancellation(string taskType, string reason)
        {
            TaskCancellations.WithLabels(taskType, reason).Inc();
        }

        public static void RecordWebhookDelivery(string status, int retryCount, double durationSeconds)
        {
            WebhookDeliveries.WithLabels(status, retryCount.ToString()).Inc();
            WebhookDeliveryDuration.WithLabels(status).Observe(durationSeconds);
        }

        public static void RecordBatchProcessing(string operationType, int batchSize)
        {
            BatchProcessingSize.WithLabels(operationType).Observe(batchSize);
        }
    }
}
