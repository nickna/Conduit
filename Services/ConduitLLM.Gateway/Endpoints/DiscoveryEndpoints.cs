using System.Text.Json;
using ConduitLLM.Gateway.Serialization;
using ConduitLLM.Configuration;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Configuration.DTOs;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using ConduitLLM.Gateway.DTOs;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Functions.Utilities;
using ConduitLLM.Functions.DTOs;
using GatewayDiscoveredModelDto = ConduitLLM.Configuration.DTOs.DiscoveredModelDto;

namespace ConduitLLM.Gateway.Endpoints
{
    /// <summary>
    /// Controller for discovering model capabilities and provider features.
    /// Provides runtime discovery for virtual key holders to understand available models and their capabilities.
    /// </summary>
    public class DiscoveryEndpoints : GatewayEndpointHandlerBase
    {
        private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;
        private readonly IModelCapabilityService _modelCapabilityService;
        private readonly IVirtualKeyService _virtualKeyService;
        private readonly IDiscoveryCacheService _discoveryCacheService;
        private readonly JsonSerializerOptions _wireJsonOptions;
        private readonly DiscoveryCacheOptions _discoveryOptions;

        /// <summary>
        /// Initializes the Discovery endpoint handler.
        /// </summary>
        public DiscoveryEndpoints(
            IDbContextFactory<ConduitDbContext> dbContextFactory,
            IModelCapabilityService modelCapabilityService,
            IVirtualKeyService virtualKeyService,
            IDiscoveryCacheService discoveryCacheService,
            JsonSerializerOptions wireJsonOptions,
            IOptions<DiscoveryCacheOptions> discoveryOptions,
            IHttpContextAccessor httpContextAccessor,
            ILogger<DiscoveryEndpoints> logger)
            : base(null, httpContextAccessor, logger)
        {
            _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
            _modelCapabilityService = modelCapabilityService ?? throw new ArgumentNullException(nameof(modelCapabilityService));
            _virtualKeyService = virtualKeyService ?? throw new ArgumentNullException(nameof(virtualKeyService));
            _discoveryCacheService = discoveryCacheService ?? throw new ArgumentNullException(nameof(discoveryCacheService));
            _wireJsonOptions = wireJsonOptions ?? throw new ArgumentNullException(nameof(wireJsonOptions));
            _discoveryOptions = (discoveryOptions ?? throw new ArgumentNullException(nameof(discoveryOptions))).Value;
        }

        /// <summary>
        /// Discovery is authentication-protected but not balance-protected. A valid key may
        /// inspect capabilities even when its group has no remaining balance.
        /// </summary>
        private async Task<IResult?> ValidateDiscoveryAccessAsync()
        {
            var virtualKeyValue = HttpContext.User.FindFirst("VirtualKey")?.Value;
            if (string.IsNullOrEmpty(virtualKeyValue))
            {
                return OpenAIError(
                    StatusCodes.Status401Unauthorized,
                    "Virtual key not found",
                    VirtualKeyValidationFailureCodes.MissingKey,
                    "authentication_error");
            }

            var validation = await _virtualKeyService.ValidateVirtualKeyForAuthenticationAsync(virtualKeyValue);
            if (validation.IsValid && validation.Key is not null)
            {
                return null;
            }

            return OpenAIError(
                validation.HttpStatusCode,
                validation.Reason ?? "Virtual key validation failed.",
                validation.FailureCode ?? VirtualKeyValidationFailureCodes.ValidationError,
                GatewayResults.OpenAIErrorTypeFor(validation.HttpStatusCode));
        }

        /// <summary>
        /// Gets all discovered models and their capabilities for authenticated virtual keys.
        /// </summary>
        /// <param name="capability">Optional capability filter (e.g., "video_generation", "vision")</param>
        /// <returns>List of models with their capabilities.</returns>
        public async Task<IResult> GetModels(string? capability = null)
        {
            if (await ValidateDiscoveryAccessAsync() is { } accessFailure)
            {
                return accessFailure;
            }

            // Build cache key based on capability filter; pricing-bearing payloads are
            // cached under a distinct key so toggling ExposePricing never serves the
            // wrong shape from a stale entry.
            var exposePricing = _discoveryOptions.ExposePricing;
            var cacheKey = DiscoveryCacheService.BuildCacheKey(capability, includePricing: exposePricing);

            var result = await _discoveryCacheService.GetOrLoadAsync(cacheKey, token =>
                DiscoveryCacheLoader.LoadAsync(_dbContextFactory, capability, exposePricing, _wireJsonOptions, Logger, token),
                HttpContext.RequestAborted);
            var models = result.Data.Select(element => element.Deserialize(
                    GatewayJsonTypeInfo.Require<GatewayDiscoveredModelDto>(_wireJsonOptions)))
                .Where(model => model is not null).Cast<GatewayDiscoveredModelDto>().ToList();
            return Ok(new DiscoveryModelsResponse(models, models.Count));
        }

