using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Interfaces;

/// <summary>
/// Request-time virtual-key operations required by the Gateway data plane.
/// </summary>
/// <remarks>
/// This contract is intentionally separate from key-management CRUD so a native
/// Gateway can use an AOT-safe persistence adapter without rooting the Admin
/// management surface or its EF query graph.
/// </remarks>
public interface IVirtualKeyRuntimeService
{
    /// <summary>
    /// Validates a virtual key for authentication without checking balance.
    /// </summary>
    Task<VirtualKeyValidationOutcome> ValidateVirtualKeyForAuthenticationAsync(
        string key,
        string? requestedModel = null);

    /// <summary>
    /// Validates a virtual key for a balance-protected operation.
    /// </summary>
    Task<VirtualKeyValidationOutcome> ValidateVirtualKeyAsync(
        string key,
        string? requestedModel = null);

    /// <summary>
    /// Applies request spend to a virtual key.
    /// </summary>
    Task<bool> UpdateSpendAsync(int keyId, decimal cost);

    /// <summary>
    /// Gets virtual-key information needed by request validation and orchestration.
    /// </summary>
    Task<VirtualKey?> GetVirtualKeyInfoForValidationAsync(
        int keyId,
        CancellationToken cancellationToken = default);
}
