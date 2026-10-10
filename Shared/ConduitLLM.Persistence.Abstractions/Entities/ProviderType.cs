namespace ConduitLLM.Configuration;

/// <summary>
/// Strongly-typed enumeration of LLM provider identities, including reserved types.
/// This identifies an adapter type, not a unique configured provider instance.
/// </summary>
public enum ProviderType
{
    Unknown = 0,
    OpenAI = 1,
    Groq = 2,
    Replicate = 3,
    Fireworks = 4,
    OpenAICompatible = 5,
    MiniMax = 6,
    Ultravox = 7, // Reserved until an adapter is implemented; not configurable.
    ElevenLabs = 8, // Reserved until an adapter is implemented; not configurable.
    Cerebras = 9,
    SambaNova = 10,
    DeepInfra = 11,
    Cloudflare = 12,
    OpenRouter = 13,
    Meta = 14,
    Azure = 15,
    Bedrock = 16,
    Vertex = 17
}

/// <summary>
/// Defines the provider types that can be used for provider configuration.
/// </summary>
public static class ProviderTypeCatalog
{
    /// <summary>
    /// All provider types backed by an operational provider adapter.
    /// New enum members must be added here only after their adapters are implemented.
    /// </summary>
    public static IReadOnlyList<ProviderType> ConfigurableTypes { get; } = Array.AsReadOnly(
        new[]
        {
            ProviderType.OpenAI,
            ProviderType.Groq,
            ProviderType.Replicate,
            ProviderType.Fireworks,
            ProviderType.OpenAICompatible,
            ProviderType.MiniMax,
            ProviderType.Cerebras,
            ProviderType.SambaNova,
            ProviderType.DeepInfra,
            ProviderType.Cloudflare,
            ProviderType.OpenRouter,
            ProviderType.Meta,
            ProviderType.Azure,
            ProviderType.Bedrock,
            ProviderType.Vertex
        });

    /// <summary>
    /// Returns whether the value identifies a configurable provider adapter.
    /// </summary>
    public static bool IsConfigurable(ProviderType providerType) =>
        ConfigurableTypes.Contains(providerType);
}
