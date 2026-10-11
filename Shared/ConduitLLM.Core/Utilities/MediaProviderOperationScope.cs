using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Utilities;

/// <summary>Per-invocation callback that flows through client decorators without shared mutable callbacks.</summary>
public sealed class MediaProviderOperationScope : IDisposable
{
    private static readonly AsyncLocal<MediaProviderOperationScope?> Current = new();
    private readonly MediaProviderOperationScope? _previous;
    private readonly Func<MediaProviderOperation, CancellationToken, Task> _callback;
    private bool _accepted;
    public MediaProviderOperationScope(Func<MediaProviderOperation, CancellationToken, Task> callback)
    {
        _previous = Current.Value;
        _callback = callback;
        Current.Value = this;
    }
    public static bool HasAcceptedOperation => Current.Value?._accepted == true;
    public static Task RecordAsync(MediaProviderOperation provider, CancellationToken cancellationToken)
    {
        var scope = Current.Value;
        if (scope == null) return Task.CompletedTask;
        // Acceptance must stop credential failover even if persistence itself fails.
        scope._accepted = true;
        return scope._callback(provider, cancellationToken);
    }
    public void Dispose() => Current.Value = _previous;
}
