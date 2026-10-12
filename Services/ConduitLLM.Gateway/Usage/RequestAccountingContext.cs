using ConduitLLM.Core.Models;

namespace ConduitLLM.Gateway.UsageTracking;

public enum RequestOperation
{
    Unknown,
    ChatCompletion,
    Responses,
    Embedding,
    Rerank,
    Function,
    Audio,
    Image,
    Video
}

public enum UsageEvidenceSource
{
    None,
    Provider,
    Estimated
}

public enum StreamTransportOutcome
{
    NotStarted,
    Emitting,
    Completed,
    ProviderFailed,
    ClientDisconnected,
    AccountingIndeterminate
}

public sealed record ProviderUsageEvidence(
    Usage Usage,
    string Model,
    UsageEvidenceSource Source);

/// <summary>Request-local pricing shared by response summaries and spend finalization.</summary>
public sealed record ProviderPricingEvidence(
    decimal? Cost,
    string? FallbackReason = null,
    string? FailureReason = null);

public sealed record ProviderToolPricingEvidence(
    decimal? Cost,
    string? UsageJson = null,
    string? FailureReason = null,
    string? UnconfiguredToolNames = null);

public sealed record StreamTransportEvidence(
    StreamTransportOutcome Outcome,
    long ProviderChunksObserved,
    long EventsWritten,
    long BytesWritten,
    DateTimeOffset? ProviderFirstChunkAt,
    DateTimeOffset? ClientFirstFlushAt,
    bool EvidenceTruncated);

public sealed record SpendReservationEvidence(
    decimal ReservedAmount,
    bool InvocationStarted,
    bool Closed);

public sealed record DirectCostEvidence(
    string OperationName,
    decimal ActualCost,
    string? ExecutionId,
    string? MetadataJson);

public sealed record RequestAccountingSnapshot(
    string BillingRequestId,
    RequestOperation Operation,
    int? VirtualKeyId,
    string? RequestedModel,
    ProviderUsageEvidence? ProviderUsage,
    IReadOnlyList<ProviderCallUsage> ProviderCalls,
    IReadOnlyList<ToolExecutionEvent> FunctionExecutions,
    decimal FunctionExecutionCost,
    IReadOnlyList<ToolCall> StreamingToolCalls,
    ProviderToolUsage? ProviderToolUsage,
    StreamTransportEvidence? Transport,
    DirectCostEvidence? DirectCost,
    string? MetadataJson,
    SpendReservationEvidence? Reservation,
    bool IsIndeterminate,
    string? IndeterminateReason,
    ProviderPricingEvidence? ProviderPricing = null,
    ProviderToolPricingEvidence? ProviderToolPricing = null);

public interface IRequestAccountingContext
{
    string BillingRequestId { get; }

    void SetOperation(RequestOperation operation, int? virtualKeyId, string? requestedModel);

    void RecordProviderUsage(Usage usage, string model, UsageEvidenceSource source);

    void RecordProviderCalls(IEnumerable<ProviderCallUsage> providerCalls);

    void RecordProviderPricing(ProviderPricingEvidence pricing);

    void RecordProviderToolPricing(ProviderToolPricingEvidence pricing);

    void RecordFunctionExecutions(
        IEnumerable<ToolExecutionEvent> executions,
        decimal totalCost);

    void RecordStreamingToolCalls(IEnumerable<ToolCall> toolCalls);

    void RecordProviderToolUsage(ProviderToolUsage toolUsage);

    void RecordTransport(StreamTransportEvidence transport);

    void RecordDirectCost(DirectCostEvidence directCost);

    void RecordMetadata(string metadataJson);

    void RecordReservation(decimal reservedAmount);

    void MarkInvocationStarted();

    void CloseReservation();

    void MarkIndeterminate(string reason);

    RequestAccountingSnapshot Snapshot();
}

public sealed class RequestAccountingContext : IRequestAccountingContext
{
    private readonly object _sync = new();
    private RequestOperation _operation;
    private int? _virtualKeyId;
    private string? _requestedModel;
    private ProviderUsageEvidence? _providerUsage;
    private List<ProviderCallUsage> _providerCalls = [];
    private ProviderPricingEvidence? _providerPricing;
    private ProviderToolPricingEvidence? _providerToolPricing;
    private List<ToolExecutionEvent> _functionExecutions = [];
    private decimal _functionExecutionCost;
    private List<ToolCall> _streamingToolCalls = [];
    private ProviderToolUsage? _providerToolUsage;
    private StreamTransportEvidence? _transport;
    private DirectCostEvidence? _directCost;
    private string? _metadataJson;
    private SpendReservationEvidence? _reservation;
    private bool _isIndeterminate;
    private string? _indeterminateReason;

    public RequestAccountingContext()
    {
        BillingRequestId = Guid.NewGuid().ToString("N");
    }