        /// <summary>
        /// Gets all available capabilities in the system.
        /// </summary>
        /// <returns>List of all available capabilities.</returns>
        public async Task<IResult> GetCapabilities()
        {
            await Task.CompletedTask;

            // Return all known capabilities
            var capabilities = new[]
            {
                "chat",
                "chat_stream",
                "vision",
                "video_generation",
                "image_generation",
                "embeddings",
                "speech_to_text",
                "text_to_speech",
                "rerank",
                "function_calling",
                "tool_use"
                // "json_mode" is intentionally not advertised: per-model JSON-mode
                // support is not tracked, so filtering by it could never match
            };

            return Ok(new DiscoveryCapabilitiesResponse(capabilities));
        }

        /// <summary>
        /// Gets UI parameters for a specific model to enable dynamic UI generation.
        /// </summary>
        /// <param name="model">The model alias or identifier to get parameters for</param>
        /// <returns>JSON object containing UI parameter definitions for the model.</returns>
        public async Task<IResult> GetModelParameters(string model)
        {
            if (await ValidateDiscoveryAccessAsync() is { } accessFailure)
            {
                return accessFailure;
            }

            using var context = await _dbContextFactory.CreateDbContextAsync();

            // Find the model mapping by alias
            var modelMapping = await context.ModelProviderMappings
                .Include(m => m.ModelProviderTypeAssociation)
                    .ThenInclude(mpta => mpta.Model)
                        .ThenInclude(m => m!.Series)
                .AsNoTracking()
                .Where(m => m.ModelAlias == model && m.IsEnabled)
                .FirstOrDefaultAsync();

            if (modelMapping == null)
            {
                // Try to find by Model.Id if the input is numeric
                if (int.TryParse(model, out var modelId))
                {
                    modelMapping = await context.ModelProviderMappings
                        .Include(m => m.ModelProviderTypeAssociation)
                            .ThenInclude(mpta => mpta.Model)
                                .ThenInclude(m => m!.Series)
                        .AsNoTracking()
                        .Where(m => m.ModelProviderTypeAssociation != null && m.ModelProviderTypeAssociation.ModelId == modelId && m.IsEnabled)
                        .FirstOrDefaultAsync();
                }
            }

            if (modelMapping?.ModelProviderTypeAssociation?.Model == null)
            {
                return OpenAIError(404, $"Model '{model}' not found or has no parameter information", "model_not_found");
            }

            // Parse the Parameters JSON - check model-specific parameters first, then fall back to series
            JsonElement? parameters = null;
            var parametersJson = modelMapping.ModelProviderTypeAssociation.Model.ModelParameters
                ?? modelMapping.ModelProviderTypeAssociation.Model.Series?.Parameters;

            if (!string.IsNullOrEmpty(parametersJson))
            {
                try
                {
                    parameters = JsonDocument.Parse(parametersJson).RootElement.Clone();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to parse parameters for model {Model}", LoggingSanitizer.S(model));
                    parameters = EmptyJsonObject();
                }
            }

            return Ok(new ModelParametersResponse(
                modelMapping.ModelProviderTypeAssociation.ModelId,
                modelMapping.ModelAlias,
                modelMapping.ModelProviderTypeAssociation.Model.Series?.Name ?? string.Empty,
                parameters ?? EmptyJsonObject()));
        }

