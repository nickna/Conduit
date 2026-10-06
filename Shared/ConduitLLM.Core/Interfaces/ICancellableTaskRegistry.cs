namespace ConduitLLM.Core.Interfaces
{
    /// <summary>
    /// Registry for managing cancellable async tasks.
    /// Allows tasks to be cancelled after they've been started.
    /// </summary>
    public interface ICancellableTaskRegistry
    {
        /// <summary>
        /// Registers a task with its cancellation token source.
        /// </summary>
        /// <param name="taskId">The unique identifier for the task.</param>
        /// <param name="cts">The cancellation token source for the task.</param>
        void RegisterTask(string taskId, CancellationTokenSource cts);

        /// <summary>Installs the cancellation source for the authoritative durable claim, replacing a stale local registration.</summary>
        void RegisterClaimedTask(string taskId, CancellationTokenSource cts);

        /// <summary>
        /// Attempts to cancel a registered task.
        /// </summary>
        /// <param name="taskId">The unique identifier for the task to cancel.</param>
        /// <returns>True if the task was found and cancellation was requested; false otherwise.</returns>
        bool TryCancel(string taskId);

        /// <summary>
        /// Unregisters a task from the registry.
        /// Should be called when a task completes or is cancelled.
        /// </summary>
        /// <param name="taskId">The unique identifier for the task to unregister.</param>
        void UnregisterTask(string taskId);

        /// <summary>Removes a registration only if it still belongs to this execution.</summary>
        void UnregisterTask(string taskId, CancellationTokenSource owner);

        /// <summary>
        /// Gets the cancellation token for a registered task.
        /// </summary>
        /// <param name="taskId">The unique identifier for the task.</param>
        /// <param name="cancellationToken">The cancellation token if found; otherwise null.</param>
        /// <returns>True if the task was found; false otherwise.</returns>
        bool TryGetCancellationToken(string taskId, out CancellationToken? cancellationToken);

        /// <summary>
        /// Cancels all registered tasks.
        /// Useful for graceful shutdown scenarios.
        /// </summary>
        void CancelAll();
    }
}
