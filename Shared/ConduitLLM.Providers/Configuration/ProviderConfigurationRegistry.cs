using System.Text.RegularExpressions;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Providers;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Providers.Authentication;

namespace ConduitLLM.Providers.Configuration
{
    /// <summary>
    /// Centralized registry for provider configurations.
    /// Eliminates duplicated Constants classes across provider implementations.
    /// </summary>
    public static class ProviderConfigurationRegistry
    {
        /// <summary>
        /// The Azure OpenAI REST API version used when an operator supplies none. Declared as the
        /// <c>api_version</c> setting's default so it is visible and overridable per provider rather
        /// than pinned in client code.
        /// </summary>
        public const string AzureDefaultApiVersion = "2024-02-01";

        /// <summary>
        /// Registry of provider configurations keyed by ProviderType.
        /// </summary>
        private static readonly Dictionary<ProviderType, ProviderConfiguration> Configurations = new()
        {
            [ProviderType.OpenAI] = new ProviderConfiguration
            {
                DisplayName = "OpenAI",
                HelpUrl = "https://platform.openai.com/api-keys",
                HelpText = "Create or manage an API key in the OpenAI platform.",
                DefaultBaseUrl = DefaultUrl(ProviderType.OpenAI),
                ModelsEndpoint = "/models",
                AuthenticationStrategy = BearerTokenStrategy.Instance,
                ErrorMessages = new ProviderErrorMessages
                {
                    InvalidApiKey = "Invalid API key for OpenAI. Please verify your API key is correct.",
                    RateLimitExceeded = "OpenAI API rate limit exceeded. Please try again later.",
                    QuotaExceeded = "Insufficient balance in your OpenAI account.",
                    ModelNotFound = "Model not found. Please verify the model ID is correct."
                },
                Settings = new[]
                {
                    new ProviderSettingDefinition
                    {
                        Key = "organization",
                        Label = "Organization ID",
                        HelpText = "Scopes requests and usage attribution to a specific OpenAI organization. Leave blank to use the API key's default organization.",
                        Placeholder = "org-...",
                        Required = false,
                        Binding = ProviderSettingBinding.Header,
                        BindingTarget = "OpenAI-Organization",
                        ValidationRegex = "^org-[A-Za-z0-9]+$"
                    },
                    new ProviderSettingDefinition
                    {
                        Key = "project",
                        Label = "Project ID",
                        HelpText = "Scopes requests and usage attribution to a specific project within the organization. Leave blank to use the API key's default project.",
                        Placeholder = "proj_...",
                        Required = false,
                        Binding = ProviderSettingBinding.Header,
                        BindingTarget = "OpenAI-Project",
                        ValidationRegex = "^proj_[A-Za-z0-9]+$"
                    }
                }
            },

            [ProviderType.Groq] = new ProviderConfiguration
            {
                DisplayName = "Groq",
                HelpUrl = "https://console.groq.com/keys",
                HelpText = "Create or manage an API key in the Groq console.",
                DefaultBaseUrl = DefaultUrl(ProviderType.Groq),
                ModelsEndpoint = "/models",
                AuthenticationStrategy = BearerTokenStrategy.Instance,
                ErrorMessages = new ProviderErrorMessages
                {
                    InvalidApiKey = "Invalid API key for Groq. Please verify your API key is correct.",
                    RateLimitExceeded = "Groq API rate limit exceeded. Please try again later or reduce your request frequency.",
                    ModelNotFound = "Model not found. Available Groq models include: llama3-8b-8192, llama3-70b-8192, mixtral-8x7b-32768, gemma-7b-it"
                }
            },

            [ProviderType.Fireworks] = new ProviderConfiguration
            {
                DisplayName = "Fireworks AI",
                HelpUrl = "https://app.fireworks.ai/account/api-keys",
                HelpText = "Create or manage an API key in your Fireworks AI account.",
                DefaultBaseUrl = DefaultUrl(ProviderType.Fireworks),
                ModelsEndpoint = "/models",
                AuthenticationStrategy = BearerTokenStrategy.Instance,
                ErrorMessages = new ProviderErrorMessages
                {
                    InvalidApiKey = "Invalid API key for Fireworks. Please verify your API key is correct.",
                    RateLimitExceeded = "Fireworks API rate limit exceeded. Please try again later.",
                    ModelNotFound = "Model not found. Please verify the model ID is correct."
                }
            },

            [ProviderType.Cerebras] = new ProviderConfiguration
            {
                DisplayName = "Cerebras",
                HelpUrl = "https://cloud.cerebras.ai",
                HelpText = "Create or manage an API key in Cerebras Cloud.",
                DefaultBaseUrl = DefaultUrl(ProviderType.Cerebras),
                ModelsEndpoint = "/models",
                AuthenticationStrategy = BearerTokenStrategy.Instance,
                ErrorMessages = new ProviderErrorMessages
                {
                    InvalidApiKey = "Invalid API key for Cerebras. Please verify your API key is correct.",
                    RateLimitExceeded = "Cerebras API rate limit exceeded. Please try again later.",
                    ModelNotFound = "Model not found. Please verify the model ID is correct.",
                    MissingApiKey = "API key is required for Cerebras"
                }
            },

            [ProviderType.SambaNova] = new ProviderConfiguration
            {
                DisplayName = "SambaNova Cloud",
                HelpUrl = "https://cloud.sambanova.ai/plans/pricing",
                HelpText = "Create or manage an API key in SambaNova Cloud.",
                DefaultBaseUrl = DefaultUrl(ProviderType.SambaNova),
                ModelsEndpoint = "/models",
                AuthenticationStrategy = BearerTokenStrategy.Instance,
                ErrorMessages = new ProviderErrorMessages
                {
                    InvalidApiKey = "Invalid API key for SambaNova. Please verify your API key is correct.",
                    RateLimitExceeded = "SambaNova API rate limit exceeded. Please try again later.",
                    ModelNotFound = "Model not found. Please verify the model ID is correct."
                }
            },

            [ProviderType.DeepInfra] = new ProviderConfiguration
            {
                DisplayName = "DeepInfra",
                HelpUrl = "https://deepinfra.com/docs/openai_api",
                HelpText = "Use a DeepInfra API key with its OpenAI-compatible inference API.",
                DefaultBaseUrl = DefaultUrl(ProviderType.DeepInfra),
                ModelsEndpoint = "/models",
                AuthenticationStrategy = BearerTokenStrategy.Instance,
                ErrorMessages = new ProviderErrorMessages
                {
                    InvalidApiKey = "Invalid API key for DeepInfra. Please verify your API key is correct.",
                    RateLimitExceeded = "DeepInfra API rate limit exceeded. Please try again later.",
                    ModelNotFound = "Model not found. Please verify the model ID is correct."
                }
            },

            [ProviderType.Cloudflare] = new ProviderConfiguration
            {
                DisplayName = "Cloudflare Workers AI",
                HelpUrl = "https://developers.cloudflare.com/workers-ai/",
                HelpText = "Create an API token in the Cloudflare dashboard and enter the account ID used to build the Workers AI endpoint.",
                DefaultBaseUrl = DefaultUrl(ProviderType.Cloudflare),
                ModelsEndpoint = "/models",
                // Model discovery is supported via the native /ai/models/search endpoint
                // (see CloudflareClient.GetModelsAsync), not the OpenAI-style /models path.
                AuthenticationStrategy = BearerTokenStrategy.Instance,
                ErrorMessages = new ProviderErrorMessages
                {
                    InvalidApiKey = "Invalid API token for Cloudflare. Please verify your Cloudflare API token is correct.",
                    RateLimitExceeded = "Cloudflare Workers AI rate limit exceeded. Please try again later.",
                    ModelNotFound = "Model not found. Cloudflare Workers AI models use the @cf/provider/model-name format.",
                    MissingApiKey = "API token is required for Cloudflare Workers AI"
                },
                Settings = new[]
                {
                    new ProviderSettingDefinition
                    {
                        Key = "account_id",
                        Label = "Account ID",
                        HelpText = "Your Cloudflare account ID (shown in the dashboard URL and on the Workers AI page). Used to build the API base URL.",
                        Placeholder = "e.g. 0123456789abcdef0123456789abcdef",
                        Required = true,
                        Binding = ProviderSettingBinding.UrlPathToken,
                        BindingTarget = "account_id",
                        ValidationRegex = "^[0-9a-fA-F]{32}$"
                    }
                }
            },

            [ProviderType.Replicate] = new ProviderConfiguration
            {
                DisplayName = "Replicate",
                HelpUrl = "https://replicate.com/account/api-tokens",
                HelpText = "Create or manage an API token in your Replicate account.",
                DefaultBaseUrl = DefaultUrl(ProviderType.Replicate),
                ModelsEndpoint = "/models",
                HealthCheckEndpoint = "/account",
                AuthenticationStrategy = TokenStrategy.Instance,
                ErrorMessages = new ProviderErrorMessages
                {
                    InvalidApiKey = "Invalid API token for Replicate. Please verify your API token is correct.",
                    RateLimitExceeded = "Replicate API rate limit exceeded. Please try again later.",
                    ModelNotFound = "Model not found. Please verify the model version hash is correct."
                }
            },

            [ProviderType.MiniMax] = new ProviderConfiguration
            {
                DisplayName = "MiniMax",
                HelpText = "Contact MiniMax support or use the MiniMax platform to obtain API access.",
                DefaultBaseUrl = DefaultUrl(ProviderType.MiniMax),
                ModelsEndpoint = "/models",
                AuthenticationStrategy = BearerTokenStrategy.Instance,
                ErrorMessages = new ProviderErrorMessages
                {
                    InvalidApiKey = "Invalid API key for MiniMax. Please verify your API key is correct.",
                    RateLimitExceeded = "MiniMax API rate limit exceeded. Please try again later.",
                    ModelNotFound = "Model not found. Please verify the model ID is correct."
                }
            },

            [ProviderType.OpenAICompatible] = new ProviderConfiguration
            {
                DisplayName = "OpenAI Compatible",
                HelpText = "Configure the endpoint and API key for an OpenAI-compatible service.",
                DefaultBaseUrl = DefaultUrl(ProviderType.OpenAICompatible), // Explicit provider URL remains required.
                ModelsEndpoint = "/models",
                AuthenticationStrategy = BearerTokenStrategy.Instance,
                ErrorMessages = new ProviderErrorMessages
                {
                    InvalidApiKey = "Invalid API key. Please verify your API key is correct.",
                    RateLimitExceeded = "API rate limit exceeded. Please try again later.",
                    ModelNotFound = "Model not found. Please verify the model ID is correct."
                }
            },

            [ProviderType.OpenRouter] = new ProviderConfiguration
            {
                DisplayName = "OpenRouter",
                HelpUrl = "https://openrouter.ai/keys",
                HelpText = "Create or manage an API key in OpenRouter.",
                DefaultBaseUrl = DefaultUrl(ProviderType.OpenRouter),
                ModelsEndpoint = "/models",
                AuthenticationStrategy = BearerTokenStrategy.Instance,
                ErrorMessages = new ProviderErrorMessages
                {
                    InvalidApiKey = "Invalid API key for OpenRouter. Please verify your API key is correct.",
                    RateLimitExceeded = "OpenRouter API rate limit exceeded. Please try again later.",
                    ModelNotFound = "Model not found. OpenRouter models use provider/model-name format (e.g., openai/gpt-4o)."
                }
            },

            // Azure OpenAI is deployment-scoped: every operation lives under
            // /openai/deployments/{deployment}/... on a per-resource host, and every request carries
            // an api-version. The deployment is per-model and comes from the model mapping's
            // provider model ID; the resource and api-version are provider-scoped and declared here.
            [ProviderType.Azure] = new ProviderConfiguration
            {
                DisplayName = "Azure OpenAI",
                HelpUrl = "https://learn.microsoft.com/azure/ai-services/openai/",
                HelpText = "Use a key from the Azure OpenAI resource. Set each model mapping's provider model ID to its Azure deployment name.",
                DefaultBaseUrl = DefaultUrl(ProviderType.Azure),
                ModelsEndpoint = "/openai/deployments",
                AuthenticationStrategy = ApiKeyHeaderStrategy.AzureInstance,
                ErrorMessages = new ProviderErrorMessages
                {
                    InvalidApiKey = "Invalid API key for Azure OpenAI. Please verify the key from your Azure OpenAI resource.",
                    RateLimitExceeded = "Azure OpenAI rate limit exceeded. Please try again later or raise the deployment's quota.",
                    ModelNotFound = "Deployment not found. Azure addresses models by deployment name, not model name - verify the deployment exists on this resource.",
                    MissingApiKey = "API key is required for Azure OpenAI"
                },
                Settings = new[]
                {
                    new ProviderSettingDefinition
                    {
                        Key = "resource_name",
                        Label = "Resource Name",
                        HelpText = "The name of your Azure OpenAI resource, as it appears in the portal. Used to build the endpoint https://<resource>.openai.azure.com. Set a custom API endpoint instead if your resource uses a private or custom domain.",
                        Placeholder = "e.g. my-openai-resource",
                        Required = true,
                        Binding = ProviderSettingBinding.UrlPathToken,
                        BindingTarget = "resource_name",
                        ValidationRegex = "^[A-Za-z0-9][A-Za-z0-9-]{1,62}$"
                    },
                    new ProviderSettingDefinition
                    {
                        Key = "api_version",
                        Label = "API Version",
                        HelpText = "The Azure OpenAI REST API version sent with every request. Leave blank to use the version Conduit was tested against.",
                        Placeholder = AzureDefaultApiVersion,
                        Required = false,
                        Binding = ProviderSettingBinding.QueryParam,
                        BindingTarget = "api-version",
                        DefaultValue = AzureDefaultApiVersion,
                        ValidationRegex = "^[0-9]{4}-[0-9]{2}-[0-9]{2}(-preview)?$"
                    }
                }
            },

            // Amazon Bedrock is region-scoped: the runtime endpoint host embeds the region, and the
            // region also forms the SigV4 credential scope. Authentication is either SigV4 (the key
            // credential's ApiKey is the access key ID and the secret access key / session token are
            // secret settings) or a Bedrock API key sent as a Bearer token when no secret access key
            // is configured. BedrockClient signs per request, so the registered strategy only covers
            // the Bearer mode.
            [ProviderType.Bedrock] = new ProviderConfiguration
            {
                DisplayName = "Amazon Bedrock",
                HelpUrl = "https://docs.aws.amazon.com/bedrock/latest/userguide/getting-started.html",
                HelpText = "Use an AWS access key ID with its secret access key, or a Bedrock API key, and select the AWS region hosting the models.",
                DefaultBaseUrl = DefaultUrl(ProviderType.Bedrock),
                AuthenticationStrategy = BearerTokenStrategy.Instance,
                ErrorMessages = new ProviderErrorMessages
                {
                    InvalidApiKey = "Invalid AWS credentials for Bedrock. Verify the access key ID and secret access key (or Bedrock API key) and that the key has bedrock permissions.",
                    RateLimitExceeded = "Amazon Bedrock throttled the request. Please try again later or request a quota increase.",
                    ModelNotFound = "Model not found. Bedrock model IDs look like 'anthropic.claude-sonnet-4-20250514-v1:0'; cross-region inference profiles are prefixed (e.g. 'us.anthropic...'). Verify the model is enabled in this region.",
                    MissingApiKey = "An AWS access key ID or Bedrock API key is required for Amazon Bedrock"
                },
                Settings = new[]
                {
                    new ProviderSettingDefinition
                    {
                        Key = "region",
                        Label = "AWS Region",
                        HelpText = "The AWS region hosting the Bedrock models (for example us-east-1). Builds the endpoint https://bedrock-runtime.<region>.amazonaws.com and scopes request signing.",
                        Placeholder = "e.g. us-east-1",
                        Required = true,
                        Binding = ProviderSettingBinding.UrlPathToken,
                        BindingTarget = "region",
                        ValidationRegex = "^[a-z]{2}(-[a-z]+)+-[0-9]+$"
                    },
                    new ProviderSettingDefinition
                    {
                        Key = "secret_access_key",
                        Label = "Secret Access Key",
                        HelpText = "The AWS secret access key paired with the access key ID entered as the API key. Leave blank when the API key field holds a Bedrock API key instead of an IAM access key.",
                        Required = false,
                        Secret = true,
                        Binding = ProviderSettingBinding.AuthScope
                    },
                    new ProviderSettingDefinition
                    {
                        Key = "session_token",
                        Label = "Session Token",
                        HelpText = "The STS session token when using temporary credentials (access key IDs starting with ASIA). Leave blank for long-term credentials.",
                        Required = false,
                        Secret = true,
                        Binding = ProviderSettingBinding.AuthScope
                    }
                }
            },

            // Vertex AI exposes an OpenAI-compatible Chat Completions surface. Project and
            // location identify the Google Cloud resource in every endpoint; authentication uses
            // a short-lived OAuth access token minted from the service-account JSON stored on the
            // key credential. The client owns that exchange, so no separate API-key value is
            // required from the operator.
            [ProviderType.Vertex] = new ProviderConfiguration
            {
                DisplayName = "Google Vertex AI",
                HelpUrl = "https://cloud.google.com/vertex-ai/generative-ai/docs/start/openai",
                HelpText = "Use a Google Cloud service account with the Vertex AI User role. "
                    + "The project and location select the Vertex AI endpoint; the service-account JSON is stored encrypted on the key credential.",
                DefaultBaseUrl = DefaultUrl(ProviderType.Vertex),
                ModelsEndpoint = "/models",
                AuthenticationStrategy = OAuthAccessTokenStrategy.Instance,
                ErrorMessages = new ProviderErrorMessages
                {
                    InvalidApiKey = "Vertex AI rejected the service-account credentials. Verify the JSON key and that the service account is enabled.",
                    RateLimitExceeded = "Vertex AI quota was exceeded. Please try again later or increase the project's quota.",
                    ModelNotFound = "Vertex AI model not found. Use a publisher-qualified model ID such as 'google/gemini-2.5-flash'.",
                    MissingApiKey = "A service-account JSON credential is required for Google Vertex AI."
                },
                Settings = new[]
                {
                    new ProviderSettingDefinition
                    {
                        Key = "project_id",
                        Label = "Google Cloud Project ID",
                        HelpText = "The Google Cloud project with Vertex AI enabled and billing configured.",
                        Placeholder = "e.g. my-vertex-project",
                        Required = true,
                        Binding = ProviderSettingBinding.UrlPathToken,
                        BindingTarget = "project_id",
                        ValidationRegex = "^[a-z][a-z0-9-]{4,28}[a-z0-9]$"
                    },
                    new ProviderSettingDefinition
                    {
                        Key = "location",
                        Label = "Vertex AI Location",
                        HelpText = "The Vertex AI region hosting the models, or global for models that support the global endpoint.",
                        Placeholder = "e.g. us-central1",
                        Required = true,
                        Binding = ProviderSettingBinding.UrlPathToken,
                        BindingTarget = "location",
                        ValidationRegex = "^(global|[a-z]+(?:-[a-z0-9]+)+[0-9])$"
                    },
                    new ProviderSettingDefinition
                    {
                        Key = "service_account_json",
                        Label = "Service Account JSON",
                        HelpText = "The complete JSON key document for a service account with permission to use Vertex AI. "
                            + "It is encrypted at rest and never returned by the API.",
                        Placeholder = "{ \"type\": \"service_account\", ... }",
                        Required = true,
                        Secret = true,
                        Binding = ProviderSettingBinding.AuthScope
                    }
                }
            },

            [ProviderType.Meta] = new ProviderConfiguration
            {
                DisplayName = "Meta AI",
                HelpUrl = "https://ai.developer.meta.com",
                HelpText = "Create or manage an API key in the Meta AI developer platform.",
                DefaultBaseUrl = DefaultUrl(ProviderType.Meta),
                ModelsEndpoint = "/models",
                AuthenticationStrategy = BearerTokenStrategy.Instance,
                ErrorMessages = new ProviderErrorMessages
                {
                    MissingApiKey = "API key is missing for provider 'meta'",
                    InvalidApiKey = "Invalid Meta Model API key. Please check your credentials.",
                    RateLimitExceeded = "Meta Model API rate limit exceeded. Please try again later or reduce your request frequency.",
                    ModelNotFound = "The specified model is not available. Please check the model name and try again.",
                    QuotaExceeded = "API quota exceeded. Please check your usage limits or remaining credits."
                }
            }
        };

