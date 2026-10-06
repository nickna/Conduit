namespace ConduitLLM.Core.Configuration;

/// <summary>Receiver delivery policy. All durations are independent of worker lifetimes.</summary>
public sealed class WebhookDeliveryOptions
{
    public const string SectionName = "Webhooks:Delivery";
    public int AttemptTimeoutSeconds { get; set; } = 10;
    public int ConnectTimeoutSeconds { get; set; } = 5;
    public int MaxAttempts { get; set; } = 100;
    public int TerminalWindowSeconds { get; set; } = 24 * 60 * 60;
    public int ProgressWindowSeconds { get; set; } = 5 * 60;
    public int InitialDelaySeconds { get; set; } = 2;
    public int MaxDelaySeconds { get; set; } = 60 * 60;
    public int MaxRetryAfterSeconds { get; set; } = 60 * 60;
    public int DeferralSeconds { get; set; } = 30;
    public double JitterRatio { get; set; } = 0.2;
    public int GlobalConcurrency { get; set; } = 32;
    public int DestinationConcurrency { get; set; } = 4;
    public int RecoveryProbes { get; set; } = 1;
    public int CircuitFailureThreshold { get; set; } = 5;
    public int CircuitOpenSeconds { get; set; } = 60;

    public bool IsValid() => AttemptTimeoutSeconds is >= 1 and <= 300 &&
        ConnectTimeoutSeconds >= 1 && ConnectTimeoutSeconds <= AttemptTimeoutSeconds &&
        MaxAttempts is >= 1 and <= 10000 && TerminalWindowSeconds is >= 1 and <= 604800 &&
        ProgressWindowSeconds >= 1 && ProgressWindowSeconds <= TerminalWindowSeconds &&
        InitialDelaySeconds >= 1 && MaxDelaySeconds >= InitialDelaySeconds && MaxDelaySeconds <= 86400 &&
        MaxRetryAfterSeconds is >= 1 and <= 86400 && DeferralSeconds is >= 1 and <= 3600 &&
        double.IsFinite(JitterRatio) && JitterRatio is >= 0 and <= 1 &&
        GlobalConcurrency is >= 1 and <= 10000 && DestinationConcurrency >= 1 &&
        DestinationConcurrency <= GlobalConcurrency && RecoveryProbes >= 1 && RecoveryProbes <= DestinationConcurrency &&
        CircuitFailureThreshold is >= 1 and <= 1000 && CircuitOpenSeconds is >= 1 and <= 3600;
}
