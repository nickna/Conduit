using ConduitLLM.Core.Models;
using ConduitLLM.Configuration.Utilities;

namespace ConduitLLM.Admin.Interfaces;

/// <summary>
/// Service for processing refunds for virtual key group usage
/// </summary>
public interface IRefundService
{
    Task<RefundResult> ProcessRefundConditionalAsync(int virtualKeyGroupId, string modelId,
        Usage originalUsage, Usage refundUsage, string refundReason, string originalTransactionId,
        string idempotencyKey, string initiatedBy, string? initiatedByUserId, int? requestLogId,
        ResourcePrecondition precondition, CancellationToken cancellationToken);
    /// <summary>
    /// Processes a refund for a virtual key group
    /// </summary>
    /// <param name="virtualKeyGroupId">The virtual key group ID to refund</param>
    /// <param name="modelId">The model ID that was used</param>
    /// <param name="originalUsage">The original usage data that was charged</param>
    /// <param name="refundUsage">The usage data to be refunded</param>
    /// <param name="refundReason">The reason for the refund</param>
    /// <param name="originalTransactionId">ID of the original debit transaction being refunded</param>
    /// <param name="idempotencyKey">Caller-supplied refund operation identifier</param>
    /// <param name="initiatedBy">User who initiated the refund</param>
    /// <param name="initiatedByUserId">Clerk user ID if initiated by an admin user</param>
    /// <param name="requestLogId">Optional ID of the original request log, used to prorate refunds of
    /// provider-cost-billed requests from the amount actually charged.</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A RefundResult containing the refund details and transaction ID</returns>
    /// <exception cref="InvalidOperationException">Thrown when the virtual key group is not found</exception>
    /// <exception cref="ArgumentException">Thrown when refund validation fails</exception>
    Task<RefundResult> ProcessRefundAsync(
        int virtualKeyGroupId,
        string modelId,
        Usage originalUsage,
        Usage refundUsage,
        string refundReason,
        string originalTransactionId,
        string idempotencyKey,
        string initiatedBy,
        string? initiatedByUserId,
        int? requestLogId = null,
        CancellationToken cancellationToken = default);
}