        private static string DefaultUrl(ProviderType providerType) =>
            ProviderAdapterDefaultsRegistry.GetRequired(providerType).DefaultBaseUrl;

        /// <summary>
        /// Gets the configuration for a provider type.
        /// </summary>
        /// <param name="providerType">The provider type.</param>
        /// <returns>The provider configuration, or null if not found.</returns>
        public static ProviderConfiguration? GetConfiguration(ProviderType providerType)
        {
            return Configurations.TryGetValue(providerType, out var config) ? config : null;
        }

        /// <summary>
        /// Tries to get the configuration for a provider type.
        /// </summary>
        /// <param name="providerType">The provider type.</param>
        /// <param name="configuration">The configuration if found.</param>
        /// <returns>True if found, false otherwise.</returns>
        public static bool TryGetConfiguration(ProviderType providerType, out ProviderConfiguration? configuration)
        {
            return Configurations.TryGetValue(providerType, out configuration);
        }

        /// <summary>
        /// Gets every provider type with immutable adapter configuration.
        /// </summary>
        public static IReadOnlyCollection<ProviderType> GetRegisteredProviderTypes() =>
            Configurations.Keys.ToArray();

        /// <summary>
        /// Gets the default base URL for a provider type.
        /// </summary>
        /// <param name="providerType">The provider type.</param>
        /// <returns>The default base URL, or null if not found.</returns>
        public static string? GetDefaultBaseUrl(ProviderType providerType)
        {
            return GetConfiguration(providerType)?.DefaultBaseUrl;
        }

