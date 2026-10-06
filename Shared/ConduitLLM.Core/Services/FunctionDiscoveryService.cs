using System.Text.Json;
using System.Text.Json.Nodes;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services;

/// <summary>
/// Service for discovering function configurations and converting them to LLM-compatible Tool definitions.
/// Supports caching via IFunctionDiscoveryCacheService for improved performance.
/// </summary>
public class FunctionDiscoveryService : IFunctionDiscoveryService
{
    private readonly IFunctionConfigurationRepository _functionConfigRepository;
    private readonly IFunctionClientFactory _clientFactory;
    private readonly IFunctionDiscoveryCacheService? _cacheService;
    private readonly ILogger<FunctionDiscoveryService> _logger;

    public FunctionDiscoveryService(
        IFunctionConfigurationRepository functionConfigRepository,
        IFunctionClientFactory clientFactory,
        IFunctionDiscoveryCacheService? cacheService,
        ILogger<FunctionDiscoveryService> logger)
    {
        _functionConfigRepository = functionConfigRepository ?? throw new ArgumentNullException(nameof(functionConfigRepository));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _cacheService = cacheService; // Nullable - caching is optional
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<List<Tool>> GetToolsForFunctionConfigurationsAsync(
        List<int> functionConfigurationIds,
        int virtualKeyId,
        CancellationToken cancellationToken = default)
    {
        if (functionConfigurationIds == null || functionConfigurationIds.Count == 0)
        {
            return new List<Tool>();
        }

        _logger.LogDebug("Loading {Count} function configurations for virtual key {VirtualKeyId}",
            functionConfigurationIds.Count, virtualKeyId);

        functionConfigurationIds = functionConfigurationIds.Distinct().Order().ToList();
        if (_cacheService is not null)
            return await _cacheService.GetOrLoadAsync(functionConfigurationIds,
                token => LoadToolsAsync(functionConfigurationIds, token), cancellationToken: cancellationToken);
        return (await LoadToolsAsync(functionConfigurationIds, cancellationToken)).Tools;
    }

    private async Task<FunctionDiscoveryLoad> LoadToolsAsync(List<int> functionConfigurationIds,
        CancellationToken cancellationToken)
    {
        // Load all configurations from database
        var configurations = await _functionConfigRepository.GetByIdsAsync(functionConfigurationIds, cancellationToken);

        // Validate all requested configurations were found
        if (configurations.Count != functionConfigurationIds.Count)
        {
            var missingIds = functionConfigurationIds.Except(configurations.Select(c => c.Id)).ToList();
            throw new ArgumentException($"Function configurations not found: {string.Join(", ", missingIds)}");
        }

        // Validate all are enabled
        var disabledConfigs = configurations.Where(c => !c.IsEnabled).ToList();
        if (disabledConfigs.Any())
        {
            var disabledIds = disabledConfigs.Select(c => c.Id).ToList();
            throw new ArgumentException($"Function configurations are disabled: {string.Join(", ", disabledIds)}");
        }

        // Convert to Tools. A fixed-schema configuration (Exa, Tavily) yields exactly one tool;
        // a dynamic provider (MCP) expands into one tool per server-advertised tool.
        var tools = new List<Tool>();
        foreach (var config in configurations)
        {
            try
            {
                if (IsDynamicProvider(config))
                {
                    var discovered = await DiscoverDynamicToolsAsync(config, cancellationToken);
                    foreach (var dynamicTool in discovered)
                    {
                        tools.Add(BuildDynamicTool(config, dynamicTool));
                    }
                }
                else
                {
                    tools.Add(ConvertConfigurationToTool(config));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Failed to convert function configuration {ConfigId} to Tool", config.Id);
                throw new InvalidOperationException($"Failed to convert function configuration {config.Id} to Tool", ex);
            }
        }

        var ttl = configurations.Where(config => config.CacheTtlMinutes.HasValue)
            .Select(config => config.CacheTtlMinutes).Min();
        return new FunctionDiscoveryLoad(tools, ttl);
    }

    public async Task<Dictionary<string, FunctionRoute>> GetFunctionNameToIdMappingAsync(
        List<int> functionConfigurationIds,
        CancellationToken cancellationToken = default)
    {
        if (functionConfigurationIds == null || functionConfigurationIds.Count == 0)
        {
            return new Dictionary<string, FunctionRoute>();
        }

        var configurations = await _functionConfigRepository.GetByIdsAsync(functionConfigurationIds, cancellationToken);

        var mapping = new Dictionary<string, FunctionRoute>();
        foreach (var config in configurations)
        {
            if (IsDynamicProvider(config))
            {
                // One route per server-advertised tool. The LLM-facing name is derived
                // deterministically (see BuildDynamicToolName) so it matches the tool emitted by
                // GetToolsForFunctionConfigurationsAsync, and the route carries the original tool
                // name for execution routing.
                var discovered = await DiscoverDynamicToolsAsync(config, cancellationToken);
                foreach (var dynamicTool in discovered)
                {
                    var name = BuildDynamicToolName(config, dynamicTool.Name);
                    mapping[name] = new FunctionRoute(config.Id, dynamicTool.Name);
                }
            }
            else
            {
                var functionName = GetFunctionNameForConfiguration(config);
                mapping[functionName] = new FunctionRoute(config.Id);
            }
        }

        return mapping;
    }

    /// <summary>
    /// Whether a configuration's provider discovers its tools dynamically at runtime (MCP) rather
    /// than exposing a single static schema. Gating on provider type keeps the fixed-schema
    /// providers on their original path (no client construction, no credential requirement during
    /// discovery); the client is still verified to implement <see cref="IDynamicToolProvider"/>.
    /// </summary>
    private static bool IsDynamicProvider(ConduitLLM.Functions.Entities.FunctionConfiguration config)
        => config.ProviderType == ConduitLLM.Functions.Enums.FunctionProviderType.Mcp;

    /// <summary>
    /// Enumerates a dynamic provider's tools by constructing its client and asking it to list them.
    /// </summary>
    private async Task<IReadOnlyList<ConduitLLM.Functions.Models.DiscoveredTool>> DiscoverDynamicToolsAsync(
        ConduitLLM.Functions.Entities.FunctionConfiguration config,
        CancellationToken cancellationToken)
    {
        var client = await _clientFactory.GetClientAsync(config.ProviderType, config.Id);
        if (client is not IDynamicToolProvider dynamicProvider)
        {
            _logger.LogWarning(
                "Configuration {ConfigId} ({Provider}) was treated as dynamic but its client does not support tool discovery; exposing no tools.",
                config.Id, config.ProviderType);
            return Array.Empty<ConduitLLM.Functions.Models.DiscoveredTool>();
        }

        return await dynamicProvider.ListToolsAsync(cancellationToken);
    }

    /// <summary>
    /// Builds an LLM Tool from a dynamically-discovered tool, namespacing the name under its
    /// configuration.
    /// </summary>
    private Tool BuildDynamicTool(
        ConduitLLM.Functions.Entities.FunctionConfiguration config,
        ConduitLLM.Functions.Models.DiscoveredTool dynamicTool)
    {
        return new Tool
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = BuildDynamicToolName(config, dynamicTool.Name),
                Description = string.IsNullOrWhiteSpace(dynamicTool.Description)
                    ? $"{GetProviderDisplayName(config.ProviderType)} tool '{dynamicTool.Name}'"
                    : dynamicTool.Description,
                Parameters = dynamicTool.ParametersSchema
            }
        };
    }

