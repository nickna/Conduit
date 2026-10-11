using System.Text.Json.Serialization;

namespace ConduitLLM.Admin.DTOs;

/// <summary>Database probe details without an EF navigation graph.</summary>
public sealed record DatabaseHealthDetails(bool ConnectionPooling, string Provider);

/// <summary>Instance heartbeat summary; reason is present only when no heartbeat exists.</summary>
public sealed record HeartbeatHealthDetails(
    string Source,
    int HealthyInstances,
    int TotalInstances,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

/// <summary>Redis fallback details when no distributed cache is configured.</summary>
public sealed record RedisFallbackHealthDetails(bool Configured, string Mode, string Description);

/// <summary>Successful Redis probe details.</summary>
public sealed record RedisHealthDetails(bool Configured, double PingMilliseconds);

/// <summary>Failed Redis probe details.</summary>
public sealed record RedisErrorHealthDetails(bool Configured, string Error);

/// <summary>Explicit non-durable messaging mode.</summary>
public sealed record InMemoryMessagingHealthDetails(
    string Backend, string Transport, bool Durable, string Description);

/// <summary>Durable messaging checks, retaining a null error on success.</summary>
public sealed record DurableMessagingHealthDetails(
    string Backend, string Transport, bool Durable, MessagingHealthCheckDetails[] Checks, string? Error);

/// <summary>One messaging readiness check.</summary>
public sealed record MessagingHealthCheckDetails(string Name, string Status, string? Description);

/// <summary>Messaging probe failure details.</summary>
public sealed record MessagingErrorHealthDetails(string Backend, string Error);

/// <summary>Storage probe details, including nullable bucket and endpoint values.</summary>
public sealed record StorageHealthDetails(
    string Mode, string Description, bool Ephemeral, string? Bucket, string? Endpoint);

/// <summary>Storage probe failure details.</summary>
public sealed record StorageErrorHealthDetails(string Mode, string Error);
