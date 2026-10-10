using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Security;
using ConduitLLM.Core.Decorators;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Providers.Configuration;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers
{
    /// <summary>
    /// Database-aware implementation of ILLMClientFactory that uses provider credentials from the database.
    /// </summary>
    /// <remarks>
    /// This factory creates LLM client instances using credentials dynamically loaded from the database.
    /// It supports all configured providers and applies decorators like performance tracking when enabled.
    /// 
    /// Use this factory when:
    /// - Credentials are stored in the database
    /// - Multiple providers of the same type are configured
    /// - Dynamic credential management is required
    /// </remarks>
    public class DatabaseAwareLLMClientFactory : ILLMClientFactory
    {
        private readonly IProviderService _credentialService;
        private readonly IModelProviderMappingService _mappingService;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<DatabaseAwareLLMClientFactory> _logger;
        private readonly IPerformanceMetricsService? _performanceMetricsService;
        private readonly IServiceProvider _serviceProvider;
        private readonly IDbContextFactory<ConduitDbContext>? _dbContextFactory;
        private readonly IDistributedCache? _distributedCache;
        private readonly IModelProviderMappingRuntimeStore? _runtimeMappingStore;

        /// <summary>
        /// Initializes a new instance of the <see cref="DatabaseAwareLLMClientFactory"/> class.
        /// </summary>
        public DatabaseAwareLLMClientFactory(
            IProviderService credentialService,
            IModelProviderMappingService mappingService,
            ILoggerFactory loggerFactory,
            IHttpClientFactory httpClientFactory,
            ILogger<DatabaseAwareLLMClientFactory> logger,
            IServiceProvider serviceProvider,
            IPerformanceMetricsService? performanceMetricsService = null,
            IDbContextFactory<ConduitDbContext>? dbContextFactory = null,
            IDistributedCache? distributedCache = null,
            IModelProviderMappingRuntimeStore? runtimeMappingStore = null)
        {
            _credentialService = credentialService ?? throw new ArgumentNullException(nameof(credentialService));
            _mappingService = mappingService ?? throw new ArgumentNullException(nameof(mappingService));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _performanceMetricsService = performanceMetricsService;
            _dbContextFactory = dbContextFactory;
            _distributedCache = distributedCache;
            _runtimeMappingStore = runtimeMappingStore;
        }

        public async Task<ILLMClient> GetClientForChatAsync(
            ConduitLLM.Core.Models.ChatCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            var mappings = await _mappingService.GetMappingsByModelAliasAsync(request.Model);
            if (mappings.Count == 0)
                throw new ModelNotFoundException(request.Model, ModelUnavailableMessage(request.Model));

            // Administratively disabled routes make the alias unavailable to the client — that is a 404,
            // not a retryable 503. Only route *health* exhaustion (open circuit below, or no usable
            // credential further down) is 503. Collapsing both into one 503 was part of #1191.
            var configured = mappings.Where(mapping => mapping.IsEnabled
                && mapping.Provider?.IsEnabled == true
                && mapping.ModelProviderTypeAssociation?.IsEnabled == true).ToList();
            if (configured.Count == 0)
                throw new ModelNotFoundException(request.Model, ModelUnavailableMessage(request.Model));

            var settings = _serviceProvider.GetService<IGlobalSettingsCacheService>();
            var switchValue = settings is null ? null : await settings.GetSettingValueAsync("Routing.Chat.Enabled");
            var routingEnabled = !bool.TryParse(switchValue, out var enabled) || enabled;
            var policy = await GetRoutePolicyAsync(request.Model, cancellationToken);
            if (routingEnabled && _logger.IsEnabled(LogLevel.Debug))
            {
                foreach (var mapping in configured.Where(mapping => !RouteCircuitRegistry.IsAvailable(mapping.Id)))
                {
                    _logger.LogDebug(
                        "Excluding provider mapping {MappingId} for model {ModelAlias} because its route circuit is open or a recovery probe is already in progress",
                        mapping.Id,
                        request.Model);
                }
            }
            // Both branches route over `configured` so that turning routing off never selects a
            // mapping an operator has disabled.
            var scored = routingEnabled ? BalancedRouteScorer.Score(configured, policy) :
                configured.OrderBy(mapping => mapping.Id).Take(1).Select(mapping => new ScoredRoute(mapping, 0.5m)).ToArray();
            if (scored.Count == 0)
                throw new ServiceUnavailableException($"No healthy provider route for model '{request.Model}'.", "Routing");

            var ordered = scored.ToList();
            if (routingEnabled && policy.CacheAffinityEnabled && _distributedCache is not null &&
                !string.IsNullOrWhiteSpace(request.RoutingAffinityKey))
            {
                var cachedId = await _distributedCache.GetStringAsync(
                    RoutedChatClient.AffinityCacheKey(request.Model, request.RoutingAffinityKey), cancellationToken);
                if (int.TryParse(cachedId, out var affinityId))
                {
                    var affinity = ordered.FirstOrDefault(route => route.Mapping.Id == affinityId);
                    if (affinity is not null && ordered[0].Score - affinity.Score <= policy.MaxAffinityScorePenalty)
                    {
                        ordered.Remove(affinity); ordered.Insert(0, affinity);
                        request.RoutingAffinityUsed = true;
                        request.RoutingDecisionReason = "affinity_reuse";
                    }
                }
            }
            request.RoutingDecisionReason ??= routingEnabled ? "balanced_score" : "routing_disabled";

            var routes = new List<(ModelProviderMapping, ILLMClient)>();
            foreach (var route in ordered)
            {
                var provider = await _credentialService.GetProviderByIdAsync(route.Mapping.ProviderId);
                if (provider is null || !provider.IsEnabled) continue;
                var credentials = await ValidateProviderAndGetCredentialsAsync(provider);
                routes.Add((route.Mapping, CreateClientForProvider(provider, credentials,
                    route.Mapping.ProviderModelId, route.Mapping.ProviderOptions)));
            }
            if (routes.Count == 0)
                throw new ServiceUnavailableException($"No configured provider credential for model '{request.Model}'.", "Routing");
            request.SelectedMappingId = routes[0].Item1.Id;
            return new RoutedChatClient(routes, request, _distributedCache, policy, _logger);
        }

        /// <summary>
        /// Client-facing message for an alias that has no route: either it does not exist or every
        /// route for it is administratively disabled. Deliberately does not distinguish the two, and
        /// deliberately avoids hinting at server configuration.
        /// </summary>
        private static string ModelUnavailableMessage(string modelAlias) =>
            $"The model '{modelAlias}' does not exist or is not available.";

        private async Task<ModelRoutePolicy> GetRoutePolicyAsync(string alias, CancellationToken cancellationToken)
        {
            if (_runtimeMappingStore is not null)
            {
                var persisted = await _runtimeMappingStore.GetRoutePolicyAsync(alias, cancellationToken);
                if (persisted is not null && persisted.IsEnabled)
                {
                    return new ModelRoutePolicy
                    {
                        Id = persisted.Id,
                        ModelAlias = persisted.ModelAlias,
                        Strategy = persisted.Strategy,
                        CostWeight = persisted.CostWeight,
                        SpeedWeight = persisted.SpeedWeight,
                        QualityWeight = persisted.QualityWeight,
                        CacheAffinityEnabled = persisted.CacheAffinityEnabled,
                        AffinityTtlSeconds = persisted.AffinityTtlSeconds,
                        MaxAffinityScorePenalty = persisted.MaxAffinityScorePenalty,
                        IsEnabled = persisted.IsEnabled,
                        CreatedAt = persisted.CreatedAt,
                        UpdatedAt = persisted.UpdatedAt
                    };
                }
            }
            else if (_dbContextFactory is not null)
            {
                await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
                var persisted = await context.ModelRoutePolicies.AsNoTracking()
                    .SingleOrDefaultAsync(policy => policy.ModelAlias == alias, cancellationToken);
                if (persisted is not null && persisted.IsEnabled) return persisted;
            }
            var result = new ModelRoutePolicy { ModelAlias = alias };
            var settings = _serviceProvider.GetService<IGlobalSettingsCacheService>();
            var json = settings is null ? null : await settings.GetSettingValueAsync("Routing.Defaults");
            if (string.IsNullOrWhiteSpace(json)) return result;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.TryGetProperty("costWeight", out var cost)) result.CostWeight = cost.GetDecimal();
                if (root.TryGetProperty("speedWeight", out var speed)) result.SpeedWeight = speed.GetDecimal();
                if (root.TryGetProperty("qualityWeight", out var quality)) result.QualityWeight = quality.GetDecimal();
                if (root.TryGetProperty("cacheAffinityEnabled", out var affinity)) result.CacheAffinityEnabled = affinity.GetBoolean();
                if (root.TryGetProperty("affinityTtlSeconds", out var ttl)) result.AffinityTtlSeconds = ttl.GetInt32();
                if (root.TryGetProperty("maxAffinityScorePenalty", out var penalty)) result.MaxAffinityScorePenalty = penalty.GetDecimal();
            }
            catch (JsonException exception)
            {
                _logger.LogError(exception, "Invalid Routing.Defaults configuration; using built-in defaults");
            }
            return result;
        }

        /// <inheritdoc />
        public async Task<ILLMClient> GetClientAsync(string modelName, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("DatabaseAwareLLMClientFactory.GetClientAsync called for model: {ModelName}", modelName);

            // Get model mapping from database
            var mapping = await _mappingService.GetMappingByModelAliasAsync(modelName);

            if (mapping == null)
            {
                _logger.LogWarning("No model mapping found in database for alias: {ModelAlias}", modelName);
                throw new ModelNotFoundException(modelName, ModelUnavailableMessage(modelName));
            }

            _logger.LogDebug("Found mapping in database: {ModelAlias} -> ProviderId:{ProviderId}/{ProviderModelId}",
                mapping.ModelAlias, mapping.ProviderId, mapping.ProviderModelId);

            // Get the provider from database
            var provider = await _credentialService.GetProviderByIdAsync(mapping.ProviderId);

            if (provider == null)
            {
                _logger.LogWarning("Provider {ProviderId} not found", mapping.ProviderId);
                throw new ServiceUnavailableException($"Provider for model '{modelName}' is not available.", "Provider");
            }

            var credentials = await ValidateProviderAndGetCredentialsAsync(provider);
            return CreateClientForProvider(provider, credentials, mapping.ProviderModelId, mapping.ProviderOptions);
        }

        /// <inheritdoc />
        public Task<ILLMClient> GetClientByProviderIdAsync(int providerId, CancellationToken cancellationToken = default)
            => GetClientByProviderIdAsync(providerId, "default-model-id", cancellationToken);

        /// <inheritdoc />
        public async Task<ILLMClient> GetClientByProviderIdAsync(int providerId, string providerModelId, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Getting client for provider ID {ProviderId} and model {ProviderModelId} using database credentials",
                providerId, providerModelId);

            var provider = await _credentialService.GetProviderByIdAsync(providerId);

            if (provider == null)
            {
                _logger.LogWarning("No provider found for provider ID {ProviderId} in database", providerId);
                throw new InvalidRequestException($"Provider with ID '{providerId}' not found.", "provider_not_found", "providerId");
            }

            var credentials = await ValidateProviderAndGetCredentialsAsync(provider);
            return CreateClientForProvider(provider, credentials, providerModelId);
        }

        /// <inheritdoc />
        public async Task<ILLMClient> GetClientByProviderTypeAsync(ProviderType providerType, CancellationToken cancellationToken = default)
        {
            _logger.LogDebug("Getting client for provider type {ProviderType} using database credentials", providerType);

            var allProviders = await _credentialService.GetAllProvidersAsync();
            var provider = allProviders.FirstOrDefault(p => p.ProviderType == providerType);

            if (provider == null)
            {
                _logger.LogWarning("No provider found for provider type {ProviderType} in database", providerType);
                throw new InvalidRequestException($"No provider configured for type '{providerType}'.", "provider_type_not_found", "providerType");
            }

            var credentials = await ValidateProviderAndGetCredentialsAsync(provider);
            return CreateClientForProvider(provider, credentials, "default-model-id");
        }

        /// <inheritdoc />
        public ILLMClient CreateTestClient(Provider provider, ProviderKeyCredential keyCredential)
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            if (keyCredential == null)
            {
                throw new ArgumentNullException(nameof(keyCredential));
            }

            var configuration = ProviderConfigurationRegistry.GetConfiguration(provider.ProviderType);
            if (configuration?.AuthenticationStrategy.RequiresApiKey != false
                && string.IsNullOrWhiteSpace(keyCredential.ApiKey))
            {
                throw new ArgumentException("API key is required for testing credentials", nameof(keyCredential));
            }

            var missingSecrets = ProviderConfigurationRegistry.GetMissingRequiredSecrets(
                provider.ProviderType,
                keyCredential.SecretSettings);
            if (missingSecrets.Count > 0)
            {
                throw new ArgumentException(
                    $"{provider.ProviderType} requires: {string.Join(", ", missingSecrets)}.",
                    nameof(keyCredential));
            }

            _logger.LogDebug("Creating test client for provider type: {ProviderType}", provider.ProviderType);

            // Use a minimal model ID for testing - providers should accept this for auth verification
            const string testModelId = "test-model";

            return CreateSingleClientForProvider(provider, keyCredential, testModelId);
        }

        /// <summary>
        /// Returns a copy of the credential whose API key and secret settings hold plaintext,
        /// decrypting stored values at the single seam before provider clients consume them.
        /// </summary>
        /// <remarks>
        /// A copy, not an in-place mutation: the credential can come from a cached or tracked entity,
        /// and writing plaintext back into it would leak decrypted secrets into that shared instance
        /// and risk persisting them. A credential with no stored secrets is returned untouched.
        /// </remarks>
        private ProviderKeyCredential RevealSecrets(ProviderKeyCredential keyCredential)
        {
            if (keyCredential.ApiKey == null && keyCredential.SecretSettings is not { Count: > 0 })
            {
                return keyCredential;
            }

            var protector = _serviceProvider.GetService<IProviderSecretProtector>();
            if (protector == null)
            {
                _logger.LogWarning(
                    "No provider secret protector is registered; credentials for provider {ProviderId} cannot be decrypted.",
                    keyCredential.ProviderId);
                return keyCredential;
            }

            return new ProviderKeyCredential
            {
                Id = keyCredential.Id,
                ProviderId = keyCredential.ProviderId,
                ApiKey = protector.Reveal(keyCredential.ApiKey),
                KeyName = keyCredential.KeyName,
                BaseUrl = keyCredential.BaseUrl,
                SecretSettings = protector.RevealAll(keyCredential.SecretSettings),
                IsPrimary = keyCredential.IsPrimary,
                IsEnabled = keyCredential.IsEnabled,
                ProviderAccountGroup = keyCredential.ProviderAccountGroup,
                CreatedAt = keyCredential.CreatedAt,
                UpdatedAt = keyCredential.UpdatedAt
            };
        }

        /// <summary>
        /// Validates that a provider is enabled, then retrieves its primary key credential.
        /// </summary>
        private async Task<IReadOnlyList<ProviderKeyCredential>> ValidateProviderAndGetCredentialsAsync(Provider provider)
        {
            if (!provider.IsEnabled)
            {
                _logger.LogWarning("Provider {ProviderId} is disabled", provider.Id);
                throw new ServiceUnavailableException(
                    $"Provider '{provider.ProviderName}' is currently disabled.", provider.ProviderName);
            }

            return await GetEnabledKeyCredentialsAsync(provider);
        }

        private async Task<IReadOnlyList<ProviderKeyCredential>> GetEnabledKeyCredentialsAsync(Provider provider)
        {
            var keyCredentials = await _credentialService.GetKeyCredentialsByProviderIdAsync(provider.Id);

            var enabledKeys = keyCredentials
                .Where(key => key.IsEnabled)
                .OrderByDescending(key => key.IsPrimary)
                .ThenBy(key => key.Id)
                .ToArray();

            if (enabledKeys.Length == 0)
            {
                _logger.LogWarning("No enabled API key found for provider {ProviderId}", provider.Id);
                throw new ConfigurationException($"No API key configured for provider '{provider.ProviderName}'.");
            }

            return enabledKeys;
        }

        private ILLMClient CreateClientForProvider(
            Provider provider,
            IReadOnlyList<ProviderKeyCredential> keyCredentials,
            string modelId,
            string? providerOptionsJson = null)
        {
            var primaryClient = CreateSingleClientForProvider(
                provider,
                keyCredentials[0],
                modelId,
                providerOptionsJson);

            if (keyCredentials.Count == 1)
            {
                return primaryClient;
            }

            var targets = keyCredentials
                .Select((credential, index) => new ProviderKeyFailoverTarget(
                    credential.Id,
                    credential.ProviderAccountGroup,
                    index == 0
                        ? () => primaryClient
                        : () => CreateSingleClientForProvider(
                            provider,
                            credential,
                            modelId,
                            providerOptionsJson)))
                .ToArray();

            return new ProviderKeyFailoverLLMClient(targets, _logger);
        }

        private ILLMClient CreateSingleClientForProvider(
            Provider provider,
            ProviderKeyCredential keyCredential,
            string modelId,
            string? providerOptionsJson = null)
        {
            var providerName = provider.ProviderType.ToString().ToLowerInvariant();

            _logger.LogDebug("Creating client for provider type: {ProviderType}, model: {ModelId}",
                provider.ProviderType, modelId);

            keyCredential = RevealSecrets(keyCredential);

            // Create the client creation context with all dependencies
            var context = new ClientCreationContext
            {
                LoggerFactory = _loggerFactory,
                HttpClientFactory = _httpClientFactory,
                ProviderOptionsJson = providerOptionsJson
            };

            // Create the base client using the registry
            ILLMClient client;
            try
            {
                client = ClientCreatorRegistry.CreateClient(
                    provider.ProviderType,
                    provider,
                    keyCredential,
                    modelId,
                    context);
            }
            catch (ArgumentException ex)
            {
                throw new ConfigurationException($"Unsupported provider type: {provider.ProviderType}", ex);
            }

            // Apply prompt caching decorator (before context/perf so it modifies request early)
            var settingsService = _serviceProvider.GetService<IGlobalSettingsCacheService>();
            if (settingsService != null)
            {
                var cachingLogger = _loggerFactory.CreateLogger<PromptCachingLLMClient>();
                client = new PromptCachingLLMClient(
                    client, settingsService, cachingLogger, provider.ProviderType.ToString(), modelId);
            }

            // Apply context decorator to set provider key context for error tracking
            _logger.LogDebug("Applying context decorator for KeyId: {KeyId}, ProviderId: {ProviderId}",
                keyCredential.Id, provider.Id);
            client = new ContextAwareLLMClient(
                client, keyCredential.Id, provider.Id, _serviceProvider,
                string.IsNullOrWhiteSpace(provider.ProviderName) ? providerName : provider.ProviderName);

            // Apply decorators if configured
            if (_performanceMetricsService != null)
            {
                _logger.LogDebug("Applying performance tracking decorator to client");
                var perfLogger = _loggerFactory.CreateLogger<PerformanceTrackingLLMClient>();
                client = new PerformanceTrackingLLMClient(client, _performanceMetricsService, perfLogger, providerName, true);
            }

            return client;
        }
    }
}
