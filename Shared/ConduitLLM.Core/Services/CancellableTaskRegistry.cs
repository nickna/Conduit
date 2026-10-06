using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ConduitLLM.Core.Interfaces;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Thread-safe registry for managing cancellable async tasks.
    /// </summary>
    public class CancellableTaskRegistry : ICancellableTaskRegistry, IDisposable
    {
        private readonly ConcurrentDictionary<string, TaskRegistration> _registry = new();
        private readonly ILogger<CancellableTaskRegistry> _logger;
        private readonly TimeSpan _gracePeriod;
        private readonly Timer _cleanupTimer;
        private bool _disposed;

        public CancellableTaskRegistry(ILogger<CancellableTaskRegistry> logger) : this(logger, TimeSpan.FromSeconds(5))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="CancellableTaskRegistry"/> class with a custom grace period.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="gracePeriod">Grace period to keep cancelled tasks before removal.</param>
        public CancellableTaskRegistry(ILogger<CancellableTaskRegistry> logger, TimeSpan gracePeriod)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _gracePeriod = gracePeriod;
            
            // Start cleanup timer that runs every second
            _cleanupTimer = new Timer(CleanupExpiredTasks, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }

        /// <inheritdoc/>
        public void RegisterTask(string taskId, CancellationTokenSource cts)
        {
            if (string.IsNullOrWhiteSpace(taskId))
                throw new ArgumentException("Task ID cannot be null or whitespace.", nameof(taskId));
            
            ArgumentNullException.ThrowIfNull(cts);

            var registration = new TaskRegistration
            {
                CancellationTokenSource = cts,
                RegisteredAt = DateTime.UtcNow
            };

            if (_registry.TryAdd(taskId, registration))
            {
                _logger.LogDebug("Registered cancellable task {TaskId}", taskId);
                
                // Mark as cancelled when the token is cancelled (don't unregister immediately)
                cts.Token.Register(() => MarkTaskAsCancelled(taskId, registration));
            }
            else
            {
                _logger.LogWarning("Task {TaskId} is already registered", taskId);
            }
        }

        /// <inheritdoc/>
        public void RegisterClaimedTask(string taskId, CancellationTokenSource cts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
            ArgumentNullException.ThrowIfNull(cts);
            var registration = new TaskRegistration { CancellationTokenSource = cts, RegisteredAt = DateTime.UtcNow };
            _registry[taskId] = registration;
            cts.Token.Register(() => MarkTaskAsCancelled(taskId, registration));
        }

        /// <inheritdoc/>
        public void UnregisterTask(string taskId, CancellationTokenSource owner)
        {
            if (_registry.TryGetValue(taskId, out var registration) && ReferenceEquals(registration.CancellationTokenSource, owner)
                && _registry.TryRemove(new KeyValuePair<string, TaskRegistration>(taskId, registration)))
                owner.Dispose();
        }

        /// <inheritdoc/>
        public bool TryCancel(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId))
                return false;

            if (_registry.TryGetValue(taskId, out var registration))
            {
                try
                {
                    if (!registration.CancellationTokenSource.IsCancellationRequested)
                    {
                        registration.CancellationTokenSource.Cancel();
                        _logger.LogInformation("Cancelled task {TaskId}", taskId);
                        return true;
                    }
                    else
                    {
                        _logger.LogDebug("Task {TaskId} was already cancelled", taskId);
                        return false;
                    }
                }
                catch (ObjectDisposedException)
                {
                    _logger.LogWarning("Cancellation token source for task {TaskId} was already disposed", taskId);
                    UnregisterTask(taskId, registration.CancellationTokenSource);
                    return false;
                }
            }

            _logger.LogDebug("Task {TaskId} not found in registry", taskId);
            return false;
        }

        /// <inheritdoc/>
        public void UnregisterTask(string taskId)
        {
            if (string.IsNullOrWhiteSpace(taskId))
                return;

            if (_registry.TryRemove(taskId, out var registration))
            {
                _logger.LogDebug("Unregistered task {TaskId}", taskId);
                
                // Dispose the CancellationTokenSource if not already disposed
                try
                {
                    registration.CancellationTokenSource.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed, ignore
                }
            }
        }

        /// <inheritdoc/>
        public bool TryGetCancellationToken(string taskId, out CancellationToken? cancellationToken)
        {
            cancellationToken = null;
            
            if (string.IsNullOrWhiteSpace(taskId))
                return false;

            if (_registry.TryGetValue(taskId, out var registration))
            {
                try
                {
                    cancellationToken = registration.CancellationTokenSource.Token;
                    return true;
                }
                catch (ObjectDisposedException)
                {
                    _logger.LogWarning("Cancellation token source for task {TaskId} was disposed", taskId);
                    UnregisterTask(taskId, registration.CancellationTokenSource);
                    return false;
                }
            }

            return false;
        }

        /// <inheritdoc/>
        public void CancelAll()
        {
            _logger.LogInformation("Cancelling all {Count} registered tasks", _registry.Count);
            
            foreach (var kvp in _registry)
            {
                try
                {
                    if (!kvp.Value.CancellationTokenSource.IsCancellationRequested)
                    {
                        kvp.Value.CancellationTokenSource.Cancel();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Ignore disposed tokens
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error cancelling task {TaskId}", kvp.Key);
                }
            }
            
            // Clear the registry after cancelling all
            _registry.Clear();
        }

        /// <summary>
        /// Marks a task as cancelled and sets the cancellation time.
        /// </summary>
        private void MarkTaskAsCancelled(string taskId, TaskRegistration registration)
        {
            if (_registry.TryGetValue(taskId, out var current) && ReferenceEquals(current, registration))
            {
                registration.CancelledAt = DateTime.UtcNow;
                _logger.LogDebug("Marked task {TaskId} as cancelled, will be removed after grace period", taskId);
            }
        }

        /// <summary>
        /// Cleans up tasks that have been cancelled for longer than the grace period.
        /// </summary>
        private void CleanupExpiredTasks(object? state)
        {
            var now = DateTime.UtcNow;
            var tasksToRemove = new List<(string Id, CancellationTokenSource Owner)>();

            foreach (var kvp in _registry)
            {
                if (kvp.Value.CancelledAt.HasValue)
                {
                    var timeSinceCancellation = now - kvp.Value.CancelledAt.Value;
                    if (timeSinceCancellation > _gracePeriod)
                    {
                        tasksToRemove.Add((kvp.Key, kvp.Value.CancellationTokenSource));
                    }
                }
            }

            foreach (var task in tasksToRemove)
            {
                UnregisterTask(task.Id, task.Owner);
                _logger.LogDebug("Removed cancelled task {TaskId} after grace period", task.Id);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _logger.LogDebug("Disposing CancellableTaskRegistry");
            
            // Stop the cleanup timer
            _cleanupTimer?.Dispose();
            
            // Cancel and dispose all registered tasks
            foreach (var kvp in _registry)
            {
                try
                {
                    kvp.Value.CancellationTokenSource.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing cancellation token source for task {TaskId}", kvp.Key);
                }
            }
            
            _registry.Clear();
            _disposed = true;
        }

        /// <summary>
        /// Represents a task registration with metadata.
        /// </summary>
        private class TaskRegistration
        {
            public CancellationTokenSource CancellationTokenSource { get; set; } = null!;
            public DateTime RegisteredAt { get; set; }
            public DateTime? CancelledAt { get; set; }
        }
    }
}
