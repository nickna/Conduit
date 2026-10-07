using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Billing;
using ConduitLLM.Gateway.UsageTracking;

namespace ConduitLLM.Gateway.Middleware;

public partial class UsageTrackingMiddleware
{
    private async Task<bool> RecordSpendOrSettleReservationAsync(
        HttpContext context,
        int virtualKeyId,
        decimal actualCost,
        IBatchSpendUpdateService batchSpendService,
        IVirtualKeyRuntimeService virtualKeyService)
    {
        var accountingContext = context.GetOrCreateRequestAccountingContext();
        var snapshot = accountingContext.Snapshot();
        if (snapshot.Reservation is null)
        {
            await SpendUpdateHelper.UpdateSpendAsync(
                virtualKeyId,
                actualCost,
                batchSpendService,
                virtualKeyService,
                _logger,
                GetBillingTimestamp(context));
            return true;
        }

        var reservationService = context.RequestServices.GetService<ISpendReservationService>();
        if (reservationService is null)
        {
            const string reason = "Spend reservation service was unavailable during settlement";
            accountingContext.MarkIndeterminate(reason);
            _logger.LogCritical("{Reason} for billing request {BillingRequestId}", reason, snapshot.BillingRequestId);
            return false;
        }

        if (snapshot.IsIndeterminate)
        {
            await reservationService.MarkIndeterminateAsync(
                virtualKeyId,
                snapshot.BillingRequestId,
                snapshot.IndeterminateReason ?? "Usage evidence is indeterminate");
            return false;
        }

        try
        {
            var result = await reservationService.SettleAsync(
                virtualKeyId,
                snapshot.BillingRequestId,
                actualCost,
                GetBillingTimestamp(context));
            if (result.Status is SpendReservationSettlementStatus.Settled or
                SpendReservationSettlementStatus.AlreadySettled or
                SpendReservationSettlementStatus.SettledOverEstimate)
            {
                accountingContext.CloseReservation();
                if (result.Status == SpendReservationSettlementStatus.SettledOverEstimate)
                {
                    _logger.LogCritical(
                        "Billing request {BillingRequestId} settled over its reservation: actual {ActualCost:C}",
                        snapshot.BillingRequestId,
                        actualCost);
                }

                return true;
            }

            var reason = $"Reservation settlement returned {result.Status}";
            accountingContext.MarkIndeterminate(reason);
            await reservationService.MarkIndeterminateAsync(
                virtualKeyId,
                snapshot.BillingRequestId,
                reason);
            return false;
        }
        catch (Exception ex)
        {
            const string reason = "Reservation settlement threw before durable acceptance";
            accountingContext.MarkIndeterminate(reason);
            _logger.LogCritical(ex, "{Reason} for billing request {BillingRequestId}", reason, snapshot.BillingRequestId);
            await reservationService.MarkIndeterminateAsync(
                virtualKeyId,
                snapshot.BillingRequestId,
                reason);
            return false;
        }
    }

    private async Task FinalizeOpenReservationAsync(HttpContext context)
    {
        var accountingContext = context.GetOrCreateRequestAccountingContext();
        var snapshot = accountingContext.Snapshot();
        if (snapshot.Reservation is null || snapshot.Reservation.Closed)
        {
            return;
        }

        if (snapshot.VirtualKeyId is null)
        {
            const string missingVirtualKeyReason = "Open spend reservation had no Virtual Key identifier";
            accountingContext.MarkIndeterminate(missingVirtualKeyReason);
            _logger.LogCritical(
                "{Reason} for billing request {BillingRequestId}",
                missingVirtualKeyReason,
                snapshot.BillingRequestId);
            return;
        }

        var reservationService = context.RequestServices.GetService<ISpendReservationService>();
        if (reservationService is null)
        {
            _logger.LogCritical(
                "Billing request {BillingRequestId} ended with an open reservation and no reservation service",
                snapshot.BillingRequestId);
            return;
        }

        if (!snapshot.Reservation.InvocationStarted)
        {
            await reservationService.ReleaseAsync(
                snapshot.VirtualKeyId.Value,
                snapshot.BillingRequestId);
            accountingContext.CloseReservation();
            return;
        }

        var reason = snapshot.IndeterminateReason ??
                     "Provider invocation started but the request ended without durable settlement";
        accountingContext.MarkIndeterminate(reason);
        await reservationService.MarkIndeterminateAsync(
            snapshot.VirtualKeyId.Value,
            snapshot.BillingRequestId,
            reason);
    }
}
