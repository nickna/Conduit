using ConduitLLM.Persistence;

namespace ConduitLLM.Persistence.Interfaces;

/// <summary>Typed persistence for the process-local request-log queue.</summary>
public interface IRequestLogBatchRuntimeStore
{
    /// <summary>Allocates identities without inserting rows. Gaps are permitted.</summary>
    Task<IReadOnlyList<int>> AllocateIdsAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts assigned rows, accepting a replay only when every persisted scalar
    /// matches under database storage semantics. Completion errors must preserve
    /// the caller's assigned identities, including after an ambiguous commit.
    /// </summary>
    Task WriteBatchAsync(IReadOnlyList<RequestLogRuntimeBatchEntry> entries,
        CancellationToken cancellationToken = default);
}
