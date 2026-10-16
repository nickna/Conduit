using ConduitLLM.Functions.Entities;

namespace ConduitLLM.Functions.Interfaces;

/// <summary>
/// Executes provider functions inline and records their results and calculated costs.
/// </summary>
public interface IFunctionExecutionService
{
    /// <summary>
    /// Awaits a provider function and records its result and calculated cost.
    /// Gateway middleware settles unkeyed HTTP requests. Keyed requests use a durable,
    /// virtual-key-scoped claim and settle its execution identity before returning or replaying a result.
    /// </summary>
    /// <param name="functionConfigurationId">The function configuration to use.</param>
    /// <param name="virtualKeyId">The virtual key for billing.</param>
    /// <param name="parameters">Function-specific parameters.</param>
    /// <param name="idempotencyKey">
    /// Optional opaque key (1-200 nonblank characters), scoped to the virtual key. Identical canonical
    /// requests replay the same execution; conflicting requests fail before provider dispatch.
    /// Claims are retained indefinitely and uncertain calls are never automatically dispatched again.
    /// The normal Gateway host configures the required durable store; unconfigured callers
    /// fail closed when a key is supplied.
    /// </param>
    /// <param name="metadata">Optional metadata for tracking.</param>
    /// <param name="providerToolName">
    /// For dynamic multi-tool providers (MCP), the provider-native tool name to invoke. Null for
    /// fixed-schema providers (Exa, Tavily), which map to a single implicit tool.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The function execution result with billing details.</returns>
    /// <exception cref="InvalidOperationException">Thrown when configuration or credentials are unavailable or invalid.</exception>
    /// <exception cref="ArgumentException">Thrown when required parameters are missing or invalid.</exception>
    Task<FunctionExecution> ExecuteAsync(
        int functionConfigurationId,
        int virtualKeyId,
        Dictionary<string, object> parameters,
        string? idempotencyKey = null,
        Dictionary<string, object>? metadata = null,
        string? providerToolName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the execution status by ID.
    /// </summary>
    /// <param name="executionId">The execution ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The execution entity, or null if not found.</returns>
    Task<FunctionExecution?> GetExecutionAsync(
        Guid executionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists executions for a virtual key.
    /// </summary>
    /// <param name="virtualKeyId">The virtual key ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of function executions.</returns>
    Task<List<FunctionExecution>> ListExecutionsAsync(
        int virtualKeyId,
        CancellationToken cancellationToken = default);
}
