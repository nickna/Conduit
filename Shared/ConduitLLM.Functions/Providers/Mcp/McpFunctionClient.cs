using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ConduitLLM.Functions.Serialization;
using System.Text.Json.Nodes;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ConduitLLM.Functions.Providers.Mcp;

/// <summary>
/// Function client that bridges a remote Model Context Protocol (MCP) server into the Conduit
/// function-execution engine.
/// </summary>
/// <remarks>
/// Unlike the fixed-schema providers, one MCP <see cref="FunctionConfiguration"/> exposes an
/// arbitrary, runtime-discovered set of tools. This client therefore also implements
/// <see cref="IDynamicToolProvider"/>: discovery enumerates the server's tools via <c>tools/list</c>,
/// and execution routes to a specific tool via <c>tools/call</c> (the target tool name is carried
/// in <see cref="McpReservedParameterKeys.ToolName"/>). Only remote Streamable HTTP / SSE transports
/// are supported — stdio is intentionally excluded for multi-tenant safety.
/// </remarks>
public sealed partial class McpFunctionClient : IFunctionClient, IDynamicToolProvider
{
    private const int MaxResponseChars = 100_000;
    private const string TruncationMarker = "…[truncated]";

    private readonly FunctionConfiguration _configuration;
    private readonly FunctionCredential _credential;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpFunctionClient> _logger;
    private readonly McpServerSettings _settings;
    private readonly string _serverUrl;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <inheritdoc />
    public FunctionProviderType ProviderType => FunctionProviderType.Mcp;

    /// <inheritdoc />
    public string ProviderName => "MCP";

    public McpFunctionClient(
        FunctionConfiguration configuration,
        FunctionCredential credential,
        IHttpClientFactory? httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _logger = loggerFactory.CreateLogger<McpFunctionClient>();
        _settings = McpServerSettings.Parse(configuration.ProviderSettings);
        _serverUrl = DetermineServerUrl();

        _jsonOptions = Utilities.FunctionsJsonOptions.CompactWire;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DiscoveredTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        await using var client = await ConnectAsync(apiKey: null, cancellationToken);
        var tools = await client.ListToolsAsync(options: null, cancellationToken: cancellationToken);

        var allow = _settings.AllowedToolSet;
        var discovered = new List<DiscoveredTool>(tools.Count);
        foreach (var tool in tools)
        {
            if (allow is not null && !allow.Contains(tool.Name))
            {
                continue;
            }

            discovered.Add(new DiscoveredTool
            {
                Name = tool.Name,
                Description = tool.Description,
                ParametersSchema = ConvertSchema(tool.JsonSchema)
            });
        }

        _logger.LogInformation(
            "Discovered {Count} MCP tool(s) from server {Server} for configuration {ConfigId}",
            discovered.Count, _serverUrl, _configuration.Id);

        return discovered;
    }