    public string BillingRequestId { get; }

    public void SetOperation(RequestOperation operation, int? virtualKeyId, string? requestedModel)
    {
        lock (_sync)
        {
            _operation = operation;
            _virtualKeyId = virtualKeyId;
            _requestedModel = requestedModel;
        }
    }

    public void RecordProviderUsage(Usage usage, string model, UsageEvidenceSource source)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        lock (_sync)
        {
            _providerUsage = new ProviderUsageEvidence(usage, model, source);
            _providerPricing = null;
        }
    }

    public void RecordProviderCalls(IEnumerable<ProviderCallUsage> providerCalls)
    {
        ArgumentNullException.ThrowIfNull(providerCalls);
        lock (_sync)
        {
            _providerCalls = providerCalls.ToList();
            _providerPricing = null;
        }
    }

    public void RecordProviderPricing(ProviderPricingEvidence pricing)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        lock (_sync)
        {
            _providerPricing = pricing;
        }
    }

    public void RecordProviderToolPricing(ProviderToolPricingEvidence pricing)
    {
        ArgumentNullException.ThrowIfNull(pricing);
        lock (_sync)
        {
            _providerToolPricing = pricing;
        }
    }

    public void RecordFunctionExecutions(
        IEnumerable<ToolExecutionEvent> executions,
        decimal totalCost)
    {
        ArgumentNullException.ThrowIfNull(executions);
        lock (_sync)
        {
            _functionExecutions = executions.ToList();
            _functionExecutionCost = totalCost;
        }
    }

    public void RecordStreamingToolCalls(IEnumerable<ToolCall> toolCalls)
    {
        ArgumentNullException.ThrowIfNull(toolCalls);
        lock (_sync)
        {
            _streamingToolCalls = toolCalls.ToList();
        }
    }

    public void RecordProviderToolUsage(ProviderToolUsage toolUsage)
    {
        ArgumentNullException.ThrowIfNull(toolUsage);
        lock (_sync)
        {
            _providerToolUsage = toolUsage;
            _providerToolPricing = null;
        }
    }

    public void RecordTransport(StreamTransportEvidence transport)
    {
        lock (_sync)
        {
            _transport = transport;
        }
    }

    public void RecordDirectCost(DirectCostEvidence directCost)
    {
        ArgumentNullException.ThrowIfNull(directCost);
        if (directCost.ActualCost < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(directCost), "Actual cost cannot be negative.");
        }

        lock (_sync)
        {
            _directCost = directCost;
        }
    }

    public void RecordMetadata(string metadataJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadataJson);
        lock (_sync)
        {
            _metadataJson = metadataJson;
        }
    }

    public void RecordReservation(decimal reservedAmount)
    {
        if (reservedAmount < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(reservedAmount));
        }

        lock (_sync)
        {
            _reservation = new SpendReservationEvidence(reservedAmount, false, false);
        }
    }

    public void MarkInvocationStarted()
    {
        lock (_sync)
        {
            if (_reservation is not null)
            {
                _reservation = _reservation with { InvocationStarted = true };
            }
        }
    }

    public void CloseReservation()
    {
        lock (_sync)
        {
            if (_reservation is not null)
            {
                _reservation = _reservation with { Closed = true };
            }
        }
    }

    public void MarkIndeterminate(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (_sync)
        {
            _isIndeterminate = true;
            _indeterminateReason ??= reason;
        }
    }

    public RequestAccountingSnapshot Snapshot()
    {
        lock (_sync)
        {
            return new RequestAccountingSnapshot(
                BillingRequestId,
                _operation,
                _virtualKeyId,
                _requestedModel,
                _providerUsage,
                _providerCalls.ToArray(),
                _functionExecutions.ToArray(),
                _functionExecutionCost,
                _streamingToolCalls.ToArray(),
                _providerToolUsage,
                _transport,
                _directCost,
                _metadataJson,
                _reservation,
                _isIndeterminate,
                _indeterminateReason,
                _providerPricing,
                _providerToolPricing);
        }
    }
}

public static class RequestAccountingContextExtensions
{
    private static readonly object Key = new();

    public static IRequestAccountingContext GetOrCreateRequestAccountingContext(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Items.TryGetValue(Key, out var existing) &&
            existing is IRequestAccountingContext accountingContext)
        {
            return accountingContext;
        }

        accountingContext = context.RequestServices?.GetService<IRequestAccountingContext>() ??
                            new RequestAccountingContext();
        context.Items[Key] = accountingContext;
        return accountingContext;
    }

    public static RequestAccountingSnapshot? GetRequestAccountingSnapshot(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(Key, out var existing) &&
               existing is IRequestAccountingContext accountingContext
            ? accountingContext.Snapshot()
            : null;
    }
}
