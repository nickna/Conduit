using System.Text.Json;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Core task operations for HybridAsyncTaskService.
    /// </summary>
    public partial class HybridAsyncTaskService
    {
        /// <inheritdoc/>
        public async Task<string> CreateTaskAsync(string taskType, object metadata, CancellationToken cancellationToken = default)
        {
            var taskId = GenerateTaskId();
            var now = DateTime.UtcNow;
            var virtualKeyId = GetVirtualKeyIdFromMetadata(metadata);

            // Create database entity
            var asyncTask = new AsyncTask
            {
                Id = taskId,
                Type = taskType,
                State = (int)TaskState.Pending,
                CreatedAt = now,
                UpdatedAt = now,
                VirtualKeyId = virtualKeyId,
                Metadata = JsonSerializer.Serialize(
                    metadata,
                    metadata.GetType(),
                    AsyncTaskJsonContext.Default),
                Progress = 0
            };

            // Save to database first - this is the critical operation
            await _repository.CreateAsync(asyncTask, cancellationToken);
            _logger.LogInformation("Created async task {TaskId} in database", taskId);

            // Create task status for cache
            var taskStatus = new AsyncTaskStatus
            {
                TaskId = taskId,
                TaskType = taskType,
                State = TaskState.Pending,
                CreatedAt = now,
                UpdatedAt = now,
                Metadata = metadata as TaskMetadata,
                Progress = 0
            };

            // Best-effort cache update with resilience
            try
            {
                await CacheTaskStatusAsync(taskId, taskStatus, cancellationToken);
                _logger.LogDebug("Cached task {TaskId} status", taskId);
            }
            catch (Exception ex)
            {
                // Log but don't fail - cache will self-heal on next read
                _logger.LogWarning(ex, "Failed to cache task {TaskId}, will self-heal on next read", taskId);
            }
            
            // Best-effort event publishing
            if (_eventBus != null)
            {
                try
                {
                    await _eventBus.PublishAsync(new AsyncTaskCreated
                    {
                        TaskId = taskId,
                        TaskType = taskType,
                        VirtualKeyId = virtualKeyId
                    }, cancellationToken);
                    _logger.LogDebug("Published AsyncTaskCreated event for task {TaskId}", taskId);
                }
                catch (Exception ex)
                {
                    // Log but don't fail - events are not critical for task creation
                    _logger.LogWarning(ex, "Failed to publish AsyncTaskCreated event for task {TaskId}", taskId);
                }
            }
            
            _logger.LogInformation("Created hybrid async task {TaskId} of type {TaskType}", taskId, taskType);
            
            return taskId;
        }

        /// <inheritdoc/>
        public async Task<string> CreateTaskAsync(string taskType, int virtualKeyId, object metadata, CancellationToken cancellationToken = default)
        {
            var taskId = GenerateTaskId();
            var now = DateTime.UtcNow;

            // Create database entity
            var asyncTask = new AsyncTask
            {
                Id = taskId,
                Type = taskType,
                State = (int)TaskState.Pending,
                CreatedAt = now,
                UpdatedAt = now,
                VirtualKeyId = virtualKeyId,
                Metadata = JsonSerializer.Serialize(
                    metadata,
                    metadata.GetType(),
                    AsyncTaskJsonContext.Default),
                Progress = 0
            };

            // Save to database first - this is the critical operation
            await _repository.CreateAsync(asyncTask, cancellationToken);
            _logger.LogInformation("Created async task {TaskId} in database", taskId);

            // Create task status for cache
            var taskStatus = new AsyncTaskStatus
            {
                TaskId = taskId,
                TaskType = taskType,
                State = TaskState.Pending,
                CreatedAt = now,
                UpdatedAt = now,
                Metadata = metadata as TaskMetadata,
                Progress = 0
            };

            // Best-effort cache update with resilience
            try
            {
                await CacheTaskStatusAsync(taskId, taskStatus, cancellationToken);
                _logger.LogDebug("Cached task {TaskId} status", taskId);
            }
            catch (Exception ex)
            {
                // Log but don't fail - cache will self-heal on next read
                _logger.LogWarning(ex, "Failed to cache task {TaskId}, will self-heal on next read", taskId);
            }
            
            // Best-effort event publishing
            if (_eventBus != null)
            {
                try
                {
                    await _eventBus.PublishAsync(new AsyncTaskCreated
                    {
                        TaskId = taskId,
                        TaskType = taskType,
                        VirtualKeyId = virtualKeyId
                    }, cancellationToken);
                    _logger.LogDebug("Published AsyncTaskCreated event for task {TaskId}", taskId);
                }
                catch (Exception ex)
                {
                    // Log but don't fail - events are not critical for task creation
                    _logger.LogWarning(ex, "Failed to publish AsyncTaskCreated event for task {TaskId}", taskId);
                }
            }
            
            _logger.LogInformation("Created hybrid async task {TaskId} of type {TaskType} for VirtualKeyId {VirtualKeyId}", 
                taskId, taskType, virtualKeyId);
            
            return taskId;
        }

        /// <inheritdoc/>
        public async Task<AsyncTaskStatus?> GetTaskStatusAsync(string taskId, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("GetTaskStatusAsync called for task {TaskId}", taskId);
            
            // Try cache first
            var key = GetTaskKey(taskId);
            _logger.LogDebug("Looking for task in cache with key: {CacheKey}", key);
            
            string? json = null;
            bool cacheHit = false;
            
            try
            {
                json = await _cache.GetStringAsync(key, cancellationToken);
                cacheHit = !string.IsNullOrEmpty(json);
                _logger.LogDebug("Cache lookup result - Hit: {CacheHit}, HasValue: {HasValue}", cacheHit, !string.IsNullOrEmpty(json));
            }
            catch (Exception ex)
            {
                // Cache failure - log and continue with database fallback
                _logger.LogWarning(ex, "Cache read failed for task {TaskId} with key {CacheKey}, falling back to database", taskId, key);
            }
            
            if (cacheHit && !string.IsNullOrEmpty(json))
            {
                _logger.LogInformation("Task {TaskId} found in cache with key {CacheKey}", taskId, key);
                try
                {
                    var cachedStatus = JsonSerializer.Deserialize(
                        json,
                        AsyncTaskJsonContext.Default.AsyncTaskStatus);
                    _logger.LogDebug("Successfully deserialized cached task {TaskId}, State: {State}", taskId, cachedStatus?.State);
                    return cachedStatus;
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Failed to deserialize cached task {TaskId} from key {CacheKey}, JSON: {Json}", taskId, key, json?.Substring(0, Math.Min(json.Length, 200)));
                    // Continue to database fallback
                }
            }

            // Fallback to database
            _logger.LogInformation("Cache miss for task {TaskId} with key {CacheKey}, reading from database", taskId, key);
            var dbTask = await _repository.GetByIdAsync(taskId, cancellationToken);
            if (dbTask == null)
            {
                _logger.LogWarning("Task {TaskId} not found in database either", taskId);
                return null;
            }
            
            _logger.LogInformation("Task {TaskId} found in database, State: {State}", taskId, dbTask.State);

            // Log consistency monitoring
            if (cacheHit)
            {
                _logger.LogInformation("Task {TaskId} cache-database consistency issue detected - cache had invalid data", taskId);
            }

            // Convert database entity to task status
            var taskStatus = ConvertToTaskStatus(dbTask);

            // Re-cache the task with resilience
            try
            {
                await CacheTaskStatusAsync(taskId, taskStatus, cancellationToken, IsTaskCompleted(taskStatus.State));
                _logger.LogDebug("Re-cached task {TaskId} after database read", taskId);
            }
            catch (Exception ex)
            {
                // Log but don't fail the read operation
                _logger.LogWarning(ex, "Failed to re-cache task {TaskId} after database read", taskId);
            }

            return taskStatus;
        }

        /// <inheritdoc/>
        public async Task UpdateTaskStatusAsync(
            string taskId, 
            TaskState status, 
            int? progress = null,
            object? result = null, 
            string? error = null, 
            CancellationToken cancellationToken = default)
        {
            // Get task from database to ensure it exists
            var dbTask = await _repository.GetByIdAsync(taskId, cancellationToken);
            if (dbTask == null)
            {
                throw new InvalidOperationException($"Task {taskId} not found");
            }

            // Update database entity
            dbTask.State = (int)status;
            dbTask.UpdatedAt = DateTime.UtcNow;
            
            if (progress.HasValue)
            {
                dbTask.Progress = progress.Value;
            }
            
            if (result != null)
            {
                dbTask.Result = JsonSerializer.Serialize(
                    result,
                    result.GetType(),
                    AsyncTaskJsonContext.Default);
            }
            
            if (!string.IsNullOrEmpty(error))
            {
                dbTask.Error = error;
                
                // If task is being set to pending for retry after a failure
                if (status == TaskState.Pending && dbTask.RetryCount < dbTask.MaxRetries)
                {
                    dbTask.RetryCount++;
                    dbTask.ProgressMessage = $"Retry {dbTask.RetryCount}/{dbTask.MaxRetries} scheduled";
                    
                    // Calculate next retry time if not already set
                    if (dbTask.NextRetryAt == null || dbTask.NextRetryAt <= DateTime.UtcNow)
                    {
                        // Use exponential backoff: 30s * 2^retryCount
                        var baseDelay = 30; // seconds
                        var delaySeconds = baseDelay * Math.Pow(2, dbTask.RetryCount - 1);
                        var maxDelay = 3600; // 1 hour max
                        delaySeconds = Math.Min(delaySeconds, maxDelay);
                        
                        // Add jitter
                        var jitter = new Random().NextDouble() * 0.4 - 0.2; // -0.2 to +0.2
                        delaySeconds = delaySeconds * (1 + jitter);
                        
                        dbTask.NextRetryAt = DateTime.UtcNow.AddSeconds(delaySeconds);
                    }
                }
            }
            
            if (IsTaskCompleted(status))
            {
                dbTask.CompletedAt = DateTime.UtcNow;
                dbTask.LeasedBy = null;
                dbTask.LeaseExpiryTime = null;
            }

            // Save to database
            await _repository.UpdateAsync(dbTask, cancellationToken);

            // Update cache
            var taskStatus = ConvertToTaskStatus(dbTask);
            await CacheTaskStatusAsync(taskId, taskStatus, cancellationToken, IsTaskCompleted(status));
            
            // Publish event if event bus is available
            if (_eventBus != null)
            {
                await _eventBus.PublishAsync(new AsyncTaskUpdated
                {
                    TaskId = taskId,
                    State = status.ToString(),
                    Progress = dbTask.Progress,
                    IsCompleted = IsTaskCompleted(status)
                }, cancellationToken);
            }
            
            _logger.LogInformation(
                "Updated task {TaskId} status to {Status} with progress {Progress}%", 
                taskId, 
                status, 
                dbTask.Progress);
        }

        /// <inheritdoc/>
        public async Task<AsyncTaskStatus> PollTaskUntilCompletedAsync(
            string taskId, 
            TimeSpan pollingInterval, 
            TimeSpan timeout, 
            CancellationToken cancellationToken = default)
        {
            var endTime = DateTime.UtcNow.Add(timeout);
            
            while (DateTime.UtcNow < endTime && !cancellationToken.IsCancellationRequested)
            {
                var status = await GetTaskStatusAsync(taskId, cancellationToken);
                if (status == null)
                {
                    throw new InvalidOperationException($"Task {taskId} not found");
                }

                if (IsTaskCompleted(status.State))
                {
                    return status;
                }

                await Task.Delay(pollingInterval, cancellationToken);
            }

            // If we get here, the task timed out
            await UpdateTaskStatusAsync(taskId, TaskState.TimedOut, error: "Task timed out", cancellationToken: cancellationToken);
            
            var finalStatus = await GetTaskStatusAsync(taskId, cancellationToken);
            return finalStatus!;
        }
    }
}
