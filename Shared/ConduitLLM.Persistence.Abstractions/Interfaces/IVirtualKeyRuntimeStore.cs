using ConduitLLM.Persistence;

namespace ConduitLLM.Persistence.Interfaces;

/// <summary>
/// Fixed-shape persistence required by Gateway virtual-key authentication and billing.
/// </summary>
public interface IVirtualKeyRuntimeStore
{
    Task<VirtualKeyRuntimeRecord?> GetByHashAsync(
        string keyHash,
        CancellationToken cancellationToken = default);

    Task<VirtualKeyRuntimeRecord?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<VirtualKeyBalanceAdjustmentResult> AdjustBalanceAsync(
        VirtualKeyBalanceAdjustment adjustment,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetKeyHashesByGroupIdAsync(
        int groupId,
        CancellationToken cancellationToken = default);

    Task<bool> TouchAsync(
        int id,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);
}
