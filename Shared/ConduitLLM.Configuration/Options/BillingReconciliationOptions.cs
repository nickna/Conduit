using System.ComponentModel.DataAnnotations;

namespace ConduitLLM.Configuration.Options;

/// <summary>
/// Configuration for continuous request-log/ledger/provider-cost reconciliation.
/// </summary>
public sealed class BillingReconciliationOptions
{
    public const string SectionName = "BillingReconciliation";

    public bool Enabled { get; set; } = true;

    [Range(1, 24)]
    public int WindowHours { get; set; } = 1;

    [Range(0, 360)]
    public int GracePeriodMinutes { get; set; } = 15;

    [Range(0.0, 1.0)]
    public decimal RelativeThreshold { get; set; } = 0.01m;

    [Range(0.0, 1000000.0)]
    public decimal AbsoluteThresholdUsd { get; set; } = 0.01m;

    [Range(1, 168)]
    public int MaxCatchUpWindowsPerRun { get; set; } = 24;
}
