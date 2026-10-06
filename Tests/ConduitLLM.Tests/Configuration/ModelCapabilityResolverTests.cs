using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Models;

namespace ConduitLLM.Tests.Configuration;

public sealed class ModelCapabilityResolverTests
{
    [Fact]
    public void Resolve_DistinguishesVideoInputFromVideoGeneration()
    {
        var model = new Model
        {
            SupportsVideoGeneration = false,
            InputModalitiesJson = ModelModalities.Serialize(["text", "video"]),
            OutputModalitiesJson = ModelModalities.Serialize(["text"])
        };

        var capabilities = ModelCapabilityResolver.Resolve(model);

        Assert.True(capabilities.SupportsVideoInput);
        Assert.True(capabilities.SupportsVideoUnderstanding);
        Assert.False(capabilities.SupportsVideoGeneration);
    }

    [Fact]
    public void Resolve_PreservesUnknownInsteadOfTreatingItAsUnsupported()
    {
        var model = new Model { CapabilitySource = ModelCapabilitySource.Unknown };

        var capabilities = ModelCapabilityResolver.Resolve(model);

        Assert.Null(capabilities.InputModalities);
        Assert.Null(capabilities.OutputModalities);
        Assert.Equal(ModelCapabilitySource.Unknown, capabilities.CapabilitySource);
    }

    [Fact]
    public void Resolve_ProviderOverrideWinsWithoutChangingCanonicalModel()
    {
        var model = new Model
        {
            SupportsChat = true,
            InputModalitiesJson = ModelModalities.Serialize(["text"]),
            OutputModalitiesJson = ModelModalities.Serialize(["text"]),
            CapabilitySource = ModelCapabilitySource.Curated
        };
        var association = new ModelProviderTypeAssociation
        {
            InputModalitiesJson = ModelModalities.Serialize(["text", "video"]),
            CapabilitySource = ModelCapabilitySource.ProviderApi
        };

        var capabilities = ModelCapabilityResolver.Resolve(model, association);

        Assert.True(capabilities.SupportsVideoInput);
        Assert.Equal(ModelCapabilitySource.ProviderApi, capabilities.CapabilitySource);
        Assert.Equal(["text"], ModelModalities.Parse(model.InputModalitiesJson));
    }

    [Fact]
    public void Resolve_DerivesLegacyVisionAliasFromEffectiveImageInput()
    {
        var model = new Model
        {
            SupportsVision = false,
            InputModalitiesJson = ModelModalities.Serialize(["text"]),
            OutputModalitiesJson = ModelModalities.Serialize(["text"])
        };
        var association = new ModelProviderTypeAssociation
        {
            InputModalitiesJson = ModelModalities.Serialize(["text", "image"])
        };

        var capabilities = ModelCapabilityResolver.Resolve(model, association);

        Assert.True(capabilities.SupportsImageInput);
        Assert.True(capabilities.SupportsVision);
    }

    [Fact]
    public void Resolve_IncludesEffectiveTokenLimitsAndProvenance()
    {
        var verifiedAt = DateTime.UtcNow;
        var model = new Model
        {
            MaxInputTokens = 100_000,
            MaxOutputTokens = 8_000,
            CapabilitySource = ModelCapabilitySource.Curated
        };
        var association = new ModelProviderTypeAssociation
        {
            MaxOutputTokens = 16_000,
            CapabilitySource = ModelCapabilitySource.ProviderApi,
            CapabilitiesLastVerifiedAt = verifiedAt
        };

        var capabilities = ModelCapabilityResolver.Resolve(model, association);

        Assert.Equal(100_000, capabilities.MaxInputTokens);
        Assert.Equal(16_000, capabilities.MaxOutputTokens);
        Assert.Equal(ModelCapabilitySource.ProviderApi, capabilities.CapabilitySource);
        Assert.Equal(verifiedAt, capabilities.CapabilitiesLastVerifiedAt);
    }

    [Fact]
    public void PersistedCapabilityContracts_PreserveWebJsonAndLegacyReads()
    {
        var serialized = ModelCapabilityResolver.SerializeOverrides(
            new ProviderOperationalCapabilities
            {
                SupportsChat = true,
                SupportsFunctionCalling = false
            });

        Assert.Equal(
            "{\"supportsChat\":true,\"supportsStreaming\":null,\"supportsVision\":null,"
            + "\"supportsImageGeneration\":null,\"supportsVideoGeneration\":null,"
            + "\"supportsEmbeddings\":null,\"supportsFunctionCalling\":false,"
            + "\"supportsSpeechToText\":null,\"supportsTextToSpeech\":null,\"supportsRerank\":null}",
            serialized);

        var legacy = ModelCapabilityResolver.DeserializeOverrides(
            "{\"SupportsChat\":true,\"SupportsStreaming\":false}");

        Assert.NotNull(legacy);
        Assert.True(legacy.SupportsChat);
        Assert.False(legacy.SupportsStreaming);
        Assert.Equal("[\"image\",\"text\"]", ModelModalities.Serialize([" Text ", "IMAGE", "text"]));
        Assert.Equal(["image", "text"], ModelModalities.Parse("[\"image\",\"text\"]"));
    }
}
