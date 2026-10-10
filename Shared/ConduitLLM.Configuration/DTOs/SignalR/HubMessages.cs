namespace ConduitLLM.Configuration.DTOs.SignalR;

/// <summary>Describes a successful task subscription.</summary>
public sealed class TaskSubscriptionNotification
{
    /// <summary>Gets or sets the subscribed task ID.</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>Gets or sets the subscription result message.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>Describes an error sent by a SignalR hub.</summary>
public sealed class HubErrorNotification
{
    /// <summary>Gets or sets the client-safe error message.</summary>
    public string Message { get; set; } = string.Empty;
}