        /// <summary>
        /// Resolves the effective base URL from an operator override or the registered default,
        /// substituting any URL path tokens from structured provider settings.
        /// </summary>
        /// <remarks>
        /// Template-shaped Cloudflare URLs from before structured settings were removed by the
        /// contract migration for issue #1244. Any remaining database value is therefore an
        /// operator-owned override and takes precedence over the registered default.
        /// </remarks>
        public static string ResolveBaseUrl(Provider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);

            var defaultBaseUrl = GetDefaultBaseUrl(provider.ProviderType);
            if (string.IsNullOrWhiteSpace(provider.BaseUrl))
            {
                var fallback = defaultBaseUrl
                    ?? throw new InvalidOperationException($"No default base URL is registered for {provider.ProviderType}.");
                return ApplyUrlPathTokens(fallback, provider.ProviderType, provider.Settings);
            }

            return ApplyUrlPathTokens(
                provider.BaseUrl.TrimEnd('/'),
                provider.ProviderType,
                provider.Settings);
        }

        private static readonly Regex UnresolvedTokenPattern =
            new(@"\{([a-zA-Z0-9_]+)\}", RegexOptions.Compiled);

        /// <summary>
        /// Substitutes <c>{token}</c> placeholders in a base URL using the provider's structured
        /// settings (for example Cloudflare's <c>{account_id}</c>). Any placeholder left unresolved
        /// means a required setting was not supplied, which raises an actionable configuration error
        /// rather than allowing a malformed request to be sent.
        /// </summary>
        /// <param name="baseUrl">The raw base URL, possibly containing <c>{token}</c> placeholders.</param>
        /// <param name="providerType">The provider type whose setting definitions drive substitution.</param>
        /// <param name="settings">The operator-supplied setting values, keyed by setting key.</param>
        /// <returns>The base URL with all URL-path-token settings substituted.</returns>
        /// <exception cref="ConfigurationException">Thrown when a required <c>{token}</c> is unresolved.</exception>
        public static string ApplyUrlPathTokens(
            string baseUrl,
            ProviderType providerType,
            IReadOnlyDictionary<string, string>? settings)
        {
            var definitions = GetConfiguration(providerType)?.Settings
                ?? (IReadOnlyList<ProviderSettingDefinition>)Array.Empty<ProviderSettingDefinition>();

            var result = baseUrl;
            foreach (var definition in definitions)
            {
                if (definition.Binding != ProviderSettingBinding.UrlPathToken)
                {
                    continue;
                }

                if (settings != null
                    && settings.TryGetValue(definition.Key, out var value)
                    && !string.IsNullOrWhiteSpace(value))
                {
                    result = result.Replace("{" + definition.EffectiveBindingTarget + "}", value.Trim());
                }
            }

            var unresolved = UnresolvedTokenPattern.Matches(result)
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (unresolved.Count > 0)
            {
                var missing = string.Join(", ", unresolved.Select(token => DescribeSetting(providerType, token)));
                throw new ConfigurationException(
                    $"{providerType} is missing required configuration: {missing}. "
                    + "Provide the value in the provider settings.");
            }

            return result.TrimEnd('/');
        }

