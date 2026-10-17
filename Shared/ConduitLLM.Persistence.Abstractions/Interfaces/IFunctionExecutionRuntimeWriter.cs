using ConduitLLM.Functions.Entities;

namespace ConduitLLM.Persistence.Interfaces;

/// <summary>Writes detached scalar execution snapshots without navigation graphs.</summary>
public interface IFunctionExecutionRuntimeWriter
{
    /// <summary>
    /// Inserts all execution scalars. An empty ID is replaced on the supplied instance;
    /// caller-supplied state, version and timestamps are preserved. Navigation properties
    /// must be absent. ID generation precedes the database attempt, including cancellation.
    /// Database failures and cancellation propagate to the caller.
    /// </summary>
    Task<Guid> CreateAsync(FunctionExecution execution, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces all non-key scalars when ID and the supplied version match. Increments the
    /// supplied instance's version before saving and restores it if saving fails or returns
    /// a concurrency miss; integer overflow follows unchecked .NET arithmetic. An empty ID
    /// is generated as with the JIT repository and retained even if the attempt fails.
    /// Returns false for missing/stale rows; retry requires reloading a fresh snapshot.
    /// Database failures and cancellation propagate. Does not derive lifecycle fields,
    /// insert missing rows, save navigation graphs or create audit events.
    /// </summary>
    Task<bool> UpdateAsync(FunctionExecution execution, CancellationToken cancellationToken = default);
}
