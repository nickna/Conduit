using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;

namespace ConduitLLM.Benchmarks.Messaging;

/// <summary>A synthetic handler. Returning does not itself acknowledge the durable inbox.</summary>
internal sealed class BenchHandler<TEvent>(DeliveryCollector collector) : IEventHandler<TEvent>
    where TEvent : class
{
    public async Task HandleAsync(TEvent message, IEventContext context)
    {
        var ticket = collector.Start(message, context.MessageId);
        Exception? failure = null;
        try
        {
            if (collector.HandlerCostMs > 0)
                await Task.Delay(collector.HandlerCostMs, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; throw; }
        finally { collector.Finish(ticket, failure); }
    }
}

internal sealed record DeliveryTicket(string EventId, Guid MessageId);
internal sealed record DeliveryRecord(string EventId, int Sequence, Guid? MessageId,
    long PublishedTicks, long StartedTicks, long FinishedTicks, long SettlementObservedTicks,
    decimal SyntheticAmount, string? Error);
internal sealed record StoreSample(long ObservedTicks, long Ready, long Scheduled, long Incoming,
    long Outgoing, long DeadLetters, int NewlyObservedHandled);
internal sealed record StoreSetup(string[] RequiredRelations, string[] PresentRelations,
    int Polls, double ElapsedMilliseconds, bool Ready, string? Error);

internal static class StoreSchemaReadiness
{
    public static async Task WaitAsync(string[] required,
        Func<CancellationToken, Task<string[]>> probe, Action<StoreSetup> observed,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var polls = 0;
        string[] present = [];
        var expected = new HashSet<string>(required, StringComparer.Ordinal);
        try
        {
            if (required.Length == 0 || expected.Count != required.Length)
                throw new ArgumentException("Require distinct schema relation names.");
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                polls++;
                present = await probe(cancellationToken).ConfigureAwait(false);
                if (present.Distinct(StringComparer.Ordinal).Count() != present.Length || present.Any(name => !expected.Contains(name)))
                    throw new InvalidOperationException("Invalid schema readiness response.");
                if (expected.SetEquals(present))
                {
                    observed(new StoreSetup(required.ToArray(), present.ToArray(), polls, watch.Elapsed.TotalMilliseconds, true, null));
                    return;
                }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            observed(new StoreSetup(required.ToArray(), present.ToArray(), polls, watch.Elapsed.TotalMilliseconds,
                false, $"{error.GetType().Name}: {error.Message}"));
            throw;
        }
    }
}
internal sealed record DeliverySnapshot(string PhaseId, int Expected, int Registered, int Started,
    int Completed, int Settled, int InFlight, int MaxConcurrency, int DuplicateAttempts,
    int OrderViolations, string[] Errors, DeliveryRecord[] Deliveries, StoreSample[] StoreSamples)
{
    public bool Complete => Registered == Expected && Completed == Expected && Settled == Expected &&
        InFlight == 0 && Errors.Length == 0 && StoreSamples.Length > 0 &&
        StoreSamples[^1] is { Ready: 0, Scheduled: 0, Incoming: 0, Outgoing: 0, DeadLetters: 0 };
    public decimal RegisteredSyntheticAmount => Deliveries.Sum(d => d.SyntheticAmount);
    public decimal SettledSyntheticAmount => Deliveries.Where(d => d.SettlementObservedTicks > 0).Sum(d => d.SyntheticAmount);
}

/// <summary>Exact logical/transport IDs, finished handlers, and independently observed durable settlement.</summary>
internal sealed class DeliveryCollector(int handlerCostMs)
{
    private sealed class Delivery(string id, int sequence, long publishedTicks, decimal amount)
    {
        public readonly string EventId = id;
        public readonly int Sequence = sequence;
        public readonly long PublishedTicks = publishedTicks;
        public readonly decimal Amount = amount;
        public Guid? MessageId;
        public long StartedTicks, FinishedTicks, SettledTicks;
        public string? Error;
    }
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Delivery> _deliveries = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Delivery> _transportIds = new();
    private readonly HashSet<int> _sequences = new();
    private readonly List<string> _errors = new();
    private readonly List<StoreSample> _samples = new();
    private TaskCompletionSource<bool> _completion = NewCompletion();
    private int _expected, _inFlight, _maxConcurrency, _duplicates, _orderViolations, _started, _finished, _settled;
    private bool _requireOrdering;
    private string _phaseId = string.Empty;

    public int HandlerCostMs { get; } = handlerCostMs >= 0 ? handlerCostMs : throw new ArgumentOutOfRangeException(nameof(handlerCostMs));
    public string PhaseId { get { lock (_sync) return _phaseId; } }
    private static TaskCompletionSource<bool> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void BeginMeasurement(int expected, bool requireOrdering = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expected);
        lock (_sync)
        {
            if (_phaseId.Length > 0 && !CompleteUnsafe())
                throw new InvalidOperationException("Cannot reset an unfinished or unsettled phase.");
            _expected = expected;
            _phaseId = Guid.NewGuid().ToString("N");
            _requireOrdering = requireOrdering;
            _deliveries.Clear(); _transportIds.Clear(); _sequences.Clear(); _errors.Clear(); _samples.Clear();
            _inFlight = _maxConcurrency = _duplicates = _orderViolations = _started = _finished = _settled = 0;
            _completion = NewCompletion();
        }
    }

    public void RegisterPublished(object message, int sequence, long publishedTicks)
    {
        lock (_sync)
        {
            if (message is not IDomainEvent item || item.CorrelationId != _phaseId ||
                string.IsNullOrWhiteSpace(item.EventId) || sequence < 0 || sequence >= _expected || publishedTicks <= 0)
                throw new InvalidOperationException("Invalid or foreign published event identity.");
            if (!_sequences.Add(sequence) ||
                !_deliveries.TryAdd(item.EventId, new Delivery(item.EventId, sequence, publishedTicks,
                    message is SpendUpdateRequested spend ? spend.Amount : 0m)))
                throw new InvalidOperationException("Duplicate published event identity or sequence.");
        }
    }

    public DeliveryTicket Start(object message, Guid? messageId)
    {
        lock (_sync)
        {
            if (message is not IDomainEvent item || item.CorrelationId != _phaseId ||
                !_deliveries.TryGetValue(item.EventId, out var delivery))
                return Reject("Foreign or unregistered handler event.");
            if (messageId is null || messageId == Guid.Empty)
                return Reject("Handler delivery has no durable transport ID.");
            if (delivery.StartedTicks != 0 || _transportIds.ContainsKey(messageId.Value))
            {
                _duplicates++;
                return Reject("Duplicate logical event or transport ID.");
            }
            if (message is SpendUpdateRequested spend && spend.Amount != delivery.Amount)
                return Reject("Synthetic spend payload amount changed across the transport.");
            delivery.MessageId = messageId;
            delivery.StartedTicks = Stopwatch.GetTimestamp();
            _transportIds.Add(messageId.Value, delivery);
            _inFlight++;
            _maxConcurrency = Math.Max(_maxConcurrency, _inFlight);
            if (_requireOrdering && (delivery.Sequence != _started || _inFlight != 1))
            {
                _orderViolations++;
                _errors.Add("Strict handler start order/concurrency violated.");
            }
            _started++;
            return new DeliveryTicket(delivery.EventId, messageId.Value);
        }
    }

    private DeliveryTicket Reject(string message)
    {
        _errors.Add(message);
        throw new InvalidOperationException(message);
    }

    public void Finish(DeliveryTicket ticket, Exception? failure = null)
    {
        lock (_sync)
        {
            if (!_deliveries.TryGetValue(ticket.EventId, out var delivery) ||
                delivery.MessageId != ticket.MessageId || delivery.FinishedTicks != 0)
                throw new InvalidOperationException("Unknown or already finished handler ticket.");
            delivery.FinishedTicks = Stopwatch.GetTimestamp();
            _inFlight--;
            if (failure is not null)
            {
                delivery.Error = failure.GetType().Name;
                _errors.Add($"Handler failed: {delivery.Error}");
            }
            else
            {
                if (_requireOrdering && delivery.Sequence != _finished)
                {
                    _orderViolations++;
                    _errors.Add("Strict handler completion order violated.");
                }
                _finished++;
            }
        }
    }

    public Guid[] PendingTransportIds()
    {
        lock (_sync) return _transportIds.Where(p => p.Value.SettledTicks == 0).Select(p => p.Key).ToArray();
    }

    public void ObserveStore(long ticks, IReadOnlyCollection<Guid> handledIds,
        long ready, long scheduled, long incoming, long outgoing, long deadLetters)
    {
        lock (_sync)
        {
            if (ticks <= 0 || new[] { ready, scheduled, incoming, outgoing, deadLetters }.Any(n => n < 0))
                throw new InvalidOperationException("Invalid store observation.");
            var newlyHandled = 0;
            foreach (var id in handledIds)
            {
                if (!_transportIds.TryGetValue(id, out var delivery) || delivery.FinishedTicks == 0 ||
                    delivery.Error is not null || ticks < delivery.FinishedTicks)
                {
                    _errors.Add("Store settlement does not follow a known successful handler completion.");
                    continue;
                }
                if (delivery.SettledTicks == 0) { delivery.SettledTicks = ticks; newlyHandled++; _settled++; }
            }
            _samples.Add(new StoreSample(ticks, ready, scheduled, incoming, outgoing, deadLetters, newlyHandled));
            if (deadLetters != 0) _errors.Add("Dead-letter rows remain in the isolated benchmark store.");
            if (CompleteUnsafe()) _completion.TrySetResult(true);
        }
    }

    public void Fail(string error) { lock (_sync) _errors.Add(error); }
    public async Task<bool> WaitForCompletionAsync(TimeSpan timeout)
    {
        try { await _completion.Task.WaitAsync(timeout).ConfigureAwait(false); }
        catch (TimeoutException) { return false; }
        return Snapshot().Complete;
    }
    public DeliverySnapshot Snapshot() { lock (_sync) return SnapshotUnsafe(); }
    private bool CompleteUnsafe() => _deliveries.Count == _expected && _finished == _expected && _settled == _expected &&
        _inFlight == 0 && _errors.Count == 0 && _samples.Count > 0 &&
        _samples[^1] is { Ready: 0, Scheduled: 0, Incoming: 0, Outgoing: 0, DeadLetters: 0 };
    private DeliverySnapshot SnapshotUnsafe() => new(_phaseId, _expected, _deliveries.Count,
        _started, _finished, _deliveries.Values.Count(d => d.SettledTicks > 0), _inFlight,
        _maxConcurrency, _duplicates, _orderViolations, _errors.ToArray(),
        _deliveries.Values.OrderBy(d => d.Sequence).Select(d => new DeliveryRecord(d.EventId,
            d.Sequence, d.MessageId, d.PublishedTicks, d.StartedTicks, d.FinishedTicks,
            d.SettledTicks, d.Amount, d.Error)).ToArray(), _samples.ToArray());
}

