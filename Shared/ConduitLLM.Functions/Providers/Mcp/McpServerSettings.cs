using System.Text.Json;
using System.Text.Json.Serialization;
using ConduitLLM.Functions.Serialization;

namespace ConduitLLM.Functions.Providers.Mcp;

/// <summary>
/// Strongly-typed view over the <c>FunctionConfiguration.ProviderSettings</c> JSON blob for an
/// MCP server configuration.
/// </summary>
/// <remarks>
/// Example ProviderSettings:
/// <code>
/// { "allowedTools": ["search", "fetch"],   // null/absent = expose every advertised tool
///   "authScheme": "Bearer",                  // used only for the Authorization header
///   "authHeader": "Authorization",           // custom header carries the raw token
///   "allowPrivateNetwork": false }           // opt-in to private/loopback server URLs
/// </code>
/// </remarks>
public sealed class McpServerSettings
{
    /// <summary>
    /// Allowlist of tool names to expose. Null means expose every advertised tool; an empty list exposes none.
    /// </summary>
    [JsonPropertyName("allowedTools")]
    public IReadOnlyList<string>? AllowedTools { get; init; }

    /// <summary>Authorization scheme prefix (default "Bearer"), used only for the Authorization header.</summary>
    [JsonPropertyName("authScheme")]
    public string? AuthScheme { get; init; }

    /// <summary>Header name carrying the credential (default "Authorization").</summary>
    [JsonPropertyName("authHeader")]
    public string? AuthHeader { get; init; }

    /// <summary>When true, allows the server URL to resolve to a private/loopback address.</summary>
    [JsonPropertyName("allowPrivateNetwork")]
    public bool AllowPrivateNetwork { get; init; }

    /// <summary>
    /// Parses the ProviderSettings JSON. Returns defaults for null/blank/invalid input so a
    /// misconfigured blob degrades to "expose all tools, Authorization: Bearer, no private network".
    /// </summary>
    public static McpServerSettings Parse(string? providerSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(providerSettingsJson))
        {
            return new McpServerSettings();
        }

        try
        {
            return JsonSerializer.Deserialize(
                       providerSettingsJson,
                       FunctionsJsonContext.Default.McpServerSettings)
                   ?? new McpServerSettings();
        }
        catch (JsonException)
        {
            return new McpServerSettings();
        }
    }

    /// <summary>
    /// Case-sensitive allowlist set. Null permits all tools, while an empty set permits none.
    /// </summary>
    public HashSet<string>? AllowedToolSet =>
        AllowedTools is null ? null : new HashSet<string>(AllowedTools, StringComparer.Ordinal);
}