    /// <inheritdoc />
    public async Task<FunctionExecutionResult> ExecuteAsync(
        Dictionary<string, object> parameters,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        // The target MCP tool is threaded in by the execution layer via a reserved key
        // (one MCP configuration exposes many tools). Strip it from the model-supplied arguments.
        string? toolName = null;
        var arguments = parameters;
        if (parameters.TryGetValue(McpReservedParameterKeys.ToolName, out var toolNameValue))
        {
            toolName = toolNameValue as string ?? toolNameValue?.ToString();
            arguments = new Dictionary<string, object>(parameters);
            arguments.Remove(McpReservedParameterKeys.ToolName);
        }

        if (string.IsNullOrWhiteSpace(toolName))
        {
            throw new ArgumentException(
                "No MCP tool name was supplied for execution. This indicates a routing error.",
                nameof(parameters));
        }

        var allow = _settings.AllowedToolSet;
        if (allow is not null && !allow.Contains(toolName))
        {
            throw new ArgumentException(
                $"MCP tool '{toolName}' is not in the configured allowlist.",
                nameof(parameters));
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var client = await ConnectAsync(apiKey, cancellationToken);
            var callResult = await client.CallToolAsync(
                toolName,
                (IReadOnlyDictionary<string, object?>)arguments,
                cancellationToken: cancellationToken);
            stopwatch.Stop();

            var isError = callResult.IsError == true;
            return new FunctionExecutionResult
            {
                IsSuccess = !isError,
                ResponseJson = BuildResponseJson(callResult),
                ErrorMessage = isError ? "The MCP tool reported an error (see response content)." : null,
                Duration = stopwatch.Elapsed,
                Metadata = new Dictionary<string, object>
                {
                    ["mcpTool"] = toolName,
                    ["serverUrl"] = _serverUrl
                }
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "MCP tool call '{Tool}' failed on server {Server}", toolName, _serverUrl);
            return new FunctionExecutionResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                Duration = stopwatch.Elapsed
            };
        }
    }

    /// <inheritdoc />
    public async Task<FunctionAuthenticationResult> VerifyAuthenticationAsync(
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var client = await ConnectAsync(apiKey, cancellationToken);
            var tools = await client.ListToolsAsync(options: null, cancellationToken: cancellationToken);
            stopwatch.Stop();

            return FunctionAuthenticationResult.Success(
                $"Connected to MCP server; {tools.Count} tool(s) available.",
                stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "MCP authentication check failed for server {Server}", _serverUrl);
            return FunctionAuthenticationResult.Failure(
                $"Failed to connect to MCP server: {ex.Message}", ex.ToString());
        }
    }

    /// <summary>
    /// Opens a short-lived MCP session over Streamable HTTP / SSE. The session is disposed by the
    /// caller (<c>await using</c>). Pooling is a deferred optimization.
    /// </summary>
    private async Task<McpClient> ConnectAsync(string? apiKey, CancellationToken cancellationToken)
    {
        McpEndpointGuard.EnsureAllowed(_serverUrl, _settings.AllowPrivateNetwork);

        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(_serverUrl),
            Name = _configuration.ConfigurationName,
            TransportMode = HttpTransportMode.AutoDetect,
            ConnectionTimeout = TimeSpan.FromSeconds(_configuration.TimeoutSeconds ?? 30),
            AdditionalHeaders = BuildAuthHeaders(apiKey)
        };

        IClientTransport transport = _httpClientFactory is not null
            ? new HttpClientTransport(
                options,
                _httpClientFactory.CreateClient($"{ProviderName}FunctionClient"),
                _loggerFactory,
                ownsHttpClient: false)
            : new HttpClientTransport(options, _loggerFactory);

        return await McpClient.CreateAsync(transport, null, _loggerFactory, cancellationToken);
    }

    /// <summary>
    /// Builds the authentication headers from the (already-decrypted) token. Returns null when no
    /// token is configured (some MCP servers are open / rely on network controls).
    /// </summary>
    private Dictionary<string, string>? BuildAuthHeaders(string? apiKey)
    {
        var token = apiKey ?? _credential.ApiKey;
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var header = string.IsNullOrWhiteSpace(_settings.AuthHeader) ? "Authorization" : _settings.AuthHeader!;
        string value;
        if (header.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
        {
            var scheme = string.IsNullOrWhiteSpace(_settings.AuthScheme) ? "Bearer" : _settings.AuthScheme!;
            value = $"{scheme} {token}".Trim();
        }
        else
        {
            // Custom header carries the raw token (e.g. "X-Api-Key").
            value = token;
        }

        return new Dictionary<string, string> { [header] = value };
    }

    private string DetermineServerUrl()
    {
        // Priority: credential BaseUrl > configuration BaseUrl.
        if (!string.IsNullOrWhiteSpace(_credential.BaseUrl))
        {
            return _credential.BaseUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_configuration.BaseUrl))
        {
            return _configuration.BaseUrl.Trim();
        }

        throw new InvalidOperationException(
            $"MCP function configuration '{_configuration.ConfigurationName}' has no server URL (BaseUrl).");
    }

    private static JsonObject? ConvertSchema(JsonElement schema)
    {
        if (schema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        return JsonNode.Parse(schema.GetRawText()) as JsonObject;
    }

    /// <summary>
    /// Serializes a tool result to JSON for the model. Text and structured content are captured;
    /// binary blocks (image/audio/embedded resources) are summarized rather than inlined. The
    /// payload is capped at <see cref="MaxResponseChars"/> to bound context growth.
    /// </summary>
    private string BuildResponseJson(CallToolResult result)
    {
        var textParts = new List<string>();
        var nonTextBlocks = 0;

        if (result.Content is not null)
        {
            foreach (var block in result.Content)
            {
                if (block is TextContentBlock text && text.Text is not null)
                {
                    textParts.Add(text.Text);
                }
                else
                {
                    nonTextBlocks++;
                }
            }
        }

        var json = SerializeResponsePayload(
            result.IsError ?? false,
            textParts,
            nonTextBlocks,
            result.StructuredContent,
            truncated: false,
            omittedStructuredContent: false);
        if (json.Length <= MaxResponseChars)
        {
            return json;
        }

        return BuildTruncatedResponseJson(
            result.IsError ?? false,
            textParts,
            nonTextBlocks,
            result.StructuredContent);
    }

    private string BuildTruncatedResponseJson(
        bool isError,
        IReadOnlyList<string> textParts,
        int nonTextBlocks,
        object? structuredContent)
    {
        var omittedStructuredContent = false;
        var boundedStructuredContent = structuredContent;
        var best = SerializeResponsePayload(
            isError,
            TruncateTextParts(textParts, 0),
            nonTextBlocks,
            boundedStructuredContent,
            truncated: true,
            omittedStructuredContent);

        if (best.Length > MaxResponseChars)
        {
            boundedStructuredContent = null;
            omittedStructuredContent = structuredContent is not null;
            best = SerializeResponsePayload(
                isError,
                TruncateTextParts(textParts, 0),
                nonTextBlocks,
                boundedStructuredContent,
                truncated: true,
                omittedStructuredContent);
        }

        var totalTextChars = textParts.Aggregate(
            0L,
            (total, text) => Math.Min(MaxResponseChars, total + text.Length));
        var low = 0;
        var high = (int)totalTextChars;

        while (low <= high)
        {
            var budget = low + ((high - low) / 2);
            var candidate = SerializeResponsePayload(
                isError,
                TruncateTextParts(textParts, budget),
                nonTextBlocks,
                boundedStructuredContent,
                truncated: true,
                omittedStructuredContent);

            if (candidate.Length <= MaxResponseChars)
            {
                best = candidate;
                low = budget + 1;
            }
            else
            {
                high = budget - 1;
            }
        }

        return best;
    }

    private string SerializeResponsePayload(
        bool isError,
        IReadOnlyList<string> textParts,
        int nonTextBlocks,
        object? structuredContent,
        bool truncated,
        bool omittedStructuredContent)
    {
        var payload = new Dictionary<string, object?>
        {
            ["isError"] = isError,
            ["content"] = textParts
        };

        if (nonTextBlocks > 0)
        {
            payload["omittedNonTextBlocks"] = nonTextBlocks;
        }

        if (structuredContent is not null)
        {
            payload["structuredContent"] = structuredContent;
        }

        if (truncated)
        {
            payload["truncated"] = true;
        }

        if (omittedStructuredContent)
        {
            payload["omittedStructuredContent"] = true;
        }

        return JsonSerializer.Serialize(
            payload,
            FunctionsJsonContext.Default.DictionaryStringObject);
    }

    private static IReadOnlyList<string> TruncateTextParts(
        IReadOnlyList<string> textParts,
        int maxTextChars)
    {
        if (textParts.Count == 0)
        {
            return [];
        }

        var bounded = new StringBuilder(Math.Min(maxTextChars, MaxResponseChars));
        var remaining = maxTextChars;
        var omittedText = false;

        foreach (var text in textParts)
        {
            if (text.Length <= remaining)
            {
                bounded.Append(text);
                remaining -= text.Length;
                continue;
            }

            var take = remaining;
            if (take > 0 && take < text.Length && char.IsHighSurrogate(text[take - 1]))
            {
                take--;
            }

            if (take > 0)
            {
                bounded.Append(text.AsSpan(0, take));
            }

            omittedText = true;
            break;
        }

        if (omittedText)
        {
            bounded.Append(TruncationMarker);
        }

        return bounded.Length == 0 ? [] : [bounded.ToString()];
    }
}