internal sealed class HarnessOptions
{
    public string Queue { get; private set; } = "spend";
    public int MessageCount { get; private set; } = 2000;
    public IReadOnlyList<int> HandlerCostMs { get; private set; } = new[] { 0 };
    public int PublishParallelism { get; private set; } = 1;
    public double OfferedRate { get; private set; }
    public int WarmupCount { get; private set; } = 10;
    public int? ReceiveBatchOverride { get; private set; }
    public TimeSpan Timeout { get; private set; } = TimeSpan.FromSeconds(300);
    public string Database { get; private set; } = $"conduit_msgbench_{Guid.NewGuid():N}";
    public string? AdminConnectionString { get; private set; }
    public string OutputPath { get; private set; } = $"artifacts/queue-benchmark/{Guid.NewGuid():N}.json";
    public string DatabaseLatencyLabel { get; private set; } = "uncontrolled";

    public static HarnessOptions? Parse(string[] args)
    {
        try
        {
            var o = new HarnessOptions();
            if (args.Length % 2 != 0) return null;
            for (var i = 0; i < args.Length; i += 2)
            {
                var value = args[i + 1];
                switch (args[i])
                {
                    case "--queue": o.Queue = value; break;
                    case "--messages": o.MessageCount = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--handler-cost-ms": o.HandlerCostMs = value.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray(); break;
                    case "--publish-parallelism": o.PublishParallelism = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--rate": o.OfferedRate = double.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--warmup": o.WarmupCount = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--receive-batch": o.ReceiveBatchOverride = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "--timeout-seconds": o.Timeout = TimeSpan.FromSeconds(int.Parse(value, CultureInfo.InvariantCulture)); break;
                    case "--database": o.Database = value; break;
                    case "--admin-connection": o.AdminConnectionString = value; break;
                    case "--output": o.OutputPath = value; break;
                    case "--db-latency-label": o.DatabaseLatencyLabel = value; break;
                    default: return null;
                }
            }
            if (string.IsNullOrWhiteSpace(o.AdminConnectionString) || o.MessageCount < 2 || o.MessageCount > 1_000_000 ||
                o.WarmupCount < 1 || o.WarmupCount > 100_000 || o.PublishParallelism < 1 || o.PublishParallelism > 256 ||
                o.HandlerCostMs.Count == 0 || o.HandlerCostMs.Count > 20 || o.HandlerCostMs.Any(n => n < 0 || n > 60_000) ||
                !double.IsFinite(o.OfferedRate) || o.OfferedRate < 0 || o.OfferedRate > 1_000_000 ||
                o.Timeout < TimeSpan.FromSeconds(1) || o.Timeout > TimeSpan.FromHours(1) ||
                o.ReceiveBatchOverride is <= 0 or > 10_000 || string.IsNullOrWhiteSpace(o.OutputPath) ||
                !Regex.IsMatch(o.Database, "^conduit_(msgbench|phase3_queue)_[a-z0-9_]{1,32}$", RegexOptions.CultureInvariant))
                return null;
            return o;
        }
        catch (Exception error) when (error is FormatException or OverflowException or ArgumentException) { return null; }
    }
}

