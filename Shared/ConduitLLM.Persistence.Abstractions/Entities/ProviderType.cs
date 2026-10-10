namespace ConduitLLM.Configuration;

/// <summary>
/// Strongly-typed enumeration of supported LLM providers. This identifies an
/// adapter type, not a unique configured provider instance.
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
    Ultravox = 7,
    ElevenLabs = 8,
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
    /// </summary>
    public static IReadOnlyList<ProviderType> ConfigurableTypes { get; } = Array.AsReadOnly(
        Enum.GetValues<ProviderType>()
            .Where(providerType => providerType != ProviderType.Unknown)
            .ToArray());

    /// <summary>
    /// Returns whether the value identifies a configurable provider adapter.
    /// </summary>
    public static bool IsConfigurable(ProviderType providerType) =>
        providerType != ProviderType.Unknown && Enum.IsDefined(providerType);
}
