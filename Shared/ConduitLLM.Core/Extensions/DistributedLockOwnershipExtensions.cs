using ConduitLLM.Core.Interfaces;

namespace ConduitLLM.Core.Extensions;

public static class DistributedLockOwnershipExtensions
{
    /// <summary>Cancel work on shutdown/request, deadline, or loss; never release ownership on a timer.</summary>
    public static CancellationTokenSource CreateOperationCancellation(
        this IDistributedLockOwnership ownership, CancellationToken cancellationToken,
        TimeSpan? operationDeadline = null)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ownership.HandleLostToken);
        if (operationDeadline.HasValue) { source.CancelAfter(operationDeadline.Value); }
        return source;
    }
}
