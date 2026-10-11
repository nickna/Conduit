using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Gateway.UsageTracking;

namespace ConduitLLM.Gateway.Billing;

public enum SpendReservationOutcome
{
    Reserved,
    InsufficientBalance,
    Unavailable
}

public sealed record SpendReservationResult(
    SpendReservationOutcome Outcome,
    decimal ReservedAmount,
    string? FailureReason = null);

public interface ISpendReservationService
{
    Task<SpendReservationResult> ReserveAsync(
        int virtualKeyId,
        string billingRequestId,
        decimal amount);

    Task<bool> MarkInvocationStartedAsync(int virtualKeyId, string billingRequestId);

    Task<SpendReservationSettlementResult> SettleAsync(
        int virtualKeyId,
        string billingRequestId,
        decimal actualAmount,
        DateTime? billedAtUtc = null);

    Task ReleaseAsync(int virtualKeyId, string billingRequestId);

    Task MarkIndeterminateAsync(
        int virtualKeyId,
        string billingRequestId,
        string reason);

    Task<SpendReservationSettlementResult> RecordKnownIndeterminateSpendAsync(
        int virtualKeyId, string billingRequestId, decimal knownAmount, string reason,
        DateTime? billedAtUtc = null);
}

public sealed class SpendReservationService : ISpendReservationService
{
    private readonly IBatchSpendUpdateService _batchSpendService;
    private readonly ILogger<SpendReservationService> _logger;

    public SpendReservationService(
        IBatchSpendUpdateService batchSpendService,
        ILogger<SpendReservationService> logger)
    {
        _batchSpendService = batchSpendService;
        _logger = logger;
    }

    public async Task<SpendReservationResult> ReserveAsync(
        int virtualKeyId,
        string billingRequestId,
        decimal amount)
    {
        try
        {
            var reserved = await _batchSpendService.TryReserveSpendAsync(
                virtualKeyId,
                amount,
                billingRequestId);
            return reserved
                ? new SpendReservationResult(SpendReservationOutcome.Reserved, amount)
                : new SpendReservationResult(
                    SpendReservationOutcome.InsufficientBalance,
                    amount,
                    "Insufficient available balance");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Billing reservation unavailable for request {BillingRequestId}", billingRequestId);
            return new SpendReservationResult(
                SpendReservationOutcome.Unavailable,
                amount,
                ex.Message);
        }
    }

    public Task<bool> MarkInvocationStartedAsync(int virtualKeyId, string billingRequestId) =>
        _batchSpendService.MarkSpendReservationInvocationStartedAsync(virtualKeyId, billingRequestId);

    public Task<SpendReservationSettlementResult> SettleAsync(
        int virtualKeyId,
        string billingRequestId,
        decimal actualAmount,
        DateTime? billedAtUtc = null) =>
        _batchSpendService.SettleSpendReservationAsync(
            virtualKeyId,
            billingRequestId,
            actualAmount,
            billedAtUtc);

    public Task ReleaseAsync(int virtualKeyId, string billingRequestId) =>
        _batchSpendService.ReleaseSpendReservationAsync(virtualKeyId, billingRequestId);

    public async Task MarkIndeterminateAsync(
        int virtualKeyId,
        string billingRequestId,
        string reason)
    {
        _logger.LogCritical(
            "Billing request {BillingRequestId} for Virtual Key {VirtualKeyId} is indeterminate; " +
            "the invocation-started reservation remains held. Reason: {Reason}",
            billingRequestId,
            virtualKeyId,
            reason);
        var result = await _batchSpendService.RecordIndeterminateSpendReservationAsync(
            virtualKeyId, billingRequestId, null, reason);
        if (result.Status is not (SpendReservationSettlementStatus.Settled or SpendReservationSettlementStatus.AlreadySettled))
            throw new InvalidOperationException($"Indeterminate reservation persistence returned {result.Status}");
    }

    public Task<SpendReservationSettlementResult> RecordKnownIndeterminateSpendAsync(
        int virtualKeyId, string billingRequestId, decimal knownAmount, string reason,
        DateTime? billedAtUtc = null) =>
        _batchSpendService.RecordIndeterminateSpendReservationAsync(
            virtualKeyId, billingRequestId, knownAmount, reason, billedAtUtc);
}
