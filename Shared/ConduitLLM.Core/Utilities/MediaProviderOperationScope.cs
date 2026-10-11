using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Utilities;

/// <summary>Per-invocation callback that flows through client decorators without shared mutable callbacks.</summary>
public sealed class MediaProviderOperationScope : IDisposable
{
    private static readonly AsyncLocal<Func<MediaProviderOperation, CancellationToken, Task>?> Current = new();
    private readonly Func<MediaProviderOperation, CancellationToken, Task>? _previous;
    public MediaProviderOperationScope(Func<MediaProviderOperation, CancellationToken, Task> callback)
    {
        _previous = Current.Value;
        Current.Value = callback;
    }
    public static Task RecordAsync(MediaProviderOperation provider, CancellationToken cancellationToken) =>
        Current.Value?.Invoke(provider, cancellationToken) ?? Task.CompletedTask;
    public void Dispose() => Current.Value = _previous;
}