        /// <summary>
        /// Gets the settings a provider type declares as secret. Their values are held on the key
        /// credential and encrypted at rest, never in the plaintext <c>Provider.Settings</c> bag.
        /// </summary>
        /// <param name="providerType">The provider type.</param>
        /// <returns>The secret setting declarations; empty when the provider declares none.</returns>
        public static IReadOnlyList<ProviderSettingDefinition> GetSecretSettings(ProviderType providerType) =>
            GetConfiguration(providerType)?.Settings.Where(setting => setting.Secret).ToArray()
            ?? Array.Empty<ProviderSettingDefinition>();

        /// <summary>
        /// Gets the settings a provider type declares as non-secret. Their values live in the
        /// provider's plaintext <c>Settings</c> bag.
        /// </summary>
        /// <param name="providerType">The provider type.</param>
        /// <returns>The non-secret setting declarations; empty when the provider declares none.</returns>
        public static IReadOnlyList<ProviderSettingDefinition> GetNonSecretSettings(ProviderType providerType) =>
            GetConfiguration(providerType)?.Settings.Where(setting => !setting.Secret).ToArray()
            ?? Array.Empty<ProviderSettingDefinition>();

        /// <summary>
        /// Names the secret settings a provider type requires that the supplied credential does not
        /// carry, using their operator-facing labels.
        /// </summary>
        /// <param name="providerType">The provider type whose declarations are checked.</param>
        /// <param name="secretSettings">The credential's secret setting values, keyed by setting key.</param>
        /// <returns>The labels of the missing required secrets, in declaration order.</returns>
        public static IReadOnlyList<string> GetMissingRequiredSecrets(
            ProviderType providerType,
            IReadOnlyDictionary<string, string>? secretSettings) =>
            GetMissingRequiredSecrets(GetSecretSettings(providerType), secretSettings);

