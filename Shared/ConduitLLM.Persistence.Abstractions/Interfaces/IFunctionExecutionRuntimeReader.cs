using ConduitLLM.Functions.Entities;

namespace ConduitLLM.Persistence.Interfaces;

/// <summary>Reads detached execution snapshots with their scalar configuration.</summary>
public interface IFunctionExecutionRuntimeReader
{
    /// <summary>
    /// Reads an exact ID, including empty IDs, or returns null. Includes the scalar
    /// configuration and its inverse reference to this execution; audits and cost
    /// mappings remain unloaded. Included graphs are outside the scalar writer.
    /// </summary>
    Task<FunctionExecution?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every execution for the exact virtual-key ID, ordered by RequestedAt
    /// descending with unspecified ties. Applies no state or configuration-enabled
    /// filter. Each row owns a separate included configuration instance, even when
    /// configurations share an ID. Reads and cancellation do not change persistence.
    /// </summary>
    Task<IReadOnlyList<FunctionExecution>> ListByVirtualKeyAsync(int virtualKeyId, CancellationToken cancellationToken = default);
}
