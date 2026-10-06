using System.Text.Json;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Models;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Functions.Utilities;
using ConduitLLM.Functions.Serialization;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Database-backed implementation of the model capability service.
    /// Retrieves model capabilities from the ModelProviderMapping table.
    /// Uses hybrid caching (L1: Memory, L2: Redis) for optimal performance and consistency.
    /// </summary>
    public class DatabaseModelCapabilityService : IModelCapabilityService
    {
        private readonly ILogger<DatabaseModelCapabilityService> _logger;
        private readonly IModelProviderMappingRepository _repository;
        private readonly HybridCacheAccessor _cache;

        public DatabaseModelCapabilityService(
            ILogger<DatabaseModelCapabilityService> logger,
            IModelProviderMappingRepository repository,
            IMemoryCache memoryCache,
            IDistributedCache? distributedCache = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _cache = new HybridCacheAccessor(
                memoryCache,
                distributedCache,
                logger,
                "DatabaseModelCapability:",
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(30));
        }

        /// <inheritdoc/>
        public async Task<bool> SupportsVisionAsync(string model)
        {
            var cacheKey = $"Vision:{model}";
            
            // Try hybrid cache first
            var cachedResult = await _cache.GetAsync(
                cacheKey,
                FunctionsJsonContext.Default.NullableBoolean);
            if (cachedResult.HasValue)
            {
                return cachedResult.Value;
            }

            try
            {
                var mapping = await GetMappingByModelNameAsync(model);
                var association = mapping?.ModelProviderTypeAssociation;
                var result = association?.Model is not null &&
                    ModelCapabilityResolver.Resolve(association.Model, association).SupportsImageInput;
                await _cache.SetAsync(cacheKey, result, FunctionsJsonContext.Default.Boolean);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking vision capability for model {Model}", model);
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> SupportsVideoInputAsync(string model)
        {
            var cacheKey = $"VideoInput:{model}";

            var cachedResult = await _cache.GetAsync(
                cacheKey,
                FunctionsJsonContext.Default.NullableBoolean);
            if (cachedResult.HasValue)
            {
                return cachedResult.Value;
            }

            try
            {
                var mapping = await GetMappingByModelNameAsync(model);
                var association = mapping?.ModelProviderTypeAssociation;
                var result = association?.Model is not null &&
                    ModelCapabilityResolver.Resolve(association.Model, association).SupportsVideoInput;
                await _cache.SetAsync(cacheKey, result, FunctionsJsonContext.Default.Boolean);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking video input capability for model {Model}", model);
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> SupportsVideoGenerationAsync(string model)
        {
            var cacheKey = $"VideoGeneration:{model}";
            
            // Try hybrid cache first
            var cachedResult = await _cache.GetAsync(
                cacheKey,
                FunctionsJsonContext.Default.NullableBoolean);
            if (cachedResult.HasValue)
            {
                return cachedResult.Value;
            }

            try
            {
                var mapping = await GetMappingByModelNameAsync(model);
                var association = mapping?.ModelProviderTypeAssociation;
                var result = association?.Model is not null &&
                    ModelCapabilityResolver.Resolve(association.Model, association).SupportsVideoGeneration;
                await _cache.SetAsync(cacheKey, result, FunctionsJsonContext.Default.Boolean);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking video generation capability for model {Model}", model);
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> SupportsSpeechToTextAsync(string model)
        {
            var cacheKey = $"SpeechToText:{model}";

            var cachedResult = await _cache.GetAsync(
                cacheKey,
                FunctionsJsonContext.Default.NullableBoolean);
            if (cachedResult.HasValue)
            {
                return cachedResult.Value;
            }

            try
            {
                var mapping = await GetMappingByModelNameAsync(model);
                var association = mapping?.ModelProviderTypeAssociation;
                var result = association?.Model is not null &&
                    ModelCapabilityResolver.Resolve(association.Model, association).SupportsSpeechToText;
                await _cache.SetAsync(cacheKey, result, FunctionsJsonContext.Default.Boolean);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking speech-to-text capability for model {Model}", model);
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> SupportsTextToSpeechAsync(string model)
        {
            var cacheKey = $"TextToSpeech:{model}";

            var cachedResult = await _cache.GetAsync(
                cacheKey,
                FunctionsJsonContext.Default.NullableBoolean);
            if (cachedResult.HasValue)
            {
                return cachedResult.Value;
            }

            try
            {
                var mapping = await GetMappingByModelNameAsync(model);
                var association = mapping?.ModelProviderTypeAssociation;
                var result = association?.Model is not null &&
                    ModelCapabilityResolver.Resolve(association.Model, association).SupportsTextToSpeech;
                await _cache.SetAsync(cacheKey, result, FunctionsJsonContext.Default.Boolean);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking text-to-speech capability for model {Model}", model);
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task<bool> SupportsRerankAsync(string model)
        {
            var cacheKey = $"Rerank:{model}";

            var cachedResult = await _cache.GetAsync(
                cacheKey,
                FunctionsJsonContext.Default.NullableBoolean);
            if (cachedResult.HasValue)
            {
                return cachedResult.Value;
            }

            try
            {
                var mapping = await GetMappingByModelNameAsync(model);
                var association = mapping?.ModelProviderTypeAssociation;
                var result = association?.Model is not null &&
                    ModelCapabilityResolver.Resolve(association.Model, association).SupportsRerank;
                await _cache.SetAsync(cacheKey, result, FunctionsJsonContext.Default.Boolean);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking rerank capability for model {Model}", model);
                return false;
            }
        }

        /// <inheritdoc/>
        public async Task<string?> GetTokenizerTypeAsync(string model)
        {
            var cacheKey = $"Tokenizer:{model}";
            
            // Try hybrid cache first
            var cachedResult = await _cache.GetAsync(
                cacheKey,
                FunctionsJsonContext.Default.String);
            if (cachedResult != null)
            {
                return cachedResult;
            }

            try
            {
                var mapping = await GetMappingByModelNameAsync(model);
                var tokenizerType = mapping?.ModelProviderTypeAssociation?.Model?.TokenizerType;

                // Unknown model: report null rather than fabricating Cl100KBase here.
                // TokenizerEncodingMap.Resolve(null) applies the one documented default
                // (approximate cl100k_base), so the count is correctly flagged as an
                // approximation instead of claiming an exact vocabulary (#1232).
                if (tokenizerType is null)
                {
                    return null;
                }

                string result = tokenizerType.ToString()!;
                await _cache.SetAsync(cacheKey, result, FunctionsJsonContext.Default.String);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tokenizer type for model {Model}", model);
                return null; // Resolve(null) falls back to the approximate default encoding.
            }
        }


        /// <inheritdoc/>
        public async Task<string?> GetDefaultModelAsync(string provider, string capabilityType)
        {
            var cacheKey = $"Default:{provider}:{capabilityType}";
            
            // Try hybrid cache first
            var cachedResult = await _cache.GetAsync(
                cacheKey,
                FunctionsJsonContext.Default.String);
            if (cachedResult != null)
            {
                return cachedResult;
            }

            try
            {
                // Default model selection is now deprecated - return null
                // This functionality should be replaced with priority-based routing
                _logger.LogWarning("GetDefaultModelAsync is deprecated. Use priority-based routing instead.");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting default model for provider {Provider} and capability {Capability}",
                    provider, capabilityType);
                return null;
            }
        }

        /// <inheritdoc/>
        public async Task RefreshCacheAsync()
        {
            await _cache.ClearAsync();

            _logger.LogInformation("Model capability cache refresh completed");
        }

        /// <summary>
        /// Helper method to get a mapping by model name, checking both alias and provider model name.
        /// </summary>
        private async Task<ModelProviderMapping?> GetMappingByModelNameAsync(string model, CancellationToken cancellationToken = default)
        {
            var mapping = await _repository.GetByModelNameAsync(model, cancellationToken);
            if (mapping == null)
            {
                // Try to find by provider model name
                var allMappings = await RepositoryPaginationExtensions.GetAllViaPaginationAsync(
                    _repository.GetPaginatedAsync, cancellationToken: cancellationToken);
                mapping = allMappings.FirstOrDefault(m =>
                    m.ProviderModelId.Equals(model, StringComparison.OrdinalIgnoreCase));
            }
            return mapping;
        }
    }
}
