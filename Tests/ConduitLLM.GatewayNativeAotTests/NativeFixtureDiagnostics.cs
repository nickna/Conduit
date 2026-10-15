using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

/// <summary>Bounded fixture failure diagnostics that never inspect messages, data, or request values.</summary>
internal static class NativeFixtureDiagnostics
{
    private const int MaximumFailures = 8;
    private const int MaximumFrames = 4;
    private const int MaximumSymbolLength = 160;
    private static readonly ConditionalWeakTable<Exception, Projection> Projections = new();

    internal static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var pending = new Queue<(Exception Error, string Position)>();
        pending.Enqueue((exception, "0"));
        var result = new StringBuilder();
        var failures = 0;
        var omitted = false;
        while (pending.Count > 0 && failures++ < MaximumFailures)
        {
            var (error, position) = pending.Dequeue();
            if (result.Length > 0) result.Append("; ");
            result.Append("failure=").Append(position).Append(" type=").Append(Symbol(error.GetType().FullName))
                .Append(" hresult=0x").Append(error.HResult.ToString("X8", CultureInfo.InvariantCulture));
            Projections.TryGetValue(error, out var projection);
            if (projection is not null) result.Append(" context=").Append(projection.Context);
            var trace = new StackTrace(error, fNeedFileInfo: false);
            for (var frameIndex = 0; frameIndex < Math.Min(MaximumFrames, trace.FrameCount); frameIndex++)
            {
                var method = DiagnosticMethodInfo.Create(trace.GetFrame(frameIndex)!);
                result.Append(" at=").Append(Symbol(method?.DeclaringTypeName)).Append('.')
                    .Append(Symbol(method?.Name));
            }

            // Bound breadth as well as traversal. Aggregate order identifies the
            // primary failure before independently attempted cleanup failures.
            IEnumerable<Exception> inner = projection is not null ? [projection.Cause]
                : error is AggregateException aggregate ? aggregate.InnerExceptions.Take(MaximumFailures)
                : error.InnerException is { } innerException ? [innerException] : Enumerable.Empty<Exception>();
            var index = 0;
            foreach (var cause in inner)
            {
                if (pending.Count + failures >= MaximumFailures)
                {
                    omitted = true;
                    break;
                }
                pending.Enqueue((cause, position + "." + (index++).ToString(CultureInfo.InvariantCulture)));
            }
        }
        if (pending.Count > 0 || omitted) result.Append("; additional failures omitted");
        return result.ToString();
    }

    internal static InvalidOperationException Failure(string context, Exception exception)
    {
        // Callers supply only closed fixture stage/context strings. A private
        // weak registration preserves the original cause for later Describe
        // calls without exposing it to Exception.ToString via InnerException.
        var safeContext = Symbol(context);
        var failure = new InvalidOperationException(safeContext + " " + Describe(exception));
        Projections.Add(failure, new Projection(safeContext, exception));
        return failure;
    }

    private static string Symbol(string? value) => value is null ? "unknown"
        : value.Length <= MaximumSymbolLength ? value : value[..MaximumSymbolLength];

    private sealed class Projection(string context, Exception cause)
    {
        internal string Context { get; } = context;
        internal Exception Cause { get; } = cause;
    }
}