        /// <summary>
        /// Names the required secrets among the given declarations that the supplied values do not
        /// cover, using their operator-facing labels.
        /// </summary>
        /// <param name="declarations">The setting declarations to check; non-secret ones are ignored.</param>
        /// <param name="secretSettings">The credential's secret setting values, keyed by setting key.</param>
        /// <returns>The labels of the missing required secrets, in declaration order.</returns>
        public static IReadOnlyList<string> GetMissingRequiredSecrets(
            IEnumerable<ProviderSettingDefinition> declarations,
            IReadOnlyDictionary<string, string>? secretSettings) =>
            declarations
                .Where(definition => definition.Secret
                    && definition.Required
                    && (secretSettings == null
                        || !secretSettings.TryGetValue(definition.Key, out var value)
                        || string.IsNullOrWhiteSpace(value)))
                .Select(definition => definition.Label)
                .ToArray();

        /// <summary>
        /// Resolves the value of a single structured setting, falling back to the value declared in
        /// the registry when the operator supplied none.
        /// </summary>
        /// <param name="providerType">The provider type whose setting definitions are consulted.</param>
        /// <param name="settings">The operator-supplied setting values, keyed by setting key.</param>
        /// <param name="key">The setting key to resolve.</param>
        /// <returns>The effective value, or null when neither a value nor a default exists.</returns>
        public static string? GetSettingValue(
            ProviderType providerType,
            IReadOnlyDictionary<string, string>? settings,
            string key)
        {
            if (settings != null && settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            var definition = GetConfiguration(providerType)?.Settings
                .FirstOrDefault(setting => string.Equals(setting.Key, key, StringComparison.Ordinal));

            return string.IsNullOrWhiteSpace(definition?.DefaultValue) ? null : definition!.DefaultValue;
        }

        /// <summary>
        /// Resolves the HTTP headers a provider's structured settings contribute to every outbound
        /// request (for example OpenAI's <c>OpenAI-Organization</c>).
        /// </summary>
        /// <param name="providerType">The provider type whose setting definitions drive the mapping.</param>
        /// <param name="settings">The operator-supplied setting values, keyed by setting key.</param>
        /// <returns>Header name/value pairs; empty when the provider declares or supplies none.</returns>
        public static IReadOnlyList<KeyValuePair<string, string>> GetHeaderSettings(
            ProviderType providerType,
            IReadOnlyDictionary<string, string>? settings)
        {
            if (settings == null || settings.Count == 0)
            {
                return Array.Empty<KeyValuePair<string, string>>();
            }

            var definitions = GetConfiguration(providerType)?.Settings;
            if (definitions == null || definitions.Count == 0)
            {
                return Array.Empty<KeyValuePair<string, string>>();
            }

            var headers = new List<KeyValuePair<string, string>>();
            foreach (var definition in definitions)
            {
                if (definition.Binding != ProviderSettingBinding.Header)
                {
                    continue;
                }

                if (settings.TryGetValue(definition.Key, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    headers.Add(new KeyValuePair<string, string>(definition.EffectiveBindingTarget, value.Trim()));
                }
            }

            return headers;
        }

        /// <summary>
        /// Resolves a human-readable label for an unresolved URL token, preferring the declared
        /// setting label and falling back to the raw token name.
        /// </summary>
        private static string DescribeSetting(ProviderType providerType, string token)
        {
            var definition = GetConfiguration(providerType)?.Settings
                .FirstOrDefault(setting =>
                    string.Equals(setting.EffectiveBindingTarget, token, StringComparison.OrdinalIgnoreCase));

            return definition?.Label ?? token;
        }

        /// <summary>
        /// Gets the authentication strategy for a provider type.
        /// </summary>
        /// <param name="providerType">The provider type.</param>
        /// <returns>The authentication strategy, or BearerTokenStrategy as default.</returns>
        public static IAuthenticationStrategy GetAuthenticationStrategy(ProviderType providerType)
        {
            return GetConfiguration(providerType)?.AuthenticationStrategy ?? BearerTokenStrategy.Instance;
        }

        /// <summary>
        /// Gets the health check endpoint for a provider type.
        /// Returns the models endpoint by default if no specific health check endpoint is defined.
        /// </summary>
        /// <param name="providerType">The provider type.</param>
        /// <returns>The health check endpoint path.</returns>
        public static string GetHealthCheckEndpoint(ProviderType providerType)
        {
            var config = GetConfiguration(providerType);
            if (config == null)
            {
                return "/models";
            }

            return config.HealthCheckEndpoint ?? config.ModelsEndpoint ?? "/models";
        }

        /// <summary>
        /// Gets error messages for a provider type.
        /// </summary>
        /// <param name="providerType">The provider type.</param>
        /// <returns>The error messages, or default messages if not found.</returns>
        public static ProviderErrorMessages GetErrorMessages(ProviderType providerType)
        {
            return GetConfiguration(providerType)?.ErrorMessages ?? ProviderErrorMessages.Default;
        }

    }

    /// <summary>
    /// Configuration for an LLM provider.
    /// </summary>
    public record ProviderConfiguration
    {
        /// <summary>
        /// The operator-facing provider name used by administrative clients.
        /// </summary>
        public required string DisplayName { get; init; }

        /// <summary>
        /// Optional provider documentation URL shown while configuring credentials.
        /// </summary>
        public string? HelpUrl { get; init; }

        /// <summary>
        /// Optional provider-level guidance shown while configuring credentials.
        /// </summary>
        public string? HelpText { get; init; }

        /// <summary>
        /// The default base URL for the provider's API.
        /// </summary>
        public required string DefaultBaseUrl { get; init; }

        /// <summary>
        /// The endpoint path for listing models (e.g., "/models").
        /// </summary>
        public string? ModelsEndpoint { get; init; }

        /// <summary>
        /// The endpoint path for health checks. If null, uses ModelsEndpoint.
        /// </summary>
        public string? HealthCheckEndpoint { get; init; }

        /// <summary>
        /// The authentication strategy to use for this provider.
        /// </summary>
        public required IAuthenticationStrategy AuthenticationStrategy { get; init; }

        /// <summary>
        /// Error messages specific to this provider.
        /// </summary>
        public required ProviderErrorMessages ErrorMessages { get; init; }

        /// <summary>
        /// Structured, provider-scoped settings the operator supplies in addition to the API key
        /// (for example a Cloudflare account ID). Empty for providers that need only a key and URL.
        /// </summary>
        public IReadOnlyList<ProviderSettingDefinition> Settings { get; init; } = Array.Empty<ProviderSettingDefinition>();
    }

    /// <summary>
    /// Provider-specific error messages.
    /// </summary>
    public record ProviderErrorMessages
    {
        // Default message constants to avoid circular initialization
        private const string DefaultInvalidApiKey = "Invalid API key. Please verify your API key is correct.";
        private const string DefaultRateLimitExceeded = "API rate limit exceeded. Please try again later.";
        private const string DefaultModelNotFound = "Model not found. Please verify the model ID is correct.";
        private const string DefaultQuotaExceeded = "API quota exceeded. Please check your usage limits or remaining credits.";
        private const string DefaultMissingApiKey = "API key is required.";

        /// <summary>
        /// Default error messages for unknown providers.
        /// </summary>
        public static readonly ProviderErrorMessages Default = new()
        {
            InvalidApiKey = DefaultInvalidApiKey,
            RateLimitExceeded = DefaultRateLimitExceeded,
            ModelNotFound = DefaultModelNotFound,
            QuotaExceeded = DefaultQuotaExceeded,
            MissingApiKey = DefaultMissingApiKey
        };

        /// <summary>
        /// Message for invalid API key errors.
        /// </summary>
        public string InvalidApiKey { get; init; } = DefaultInvalidApiKey;

        /// <summary>
        /// Message for rate limit exceeded errors.
        /// </summary>
        public string RateLimitExceeded { get; init; } = DefaultRateLimitExceeded;

        /// <summary>
        /// Message for model not found errors.
        /// </summary>
        public string ModelNotFound { get; init; } = DefaultModelNotFound;

        /// <summary>
        /// Message for quota or account-balance errors.
        /// </summary>
        public string QuotaExceeded { get; init; } = DefaultQuotaExceeded;

        /// <summary>
        /// Message for missing API key errors.
        /// </summary>
        public string MissingApiKey { get; init; } = DefaultMissingApiKey;
    }
}
