using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

using Npgsql;

namespace ConduitLLM.IntegrationTests.Infrastructure;

/// <summary>Recent exception metadata only; never formats log state or retains raw exceptions.</summary>
internal sealed class BoundedExceptionDiagnostics
{
    internal const int MaximumRecords = 16;
    private const int MaximumExceptions = 8;
    private const int MaximumSymbolLength = 160;
    private readonly object _gate = new();
    private readonly Queue<string> _records = new();

    internal ILogger<T> CreateLogger<T>() => new CaptureLogger<T>(this);

    internal string Snapshot()
    {
        lock (_gate)
            return _records.Count == 0 ? "(none)" : string.Join(Environment.NewLine, _records);
    }

    private void Capture(string category, LogLevel level, EventId eventId, Exception exception)
    {
        var record = new StringBuilder().Append(Symbol(category)).Append(' ').Append(level)
            .Append(" event=").Append(eventId.Id.ToString(CultureInfo.InvariantCulture));
        var pending = new Queue<Exception>();
        pending.Enqueue(exception);
        var encountered = 0;
        while (pending.Count > 0 && encountered++ < MaximumExceptions)
        {
            var current = pending.Dequeue();
            record.Append(" type=").Append(Symbol(current.GetType().FullName));
            if (current is PostgresException postgres && postgres.SqlState is { Length: 5 } code
                && code.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'Z'))
                record.Append(" SQLSTATE=").Append(code);

            // Bound aggregate breadth as well as depth; do not inspect Message,
            // Data, SQL, connection strings, stack traces, or formatter/state.
            IEnumerable<Exception> inner = current is AggregateException aggregate ? aggregate.InnerExceptions
                : current.InnerException is { } nested ? new[] { nested } : Enumerable.Empty<Exception>();
            foreach (var cause in inner)
            {
                if (encountered + pending.Count >= MaximumExceptions) break;
                pending.Enqueue(cause);
            }
        }
        lock (_gate)
        {
            if (_records.Count == MaximumRecords) _records.Dequeue();
            _records.Enqueue(record.ToString());
        }
    }

    private static string Symbol(string? value) => value is null ? "unknown"
        : value.Length <= MaximumSymbolLength ? value : value[..MaximumSymbolLength];

    private sealed class CaptureLogger<T>(BoundedExceptionDiagnostics diagnostics) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null && IsEnabled(logLevel))
                diagnostics.Capture(typeof(T).FullName ?? typeof(T).Name, logLevel, eventId, exception);
        }
    }
}
