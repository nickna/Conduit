using System.Diagnostics;
using System.Text.Json;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Gateway.Interfaces;
using ConduitLLM.Gateway.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Prometheus;

namespace ConduitLLM.Gateway.Services;

/// <summary>
/// Reconciles finalized request charges against usage ledger debits and provider cost evidence.
/// </summary>
public sealed class BillingReconciliationService : BackgroundService
{
    private const int CheckpointId = 1;
    private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;
    private readonly IOperationalAlertPublisher _alertPublisher;
    private readonly BillingReconciliationOptions _options;
    private readonly ILogger<BillingReconciliationService> _logger;

    private static readonly Counter Runs = Prometheus.Metrics.CreateCounter(
        "conduit_billing_reconciliation_runs_total", "Billing reconciliation runs.", "status");
    private static readonly Histogram Duration = Prometheus.Metrics.CreateHistogram(
        "conduit_billing_reconciliation_duration_seconds", "Billing reconciliation run duration.");
    private static readonly Gauge Groups = Prometheus.Metrics.CreateGauge(
        "conduit_billing_reconciliation_groups", "Groups in the latest reconciliation window.", "status");
    private static readonly Counter Mismatches = Prometheus.Metrics.CreateCounter(
        "conduit_billing_reconciliation_mismatches_total", "Billing reconciliation mismatches.", "comparison");
    private static readonly Gauge Discrepancy = Prometheus.Metrics.CreateGauge(
        "conduit_billing_reconciliation_discrepancy_dollars", "Absolute discrepancy in the latest window.", "comparison");
    private static readonly Gauge ProviderActualCost = Prometheus.Metrics.CreateGauge(
        "conduit_billing_reconciliation_provider_actual_cost_dollars", "Raw provider cost captured in the latest window.");
    private static readonly Gauge EvidenceSkipped = Prometheus.Metrics.CreateGauge(
        "conduit_billing_reconciliation_provider_evidence_skipped", "Provider-billed rows lacking complete reconciliation evidence.");

    public BillingReconciliationService(
        IDbContextFactory<ConduitDbContext> dbContextFactory,
        IOperationalAlertPublisher alertPublisher,
        IOptions<BillingReconciliationOptions> options,
        ILogger<BillingReconciliationService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _alertPublisher = alertPublisher;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Billing reconciliation is disabled");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await ProcessEligibleWindowsAsync(DateTime.UtcNow, stoppingToken);
                Runs.WithLabels("success").Inc();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Runs.WithLabels("failure").Inc();
                _logger.LogError(ex, "Billing reconciliation cycle failed; checkpoint was not advanced");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task ProcessEligibleWindowsAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var checkpoint = await context.BillingReconciliationCheckpoints
                .SingleOrDefaultAsync(x => x.Id == CheckpointId, cancellationToken);

            if (checkpoint == null)
            {
                var nextHour = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, DateTimeKind.Utc)
                    .AddHours(1);
                context.BillingReconciliationCheckpoints.Add(new BillingReconciliationCheckpoint
                {
                    Id = CheckpointId,
                    NextWindowStartUtc = nextHour,
                    UpdatedAtUtc = nowUtc
                });
                await context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Initialized billing reconciliation at the first full window {WindowStart:O}", nextHour);
                return;
            }

