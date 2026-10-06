using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Background service that batches cache invalidation requests for optimal performance
    /// </summary>
    public class BatchCacheInvalidationService : BackgroundService, IBatchCacheInvalidationService
    {
        private readonly ConcurrentDictionary<CacheType, ConcurrentQueue<InvalidationRequest>> _queues;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<BatchCacheInvalidationService> _logger;
        private readonly ConcurrentDictionary<CacheType, CacheTypeStats> _stats;
        private Timer? _batchTimer;
        private BatchInvalidationOptions _options;
        private readonly SemaphoreSlim _processingSemaphore;
        private long _totalQueued;
        private long _totalProcessed;
        private long _totalCoalesced;
        private DateTime _lastProcessedTime = DateTime.UtcNow;
        private readonly Queue<DateTime> _errorTimestamps = new();
        private readonly object _errorLock = new();

        public BatchCacheInvalidationService(
            IServiceProvider serviceProvider,
            IOptions<BatchInvalidationOptions> options,
            ILogger<BatchCacheInvalidationService> logger)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? new BatchInvalidationOptions();
            
            _queues = new ConcurrentDictionary<CacheType, ConcurrentQueue<InvalidationRequest>>();
            _stats = new ConcurrentDictionary<CacheType, CacheTypeStats>();
            _processingSemaphore = new SemaphoreSlim(1, 1);
            
            // Initialize queues and stats for each cache type
            foreach (CacheType cacheType in Enum.GetValues<CacheType>())
            {
                _queues[cacheType] = new ConcurrentQueue<InvalidationRequest>();
                _stats[cacheType] = new CacheTypeStats();
            }
        }

        public async Task QueueInvalidationAsync<T>(string key, T eventData, CacheType cacheType) where T : DomainEvent
        {
            if (cacheType == CacheType.ModelCost) await InvalidateBillingAsync();
            if (!_options.Enabled)
            {
                await ProcessDirectInvalidation(key, eventData, cacheType);
                return;
            }

            var request = new InvalidationRequest
            {
                EntityType = cacheType.ToString(),
                EntityId = key,
                Reason = $"Event: {nameof(T)}",
                Priority = DeterminePriority(eventData),
                QueuedAt = DateTime.UtcNow,
                SourceEvent = eventData
            };

            _queues[cacheType].Enqueue(request);
            Interlocked.Increment(ref _totalQueued);
            _stats[cacheType].Queued++;

            _logger.LogDebug("Queued invalidation for {CacheType} key {Key}", cacheType, key);

            // Check if we should process immediately
            if (request.Priority == InvalidationPriority.Critical || 
                _queues[cacheType].Count >= _options.MaxBatchSize)
            {
                await TriggerImmediateProcessing();
            }
        }

        public async Task QueueBulkInvalidationAsync<T>(string[] keys, T eventData, CacheType cacheType) where T : DomainEvent
        {
            if (cacheType == CacheType.ModelCost && keys.Length > 0) await InvalidateBillingAsync();
            if (!_options.Enabled)
            {
                foreach (var key in keys)
                {
                    await ProcessDirectInvalidation(key, eventData, cacheType);
                }
                return;
            }

            var priority = DeterminePriority(eventData);
            var queuedAt = DateTime.UtcNow;

            foreach (var key in keys)
            {
                var request = new InvalidationRequest
                {
                    EntityType = cacheType.ToString(),
                    EntityId = key,
                    Reason = $"Bulk Event: {nameof(T)}",
                    Priority = priority,
                    QueuedAt = queuedAt,
                    SourceEvent = eventData
                };

                _queues[cacheType].Enqueue(request);
                Interlocked.Increment(ref _totalQueued);
                _stats[cacheType].Queued++;
            }

            _logger.LogDebug("Queued {Count} bulk invalidations for {CacheType}", keys.Length, cacheType);

            // Check if we should process immediately
            if (priority == InvalidationPriority.Critical || 
                _queues[cacheType].Count >= _options.MaxBatchSize)
            {
                await TriggerImmediateProcessing();
            }
        }

        // Billing expiration is awaited at the event boundary. Only the separate auxiliary Redis
        // cache remains batched; an in-memory queue cannot acknowledge required billing expiration.
        private async Task InvalidateBillingAsync()
        {
            using var scope = _serviceProvider.CreateScope();
            var provider = scope.ServiceProvider;
            if (provider.GetService<ConduitLLM.Configuration.Interfaces.IModelCostService>() is { } billing)
                await billing.ClearCacheAsync();
            if (provider.GetService<IModelMappingCacheInvalidator>() is { } mappings) await mappings.InvalidateAsync();
            if (provider.GetService<ICachedPricingRulesService>() is { } rules) await rules.InvalidateAllAsync();
            if (provider.GetService<IDiscoveryCacheService>() is { } discovery) await discovery.InvalidateAllDiscoveryAsync();
        }

        public void Configure(BatchInvalidationOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger.LogInformation("Batch invalidation configured: Enabled={Enabled}, BatchWindow={BatchWindow}ms, MaxBatchSize={MaxBatchSize}", 
                options.Enabled, options.BatchWindow.TotalMilliseconds, options.MaxBatchSize);
        }

        public Task<BatchInvalidationStats> GetStatsAsync()
        {
            var stats = new BatchInvalidationStats
            {
                TotalQueued = _totalQueued,
                TotalProcessed = _totalProcessed,
                TotalCoalesced = _totalCoalesced,
                ByType = _stats.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                AverageBatchProcessTime = CalculateAverageProcessTime()
            };

            return Task.FromResult(stats);
        }

        public async Task FlushAsync()
        {
            _logger.LogInformation("Forcing immediate flush of all queued invalidations");
            await ProcessAllBatches();
        }

        public Task<QueueStats> GetQueueStatsAsync()
        {
            var stats = new QueueStats
            {
                TotalQueueDepth = _queues.Sum(q => q.Value.Count()),
                QueueDepthByType = _queues.ToDictionary(q => q.Key, q => q.Value.Count()),
                LastProcessed = _lastProcessedTime
            };

            return Task.FromResult(stats);
        }

        public Task<double> GetErrorRateAsync(TimeSpan window)
        {
            lock (_errorLock)
            {
                var cutoff = DateTime.UtcNow - window;
                while (_errorTimestamps.Any() && _errorTimestamps.Peek() < cutoff)
                {
                    _errorTimestamps.Dequeue();
                }

                var errorCount = _errorTimestamps.Count;
                var totalProcessed = _totalProcessed;
                
                return Task.FromResult(totalProcessed > 0 ? errorCount / (double)totalProcessed : 0);
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("Batch cache invalidation is disabled");
                return;
            }

            _logger.LogInformation("Starting batch cache invalidation service");

            _batchTimer = new Timer(
                callback: async _ => await ProcessAllBatches(),
                state: null,
                dueTime: _options.BatchWindow,
                period: _options.BatchWindow);

            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // Expected when cancellation is requested
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping batch cache invalidation service");
            
            _batchTimer?.Dispose();
            
            // Process any remaining items
            await ProcessAllBatches();
            
            await base.StopAsync(cancellationToken);
        }

        private async Task ProcessAllBatches()
        {
            if (!await _processingSemaphore.WaitAsync(0))
            {
                // Already processing, skip this cycle
                return;
            }

            try
            {
                var tasks = new List<Task>();

                foreach (var cacheType in _queues.Keys)
                {
                    if (_queues[cacheType].Count > 0)
                    {
                        tasks.Add(ProcessBatch(cacheType));
                    }
                }

                if (tasks.Any())
                {
                    await Task.WhenAll(tasks);
                    _lastProcessedTime = DateTime.UtcNow;
                }
            }
            finally
            {
                _processingSemaphore.Release();
            }
        }

        private async Task ProcessBatch(CacheType cacheType)
        {
            var queue = _queues[cacheType];
            var batchStartTime = DateTime.UtcNow;
            var itemsToProcess = new List<InvalidationRequest>();
            
            // Dequeue up to MaxBatchSize items
            while (itemsToProcess.Count() < _options.MaxBatchSize && queue.TryDequeue(out var item))
            {
                itemsToProcess.Add(item);
            }

            if (!itemsToProcess.Any())
            {
                return;
            }

            try
            {
                // Apply coalescing if enabled
                if (_options.EnableCoalescing)
                {
                    var originalCount = itemsToProcess.Count();
                    itemsToProcess = CoalesceRequests(itemsToProcess);
                    var coalescedCount = originalCount - itemsToProcess.Count();
                    
                    if (coalescedCount > 0)
                    {
                        Interlocked.Add(ref _totalCoalesced, coalescedCount);
                        _stats[cacheType].Coalesced += coalescedCount;
                        _logger.LogDebug("Coalesced {Count} duplicate requests for {CacheType}", 
                            coalescedCount, cacheType);
                    }
                }

                // Process the batch
                await ProcessInvalidationBatch(cacheType, itemsToProcess);
                
                // Update statistics
                var processingTime = DateTime.UtcNow - batchStartTime;
                Interlocked.Add(ref _totalProcessed, itemsToProcess.Count());
                _stats[cacheType].Processed += itemsToProcess.Count();
                UpdateAverageProcessTime(cacheType, processingTime);
                
                _logger.LogInformation("Processed batch of {Count} invalidations for {CacheType} in {Duration}ms", 
                    itemsToProcess.Count(), cacheType, processingTime.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process batch for {CacheType}", cacheType);
                _stats[cacheType].Errors += itemsToProcess.Count();
                RecordError();
                
                // Re-queue failed items for retry
                foreach (var item in itemsToProcess)
                {
                    queue.Enqueue(item);
                }
            }
        }

        private List<InvalidationRequest> CoalesceRequests(List<InvalidationRequest> requests)
        {
            // Group by EntityId and take the latest request for each
            return requests
                .GroupBy(r => r.EntityId)
                .Select(g => g.OrderByDescending(r => r.QueuedAt).First())
                .ToList();
        }

        private async Task ProcessInvalidationBatch(CacheType cacheType, List<InvalidationRequest> requests)
        {
            using var scope = _serviceProvider.CreateScope();
            
            switch (cacheType)
            {
                case CacheType.VirtualKey:
                    var virtualKeyCache = scope.ServiceProvider.GetService<IVirtualKeyCache>();
                    if (virtualKeyCache != null)
                    {
                        var keys = requests.Select(r => r.EntityId).ToArray();
                        await InvalidateVirtualKeyBatch(virtualKeyCache, keys);
                    }
                    break;
                    
                case CacheType.ModelCost:
                    var modelCostCache = scope.ServiceProvider.GetService<IModelCostCache>();
                    if (modelCostCache != null)
                    {
                        var costIds = requests.Select(r => r.EntityId).ToArray();
                        await InvalidateModelCostBatch(modelCostCache, costIds);
                    }
                    break;
                    
                // Add other cache types as needed
                default:
                    _logger.LogWarning("No handler for cache type {CacheType}", cacheType);
                    break;
            }
        }

        private async Task InvalidateVirtualKeyBatch(IVirtualKeyCache cache, string[] keys)
        {
            // Check if cache supports batch invalidation
            if (cache is IBatchInvalidatable batchCache)
            {
                var requests = keys.Select(key => new InvalidationRequest
                {
                    EntityType = CacheType.VirtualKey.ToString(),
                    EntityId = key,
                    Reason = "Batch invalidation"
                }).ToList();
                
                await batchCache.InvalidateBatchAsync(requests);
            }
            else
            {
                // Fallback to individual invalidation
                var tasks = keys.Select(key => cache.InvalidateVirtualKeyAsync(key));
                await Task.WhenAll(tasks);
            }
        }

        private async Task InvalidateModelCostBatch(IModelCostCache cache, string[] costIds)
        {
            // For now, process individually until we implement batch methods
            // This will be optimized in Sub-issue #2
            foreach (var costId in costIds)
            {
                if (int.TryParse(costId, out var id))
                {
                    await cache.InvalidateModelCostAsync(id);
                }
            }
        }

        private async Task ProcessDirectInvalidation<T>(string key, T eventData, CacheType cacheType) where T : DomainEvent
        {
            // Direct invalidation when batching is disabled
            using var scope = _serviceProvider.CreateScope();
            
            switch (cacheType)
            {
                case CacheType.VirtualKey:
                    var virtualKeyCache = scope.ServiceProvider.GetService<IVirtualKeyCache>();
                    if (virtualKeyCache != null)
                    {
                        await virtualKeyCache.InvalidateVirtualKeyAsync(key);
                    }
                    break;
                    
                case CacheType.ModelCost:
                    var modelCostCache = scope.ServiceProvider.GetService<IModelCostCache>();
                    if (modelCostCache != null && int.TryParse(key, out var costId))
                    {
                        await modelCostCache.InvalidateModelCostAsync(costId);
                    }
                    break;
            }
        }

        private InvalidationPriority DeterminePriority<T>(T eventData) where T : DomainEvent
        {
            return eventData switch
            {
                // Critical - Security/billing related
                VirtualKeyDeleted => InvalidationPriority.Critical,
                SpendThresholdExceeded => InvalidationPriority.Critical,
                
                // High - Affects active operations
                VirtualKeyUpdated vku when vku.ChangedProperties.Contains("IsEnabled") => InvalidationPriority.High,
                VirtualKeyUpdated vku when vku.ChangedProperties.Contains("MaxBudget") => InvalidationPriority.High,
                SpendUpdated => InvalidationPriority.High, // All spend updates are high priority
                
                // Normal - Regular updates
                ModelCostChanged => InvalidationPriority.Normal,
                VirtualKeyCreated => InvalidationPriority.Normal,
                
                // Default
                _ => InvalidationPriority.Normal
            };
        }

        private async Task TriggerImmediateProcessing()
        {
            _logger.LogDebug("Triggering immediate batch processing");
            await ProcessAllBatches();
        }

        private void UpdateAverageProcessTime(CacheType cacheType, TimeSpan duration)
        {
            var stats = _stats[cacheType];
            var currentAvg = stats.AverageProcessTime.TotalMilliseconds;
            var processed = stats.Processed;
            
            // Calculate running average
            var newAvg = ((currentAvg * (processed - 1)) + duration.TotalMilliseconds) / processed;
            stats.AverageProcessTime = TimeSpan.FromMilliseconds(newAvg);
        }

        private TimeSpan CalculateAverageProcessTime()
        {
            var totalTime = _stats.Values.Sum(s => s.AverageProcessTime.TotalMilliseconds * s.Processed);
            var totalProcessed = _stats.Values.Sum(s => s.Processed);
            
            return totalProcessed > 0 
                ? TimeSpan.FromMilliseconds(totalTime / totalProcessed) 
                : TimeSpan.Zero;
        }

        private void RecordError()
        {
            lock (_errorLock)
            {
                _errorTimestamps.Enqueue(DateTime.UtcNow);
                
                // Keep only last hour of errors
                var cutoff = DateTime.UtcNow.AddHours(-1);
                while (_errorTimestamps.Any() && _errorTimestamps.Peek() < cutoff)
                {
                    _errorTimestamps.Dequeue();
                }
            }
        }
    }
}
