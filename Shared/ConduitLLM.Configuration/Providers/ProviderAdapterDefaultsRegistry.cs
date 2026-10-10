namespace ConduitLLM.Configuration.Providers;

/// <summary>
/// Canonical immutable protocol defaults for provider adapters.
/// </summary>
/// <remarks>
/// A configured <c>Provider.BaseUrl</c> is an operator-owned database override and takes precedence
/// wherever the adapter supports overrides. OpenAI-compatible providers always require an explicit
/// database URL even though their protocol fallback is recorded here for registry completeness.
/// Model capabilities are intentionally not stored here; they belong to model and provider-association
/// records because they vary by model.
/// </remarks>
public static class ProviderAdapterDefaultsRegistry
{
    private static readonly IReadOnlyDictionary<ProviderType, ProviderAdapterDefaults> Defaults =
        new Dictionary<ProviderType, ProviderAdapterDefaults>
        {
            [ProviderType.OpenAI] = new("https://api.openai.com/v1"),
            [ProviderType.Groq] = new("https://api.groq.com/openai/v1"),
            [ProviderType.Replicate] = new("https://api.replicate.com/v1"),
            [ProviderType.Fireworks] = new("https://api.fireworks.ai/inference/v1"),
            [ProviderType.OpenAICompatible] = new("https://api.openai.com/v1", RequiresBaseUrlOverride: true),
            [ProviderType.MiniMax] = new("https://api.minimax.io"),
            [ProviderType.Cerebras] = new("https://api.cerebras.ai/v1"),
            [ProviderType.SambaNova] = new("https://api.sambanova.ai/v1"),
            [ProviderType.DeepInfra] = new("https://api.deepinfra.com/v1/openai"),
            [ProviderType.Cloudflare] = new("https://api.cloudflare.com/client/v4/accounts/{account_id}/ai/v1"),
            [ProviderType.OpenRouter] = new("https://openrouter.ai/api/v1"),
            [ProviderType.Meta] = new("https://api.meta.ai/v1"),
            [ProviderType.Azure] = new("https://{resource_name}.openai.azure.com"),
            [ProviderType.Bedrock] = new("https://bedrock-runtime.{region}.amazonaws.com"),
            [ProviderType.Vertex] = new(
                "https://aiplatform.googleapis.com/v1beta1/projects/{project_id}/locations/{location}/endpoints/openapi")
        };

    public static ProviderAdapterDefaults GetRequired(ProviderType providerType) =>
        Defaults.TryGetValue(providerType, out var defaults)
            ? defaults
            : throw new ArgumentOutOfRangeException(nameof(providerType), providerType, "No adapter defaults are registered.");

    public static bool TryGet(ProviderType providerType, out ProviderAdapterDefaults? defaults) =>
        Defaults.TryGetValue(providerType, out defaults);

    public static IReadOnlyCollection<ProviderType> GetRegisteredProviderTypes() =>
        Defaults.Keys.ToArray();
}

public sealed record ProviderAdapterDefaults(
    string DefaultBaseUrl,
    bool RequiresBaseUrlOverride = false);