            var eligibleBefore = nowUtc.AddMinutes(-_options.GracePeriodMinutes);
            var processed = 0;
            while (processed < _options.MaxCatchUpWindowsPerRun)
            {
                var start = checkpoint.NextWindowStartUtc;
                var end = start.AddHours(_options.WindowHours);
                if (end > eligibleBefore) break;

                await ReconcileWindowAsync(context, start, end, cancellationToken);
                checkpoint.NextWindowStartUtc = end;
                checkpoint.UpdatedAtUtc = nowUtc;
                await context.SaveChangesAsync(cancellationToken);
                processed++;
            }
        }
        finally
        {
            stopwatch.Stop();
            Duration.Observe(stopwatch.Elapsed.TotalSeconds);
        }
    }

    internal async Task ReconcileWindowAsync(
        ConduitDbContext context,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        var requestRows = await context.RequestLogs
            .AsNoTracking()
            .Where(r => r.BilledAtUtc >= start && r.BilledAtUtc < end && r.Cost > 0)
            .GroupBy(r => r.VirtualKey!.VirtualKeyGroupId)
            .Select(g => new RequestAggregate(
                g.Key,
                g.Sum(x => x.Cost),
                g.Count(),
                g.Where(x => x.ProviderReportedCostUsd != null && x.ProviderCostMarkupMultiplier != null)
                    .Sum(x => x.ProviderReportedCostUsd!.Value),
                g.Where(x => x.ProviderReportedCostUsd != null && x.ProviderCostMarkupMultiplier != null)
                    .Sum(x => x.ProviderReportedCostUsd!.Value * x.ProviderCostMarkupMultiplier!.Value),
                g.Where(x => x.ProviderReportedCostUsd != null && x.ProviderCostMarkupMultiplier != null)
                    .Sum(x => x.Cost),
                g.Count(x => x.BillingMethod == RequestBillingMethod.ProviderReportedCost &&
                    (x.ProviderReportedCostUsd == null || x.ProviderCostMarkupMultiplier == null))))
            .ToListAsync(cancellationToken);

        var ledgerRows = await context.VirtualKeyGroupTransactions
            .AsNoTracking()
            .Where(t => t.BillingWindowStartUtc >= start && t.BillingWindowStartUtc < end &&
                t.TransactionType == TransactionType.Debit &&
                (t.ReferenceType == ReferenceType.VirtualKey || t.ReferenceType == ReferenceType.System))
            .GroupBy(t => t.VirtualKeyGroupId)
            .Select(g => new LedgerAggregate(g.Key, g.Sum(x => x.Amount), g.Count()))
            .ToListAsync(cancellationToken);

        var requests = requestRows.ToDictionary(x => x.GroupId);
        var ledger = ledgerRows.ToDictionary(x => x.GroupId);
        var groupIds = requests.Keys.Union(ledger.Keys).OrderBy(x => x).ToList();
        var mismatchCount = 0;
        decimal ledgerDiscrepancy = 0;
        decimal providerDiscrepancy = 0;
        decimal providerActual = 0;
        var skippedEvidence = 0;

        foreach (var groupId in groupIds)
        {
            requests.TryGetValue(groupId, out var request);
            ledger.TryGetValue(groupId, out var transaction);
            request ??= new RequestAggregate(groupId, 0, 0, 0, 0, 0, 0);
            transaction ??= new LedgerAggregate(groupId, 0, 0);

            var ledgerDifference = request.RequestCost - transaction.LedgerCost;
            var providerDifference = request.ProviderExpectedBilledCost - request.ProviderBilledCost;
            var ledgerMismatch = ExceedsThreshold(request.RequestCost, transaction.LedgerCost);
            var providerMismatch = request.ProviderExpectedBilledCost > 0 &&
                ExceedsThreshold(request.ProviderExpectedBilledCost, request.ProviderBilledCost);

            providerActual += request.ProviderActualCost;
            skippedEvidence += request.IncompleteProviderEvidenceCount;
            ledgerDiscrepancy += Math.Abs(ledgerDifference);
            providerDiscrepancy += Math.Abs(providerDifference);

            if (!ledgerMismatch && !providerMismatch) continue;

            mismatchCount++;
            if (ledgerMismatch) Mismatches.WithLabels("ledger").Inc();
            if (providerMismatch) Mismatches.WithLabels("provider").Inc();

            var comparisonTypes = new List<string>();
            if (ledgerMismatch) comparisonTypes.Add("ledger");
            if (providerMismatch) comparisonTypes.Add("provider");
            var requestId = $"billing-reconciliation:{groupId}:{start:yyyyMMddHH}";

            if (!await context.BillingAuditEvents.AnyAsync(e => e.RequestId == requestId, cancellationToken))
            {
                var potentialLoss = Math.Max(0m, ledgerDifference) + Math.Max(0m, providerDifference);
                context.BillingAuditEvents.Add(new BillingAuditEvent
                {
                    EventType = BillingAuditEventType.BillingReconciliationMismatch,
                    VirtualKeyGroupId = groupId,
                    RequestId = requestId,
                    Timestamp = DateTime.UtcNow,
                    CalculatedCost = potentialLoss,
                    FailureReason = $"Billing reconciliation mismatch ({string.Join(", ", comparisonTypes)})",
                    MetadataJson = JsonSerializer.Serialize(
                        new BillingReconciliationMetadata(
                            start,
                            end,
                            request.RequestCost,
                            transaction.LedgerCost,
                            ledgerDifference,
                            RelativeDifference(request.RequestCost, transaction.LedgerCost),
                            request.ProviderActualCost,
                            request.ProviderExpectedBilledCost,
                            request.ProviderBilledCost,
                            providerDifference,
                            RelativeDifference(request.ProviderExpectedBilledCost, request.ProviderBilledCost),
                            request.RequestCount,
                            transaction.TransactionCount,
                            request.IncompleteProviderEvidenceCount,
                            comparisonTypes.ToArray()),
                        GatewayInternalJsonContext.Default.BillingReconciliationMetadata)
                });
                await context.SaveChangesAsync(cancellationToken);
            }

            _alertPublisher.Raise(
                severity: OperationalAlertSeverity.Critical,
                component: "BillingReconciliation",
                title: $"Billing mismatch for virtual-key group {groupId}",
                message: $"Window {start:O}: request logs ${request.RequestCost:F6}, ledger ${transaction.LedgerCost:F6}.",
                context: new Dictionary<string, object>
                {
                    ["virtualKeyGroupId"] = groupId,
                    ["windowStartUtc"] = start,
                    ["windowEndUtc"] = end,
                    ["comparisonTypes"] = comparisonTypes,
                    ["auditEventId"] = requestId
                });
        }

        Groups.WithLabels("matched").Set(groupIds.Count - mismatchCount);
        Groups.WithLabels("mismatched").Set(mismatchCount);
        Discrepancy.WithLabels("ledger").Set((double)ledgerDiscrepancy);
        Discrepancy.WithLabels("provider").Set((double)providerDiscrepancy);
        ProviderActualCost.Set((double)providerActual);
        EvidenceSkipped.Set(skippedEvidence);

        _logger.LogInformation(
            "Billing reconciliation completed for {Start:O}-{End:O}: {Groups} groups, {Mismatches} mismatches",
            start, end, groupIds.Count, mismatchCount);
    }

    private bool ExceedsThreshold(decimal expected, decimal actual) =>
        Math.Abs(expected - actual) > _options.AbsoluteThresholdUsd &&
        RelativeDifference(expected, actual) > _options.RelativeThreshold;

    private static decimal RelativeDifference(decimal left, decimal right)
    {
        var basis = Math.Max(Math.Abs(left), Math.Abs(right));
        return basis == 0 ? 0 : Math.Abs(left - right) / basis;
    }

    private sealed record RequestAggregate(
        int GroupId,
        decimal RequestCost,
        int RequestCount,
        decimal ProviderActualCost,
        decimal ProviderExpectedBilledCost,
        decimal ProviderBilledCost,
        int IncompleteProviderEvidenceCount);

    private sealed record LedgerAggregate(int GroupId, decimal LedgerCost, int TransactionCount);
}