    /// <summary>
    /// Deterministically derives the LLM-facing function name for a dynamic tool:
    /// <c>{sanitized_config_name}__{sanitized_tool_name}</c>, constrained to the 64-char limit and
    /// required to start with a letter. Determinism lets the tool list and the routing map be built
    /// independently and still agree.
    /// </summary>
    private static string BuildDynamicToolName(
        ConduitLLM.Functions.Entities.FunctionConfiguration config,
        string toolName)
    {
        var prefix = SanitizeToSnakeCase(config.ConfigurationName);
        var suffix = SanitizeToSnakeCase(toolName);
        var combined = $"{prefix}__{suffix}";

        if (combined.Length == 0 || !char.IsLetter(combined[0]))
        {
            combined = "fn_" + combined;
        }

        if (combined.Length > 64)
        {
            combined = combined.Substring(0, 64);
        }

        return combined;
    }

    /// <summary>
    /// Converts a FunctionConfiguration entity to a Tool definition.
    /// </summary>
    private Tool ConvertConfigurationToTool(ConduitLLM.Functions.Entities.FunctionConfiguration config)
    {
        var functionName = GetFunctionNameForConfiguration(config);

        // Parse parameter schema if available
        JsonObject? parameters = null;
        if (!string.IsNullOrWhiteSpace(config.ParameterSchema))
        {
            try
            {
                parameters = JsonSerializer.Deserialize(
                    config.ParameterSchema,
                    AsyncTaskJsonContext.Default.JsonObject);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Failed to parse parameter schema for function configuration {ConfigId}. Using null parameters.", config.Id);
            }
        }

        return new Tool
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = functionName,
                Description = GetFunctionDescription(config),
                Parameters = parameters
            }
        };
    }

    /// <summary>
    /// Generates a function name for the LLM to call.
    /// Uses the configuration name, converted to snake_case for LLM compatibility.
    /// </summary>
    private string GetFunctionNameForConfiguration(ConduitLLM.Functions.Entities.FunctionConfiguration config)
    {
        // Convert configuration name to snake_case for LLM compatibility
        // Example: "Production Exa Search" -> "production_exa_search"
        var name = SanitizeToSnakeCase(config.ConfigurationName);

        // Ensure it starts with a letter (LLM requirement)
        if (name.Length == 0 || !char.IsLetter(name[0]))
        {
            name = "func_" + name;
        }

        // Truncate to 64 characters (LLM limit)
        if (name.Length > 64)
        {
            name = name.Substring(0, 64);
        }

        return name;
    }

    /// <summary>
    /// Lower-cases and reduces an arbitrary label to snake_case: spaces/hyphens become underscores
    /// and all other non-alphanumeric characters are dropped.
    /// </summary>
    private static string SanitizeToSnakeCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var lowered = value
            .ToLowerInvariant()
            .Replace(" ", "_")
            .Replace("-", "_");

        return new string(lowered.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
    }

    /// <summary>
    /// Generates a description for the function that helps the LLM understand when to use it.
    /// </summary>
    private string GetFunctionDescription(ConduitLLM.Functions.Entities.FunctionConfiguration config)
    {
        // Use provided description if available
        if (!string.IsNullOrWhiteSpace(config.Description))
        {
            return config.Description;
        }

        // Generate description based on provider type and purpose
        var providerName = GetProviderDisplayName(config.ProviderType);
        var purposeDescription = GetPurposeDescription(config.Purpose);

        return $"{providerName} - {purposeDescription}";
    }

    /// <summary>
    /// Gets a human-readable name for the provider type.
    /// </summary>
    private string GetProviderDisplayName(FunctionProviderType providerType)
    {
        return providerType switch
        {
            FunctionProviderType.Exa => "Exa.ai Search",
            FunctionProviderType.Tavily => "Tavily Search",
            FunctionProviderType.Mcp => "MCP Server",
            _ => providerType.ToString()
        };
    }

    /// <summary>
    /// Gets a description of what the function purpose means.
    /// </summary>
    private string GetPurposeDescription(FunctionPurpose purpose)
    {
        return purpose switch
        {
            FunctionPurpose.Search => "Search the web for information",
            FunctionPurpose.Answer => "Get direct answers to questions",
            FunctionPurpose.ContentRetrieval => "Retrieve and extract content from web pages",
            FunctionPurpose.RAG => "Search and retrieve information for RAG (Retrieval-Augmented Generation)",
            _ => purpose.ToString()
        };
    }
}