        /// <summary>
        /// Gets all available function configurations for authenticated virtual keys.
        /// </summary>
        /// <param name="purpose">Optional purpose filter (e.g., "Search", "Answer", "RAG_Search")</param>
        /// <param name="providerType">Optional provider type filter (e.g., "Exa", "Perplexity")</param>
        /// <returns>List of available function configurations</returns>
        public async Task<IResult> GetFunctions(
            string? purpose = null,
            string? providerType = null)
        {
            if (await ValidateDiscoveryAccessAsync() is { } accessFailure)
            {
                return accessFailure;
            }

            // "v4" entries use the canonical configuration shape with structured parameter schemas.
            var cacheKey = $"functions_discovery_v4_{purpose ?? "all"}_{providerType ?? "all"}";

            // Try to get from cache first
            var cachedResult = await _discoveryCacheService.GetDiscoveryResultsAsync(cacheKey);
            if (cachedResult is { Data.Count: > 0 })
            {
                Logger.LogDebug("Returning cached function discovery results");
                return Ok(cachedResult.Data[0]);
            }

            using var context = await _dbContextFactory.CreateDbContextAsync();

            // Get all enabled function configurations
            var query = context.FunctionConfigurations
                .Where(fc => fc.IsEnabled);

            // Apply filters
            if (!string.IsNullOrEmpty(purpose))
            {
                if (Enum.TryParse<ConduitLLM.Functions.Enums.FunctionPurpose>(purpose, true, out var purposeEnum))
                {
                    query = query.Where(fc => fc.Purpose == purposeEnum);
                }
            }

            if (!string.IsNullOrEmpty(providerType))
            {
                if (Enum.TryParse<ConduitLLM.Functions.Enums.FunctionProviderType>(providerType, true, out var providerEnum))
                {
                    query = query.Where(fc => fc.ProviderType == providerEnum);
                }
            }

            var configurations = await query.AsNoTracking().ToListAsync();

            var result = new ConduitLLM.Functions.DTOs.FunctionDiscoveryResponse
            {
                Functions = configurations.Select(fc => new ConduitLLM.Functions.DTOs.FunctionDiscoveryDto
                {
                    Id = fc.Id,
                    ConfigurationName = fc.ConfigurationName,
                    ProviderType = fc.ProviderType.ToString(),
                    Purpose = fc.Purpose.ToString(),
                    Description = fc.Description,
                    DefaultExecutionMode = fc.DefaultExecutionMode.ToString(),
                    IsEnabled = fc.IsEnabled,
                    TimeoutSeconds = fc.TimeoutSeconds,
                    ParameterSchema = StructuredJson.ParseObject(fc.ParameterSchema) ?? new()
                }).ToList(),
                Count = configurations.Count
            };

            // Cache the results
            var discoveryResult = new DiscoveryModelsResult
            {
                Data = new List<JsonElement>
                {
                    JsonSerializer.SerializeToElement(
                        result,
                        GatewayJsonTypeInfo.Require<FunctionDiscoveryResponse>(_wireJsonOptions))
                },
                Count = result.Count,
                CapabilityFilter = purpose
            };

            await _discoveryCacheService.SetDiscoveryResultsAsync(cacheKey, discoveryResult);

            Logger.LogInformation("Cached function discovery results with {Count} functions", result.Count);

            return Ok(result);
        }

        /// <summary>
        /// Gets parameter schema for a specific function configuration.
        /// Enables dynamic UI generation for function execution.
        /// </summary>
        /// <param name="functionConfigurationId">The function configuration ID</param>
        /// <returns>JSON schema defining required and optional parameters</returns>
        public async Task<IResult> GetFunctionParameters(int functionConfigurationId)
        {
            if (await ValidateDiscoveryAccessAsync() is { } accessFailure)
            {
                return accessFailure;
            }

            // "v4" entries use function_id and structured parameter/example objects.
            var cacheKey = $"function_parameters_v4_{functionConfigurationId}";

            // Try to get from cache first
            var cachedResult = await _discoveryCacheService.GetDiscoveryResultsAsync(cacheKey);
            if (cachedResult is { Data.Count: > 0 })
            {
                Logger.LogDebug("Returning cached function parameter schema for config {ConfigId}", functionConfigurationId);
                return Ok(cachedResult.Data[0]);
            }

            using var context = await _dbContextFactory.CreateDbContextAsync();

            // Find the function configuration
            var configuration = await context.FunctionConfigurations
                .AsNoTracking()
                .Where(fc => fc.Id == functionConfigurationId && fc.IsEnabled)
                .FirstOrDefaultAsync();

            if (configuration == null)
            {
                return OpenAIError(404, $"Function configuration {functionConfigurationId} not found or is disabled", "not_found");
            }

            // Parse the parameter schema
            Dictionary<string, JsonElement> parameterSchema = new();
            Dictionary<string, JsonElement>? exampleRequest = null;

            if (!string.IsNullOrEmpty(configuration.ParameterSchema))
            {
                parameterSchema = StructuredJson.ParseObject(configuration.ParameterSchema) ?? new();
                if (parameterSchema.TryGetValue("example", out var exampleElement))
                {
                    exampleRequest = StructuredJson.ParseObject(exampleElement.GetRawText());
                }
            }

            var result = new ConduitLLM.Functions.DTOs.FunctionParametersResponseDto
            {
                FunctionId = configuration.Id,
                ConfigurationName = configuration.ConfigurationName,
                ProviderType = configuration.ProviderType.ToString(),
                Purpose = configuration.Purpose.ToString(),
                ParameterSchema = parameterSchema,
                ExampleRequest = exampleRequest
            };

            // Cache the results
            var discoveryResult = new DiscoveryModelsResult
            {
                Data = new List<JsonElement>
                {
                    JsonSerializer.SerializeToElement(
                        result,
                        GatewayJsonTypeInfo.Require<FunctionParametersResponseDto>(_wireJsonOptions))
                },
                Count = 1
            };

            await _discoveryCacheService.SetDiscoveryResultsAsync(cacheKey, discoveryResult);

            Logger.LogInformation("Cached function parameter schema for config {ConfigId}", functionConfigurationId);

            return Ok(result);
        }

        private static JsonElement EmptyJsonObject()
        {
            using var document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }
    }
}
