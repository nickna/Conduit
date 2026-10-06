namespace ConduitLLM.Providers.OpenAICompatible
{
    /// <summary>
    /// OpenAICompatibleClient partial class containing utility and helper methods.
    /// </summary>
    public abstract partial class OpenAICompatibleClient
    {
        /// <summary>
        /// Translates an unsuccessful HTTP response into a provider-specific exception.
        /// Return <see langword="null"/> to use the shared communication exception.
        /// </summary>
        protected virtual Exception? TranslateHttpError(
            HttpResponseMessage response,
            string responseContent) => null;

        /// <summary>
        /// Posts JSON with the standard OpenAI-compatible headers, serialization options,
        /// logging, and provider-specific HTTP error translation.
        /// </summary>
        protected Task<TResponse> PostJsonAsync<TRequest, TResponse>(
            HttpClient client,
            string endpoint,
            TRequest request,
            string? apiKey,
            CancellationToken cancellationToken)
        {
            return Core.Utilities.HttpClientHelper.SendJsonRequestAsync<TRequest, TResponse>(
                client,
                HttpMethod.Post,
                endpoint,
                request,
                GetRequiredJsonTypeInfo<TRequest>(DefaultJsonOptions),
                GetRequiredJsonTypeInfo<TResponse>(DefaultJsonOptions),
                CreateStandardHeaders(apiKey),
                Logger,
                cancellationToken,
                TranslateHttpError);
        }

        /// <summary>
        /// Configure the HTTP client with provider-specific settings.
        /// </summary>
        /// <param name="client">The HTTP client to configure.</param>
        /// <param name="apiKey">The API key to use for authentication.</param>
        /// <remarks>
        /// This method adds standard headers and authentication to the HTTP client.
        /// Derived classes can override this method to provide provider-specific configuration.
        /// </remarks>
        protected override void ConfigureHttpClient(HttpClient client, string apiKey)
        {
            base.ConfigureHttpClient(client, apiKey);

            // Set the base address if not already set
            if (client.BaseAddress == null && !string.IsNullOrEmpty(BaseUrl))
            {
                client.BaseAddress = new Uri(BaseUrl);
            }

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"ConduitLLM-{Provider.ProviderType}Client/1.0");
        }

        // ExtractEnhancedErrorMessage is inherited from BaseLLMClient
    }
}
