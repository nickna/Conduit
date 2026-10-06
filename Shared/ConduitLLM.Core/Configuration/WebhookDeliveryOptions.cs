namespace ConduitLLM.Core.Configuration;

/// <summary>Receiver delivery policy. All durations are independent of worker lifetimes.</summary>
public sealed class WebhookDeliveryOptions
{
    public const string SectionName = "Webhooks:Delivery";
    public int AttemptTimeoutSeconds { get; set; } = 10;
    public int ConnectTimeoutSeconds { get; set; } = 5;

    public bool IsValid() => AttemptTimeoutSeconds is >= 1 and <= 300 &&
        ConnectTimeoutSeconds >= 1 && ConnectTimeoutSeconds <= AttemptTimeoutSeconds;
}