internal sealed record RunResult(string Queue, int HandlerCostMs, double OfferedRate, int ReceiveBatch,
    bool Complete, bool WarmupComplete, double PublishRate, double HandlerCompletionRate,
    double SettlementObservedRate, double ElapsedSeconds, double LatencyP50Ms, double LatencyP95Ms,
    double LatencyP99Ms, bool SaturationObserved, double[] DatabaseRttMs, DeliverySnapshot Warmup,
    DeliverySnapshot Measurement, string? Error, StoreSetup? Setup = null)
{
    public static bool AllComplete(IEnumerable<RunResult> results)
    {
        var all = results.ToArray();
        return all.Length > 0 && all.All(r => r.Complete && r.WarmupComplete && r.Error is null &&
            r.Setup is { Ready: true, Error: null } && r.Warmup.Complete && r.Measurement.Complete);
    }
    public string ToLine() => $"cost={HandlerCostMs}ms batch={ReceiveBatch} completed={Measurement.Completed}/{Measurement.Expected} " +
        $"settled={Measurement.Settled} settled/s={SettlementObservedRate:F1} complete={Complete} saturationObserved={SaturationObserved}";
}

internal sealed class TransportBenchmarkReport
{
    public int SchemaVersion { get; init; } = 2;
    public string Scope { get; init; } = "Synthetic handler and real PostgreSQL transport/inbox settlement; not real ledger, group balance or Redis financial qualification.";
    public string Runtime { get; init; } = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
    public string RuntimeVersion { get; init; } = Environment.Version.ToString();
    public string Architecture { get; init; } = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
    public string OsArchitecture { get; init; } = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
    public string RuntimeIdentifier { get; init; } = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
    public int ProcessId { get; init; } = Environment.ProcessId;
    public bool ServerGc { get; init; } = System.Runtime.GCSettings.IsServerGC;
    public string GcLatencyMode { get; init; } = System.Runtime.GCSettings.LatencyMode.ToString();
    public Dictionary<string, string> GcConfiguration { get; init; } = GC.GetConfigurationVariables()
        .ToDictionary(p => p.Key, p => Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? string.Empty);
    public Dictionary<string, string> GcConfigurationTypes { get; init; } = GC.GetConfigurationVariables()
        .ToDictionary(p => p.Key, p => p.Value.GetType().FullName ?? p.Value.GetType().Name);
    public string CoreLibrarySha256 { get; init; } = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
        File.ReadAllBytes(typeof(object).Assembly.Location)));
    public string Os { get; init; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
    public long StopwatchFrequency { get; init; } = Stopwatch.Frequency;
    public string? DatabaseHost { get; set; }
    public int DatabasePort { get; set; }
    public string? DatabaseLatencyLabel { get; set; }
    public bool Passed { get; set; }
    public string? Error { get; set; }
    public List<RunResult> Results { get; } = new();
    public string[] Limitations { get; init; } =
    [
        "Task.Delay is synthetic handler cost, not PostgreSQL latency or real SpendUpdateProcessor work.",
        "Per-message settlement timestamps are upper bounds observed by SQL polling; the100ms observer adds database work.",
        "Exact synthetic amount conservation is payload evidence only, not a real monetary ledger or reservation reconciliation.",
        "A complete run without sustained observed backlog does not establish saturated consumer capacity.",
        "No production policy, concurrency, partitioning, transport or durability setting is changed."
    ];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(TransportBenchmarkReport))]
internal partial class TransportBenchmarkJsonContext : JsonSerializerContext { }
