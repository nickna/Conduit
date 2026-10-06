using System.Text.Json;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Helper methods for HybridAsyncTaskService.
    /// </summary>
    public partial class HybridAsyncTaskService
    {
        private async Task CacheTaskStatusAsync(
            string taskId, 
            AsyncTaskStatus taskStatus, 
            CancellationToken cancellationToken,
            bool isCompleted = false)
        {
            var key = GetTaskKey(taskId);
            var json = JsonSerializer.Serialize(taskStatus, AsyncTaskJsonContext.Default.AsyncTaskStatus);
            
            var options = new DistributedCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromHours(isCompleted ? CACHE_EXPIRY_HOURS : 24)
            };
            
            // Retry logic with exponential backoff for cache operations
            var retryCount = 3;
            var delay = TimeSpan.FromMilliseconds(100);
            
            for (int i = 0; i < retryCount; i++)
            {
                try
                {
                    await _cache.SetStringAsync(key, json, options, cancellationToken);
                    return; // Success
                }
                catch (Exception ex) when (i < retryCount - 1)
                {
                    _logger.LogWarning(ex, "Cache operation failed for task {TaskId}, attempt {Attempt} of {MaxAttempts}", 
                        taskId, i + 1, retryCount);
                    await Task.Delay(delay, cancellationToken);
                    delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 2); // Exponential backoff
                }
            }
            
            // If we get here, all retries failed - let it throw
            await _cache.SetStringAsync(key, json, options, cancellationToken);
        }

        private static AsyncTaskStatus ConvertToTaskStatus(AsyncTask dbTask)
        {
            return new AsyncTaskStatus
            {
                TaskId = dbTask.Id,
                TaskType = dbTask.Type,
                State = (TaskState)dbTask.State,
                CreatedAt = dbTask.CreatedAt,
                UpdatedAt = dbTask.UpdatedAt,
                CompletedAt = dbTask.CompletedAt,
                Result = string.IsNullOrEmpty(dbTask.Result)
                    ? null
                    : JsonSerializer.Deserialize(dbTask.Result, AsyncTaskJsonContext.Default.JsonElement),
                Error = dbTask.Error,
                Metadata = string.IsNullOrEmpty(dbTask.Metadata)
                    ? null
                    : JsonSerializer.Deserialize(dbTask.Metadata, AsyncTaskJsonContext.Default.TaskMetadata),
                Progress = dbTask.Progress,
                ProgressMessage = dbTask.ProgressMessage,
                RetryCount = dbTask.RetryCount,
                MaxRetries = dbTask.MaxRetries,
                IsRetryable = dbTask.IsRetryable,
                NextRetryAt = dbTask.NextRetryAt
            };
        }

        private static int GetVirtualKeyIdFromMetadata(object metadata)
        {
            if (metadata == null)
            {
                throw new ArgumentNullException(nameof(metadata), "Metadata cannot be null when creating a task");
            }

            // First check if it's already a TaskMetadata instance
            if (metadata is TaskMetadata taskMetadata)
            {
                return taskMetadata.VirtualKeyId;
            }

            try
            {
                // Extract VirtualKeyId from metadata
                var json = JsonSerializer.Serialize(
                    metadata,
                    metadata.GetType(),
                    AsyncTaskJsonContext.Default);
                using var jsonDoc = JsonDocument.Parse(json);
                var root = jsonDoc.RootElement;

                // Try different property name variations (case-insensitive)
                string[] propertyNames = { "virtualKeyId", "VirtualKeyId", "virtualkeyid" };
                
                foreach (var propertyName in propertyNames)
                {
                    if (root.TryGetProperty(propertyName, out var element))
                    {
                        // Try to get as int first
                        if (element.TryGetInt32(out var intValue))
                        {
                            return intValue;
                        }
                        
                        // Try to parse string as int
                        if (element.ValueKind == JsonValueKind.String)
                        {
                            var stringValue = element.GetString();
                            if (!string.IsNullOrEmpty(stringValue) && int.TryParse(stringValue, out var parsedValue))
                            {
                                return parsedValue;
                            }
                        }
                    }
                }

                // More detailed error message to help debugging
                var availableProperties = string.Join(", ", root.EnumerateObject().Select(p => p.Name));
                throw new InvalidOperationException(
                    $"VirtualKeyId not found in metadata. Available properties: {availableProperties}. " +
                    "Metadata must contain a 'virtualKeyId' or 'VirtualKeyId' property with an integer value.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("Failed to parse metadata as JSON", ex);
            }
        }

        private static bool IsTaskCompleted(TaskState state)
        {
            return state == TaskState.Completed || 
                   state == TaskState.Failed || 
                   state == TaskState.Cancelled ||
                   state == TaskState.TimedOut ||
                   state == TaskState.Indeterminate;
        }

        private static string GetTaskKey(string taskId) => $"{TASK_KEY_PREFIX}{taskId}";

        private static string GenerateTaskId() => $"task_{Guid.NewGuid():N}";
    }
}
