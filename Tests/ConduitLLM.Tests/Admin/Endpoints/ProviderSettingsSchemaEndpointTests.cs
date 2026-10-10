using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Providers;
using ConduitLLM.Providers.Configuration;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http.HttpResults;

namespace ConduitLLM.Tests.Admin.Endpoints;

/// <summary>
/// Covers the settings-schema projection that lets administrative UIs render the complete provider
/// catalog straight from backend registries instead of hand-maintained client-side copies.
/// </summary>
public class ProviderSettingsSchemaEndpointTests
{
    private static IReadOnlyList<ProviderSettingsSchemaDto> GetSchema()
    {
        var result = ProviderCredentialsEndpoints.GetProviderSettingsSchema();
        return result.Should().BeOfType<Ok<ProviderSettingsSchemaDto[]>>().Subject.Value!;
    }

    [Fact]
    public void Schema_Should_Project_Every_Configurable_Provider_From_The_Registry()
    {
        var schema = GetSchema();

        schema.Select(entry => entry.ProviderType)
            .Should().Equal(ProviderTypeCatalog.ConfigurableTypes);

        foreach (var providerType in ProviderTypeCatalog.ConfigurableTypes)
        {
            var configuration = ProviderConfigurationRegistry.GetConfiguration(providerType);
            configuration.Should().NotBeNull($"{providerType} must have a provider configuration");
            var adapterDefaults = ProviderAdapterDefaultsRegistry.GetRequired(providerType);
            var published = schema.Should()
                .ContainSingle(entry => entry.ProviderType == providerType).Subject;

            published.ProviderTypeId.Should().Be((int)providerType);
            published.DisplayName.Should().Be(configuration!.DisplayName);
            published.DisplayName.Should().NotBeNullOrWhiteSpace();
            published.RequiresApiKey.Should().Be(configuration.AuthenticationStrategy.RequiresApiKey);
            published.RequiresEndpoint.Should().Be(adapterDefaults.RequiresBaseUrlOverride);
            published.SupportsCustomEndpoint.Should().BeTrue();
            published.HelpUrl.Should().Be(configuration.HelpUrl);
            published.HelpText.Should().Be(configuration.HelpText);
            published.Settings.Select(setting => setting.Key)
                .Should().Equal(configuration.Settings.Select(definition => definition.Key));
        }
    }

    [Fact]
    public void Schema_Should_Exclude_ProviderTypes_Without_Adapters()
    {
        GetSchema().Select(entry => entry.ProviderType).Should()
            .NotContain(new[] { ProviderType.Unknown, ProviderType.Ultravox, ProviderType.ElevenLabs });
    }

    [Fact]
    public void Schema_Should_Publish_Required_Endpoint_Metadata_For_OpenAiCompatible()
    {
        var openAiCompatible = GetSchema()
            .Should().ContainSingle(entry => entry.ProviderType == ProviderType.OpenAICompatible).Subject;

        openAiCompatible.DisplayName.Should().Be("OpenAI Compatible");
        openAiCompatible.RequiresApiKey.Should().BeTrue();
        openAiCompatible.RequiresEndpoint.Should().BeTrue();
        openAiCompatible.SupportsCustomEndpoint.Should().BeTrue();
        openAiCompatible.HelpText.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Schema_Should_Publish_Registry_Defaults_For_Azure()
    {
        var azure = GetSchema()
            .Should().ContainSingle(entry => entry.ProviderType == ProviderType.Azure).Subject;

        var apiVersion = azure.Settings
            .Should().ContainSingle(setting => setting.Key == "api_version").Subject;

        apiVersion.DefaultValue.Should().Be(ProviderConfigurationRegistry.AzureDefaultApiVersion);
    }

    [Fact]
    public void Schema_Should_Publish_The_Cloudflare_AccountId_Field_With_Its_Label_And_Validation()
    {
        var cloudflare = GetSchema()
            .Should().ContainSingle(entry => entry.ProviderType == ProviderType.Cloudflare).Subject;

        var accountId = cloudflare.Settings.Should().ContainSingle(setting => setting.Key == "account_id").Subject;
        accountId.Label.Should().Be("Account ID");
        accountId.Required.Should().BeTrue();
        accountId.Secret.Should().BeFalse();
        accountId.ValidationRegex.Should().Be("^[0-9a-fA-F]{32}$");
        accountId.HelpText.Should().NotBeNullOrWhiteSpace();
        accountId.Placeholder.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Schema_Should_Publish_Renderable_Fields_With_Unique_Keys()
    {
        // The key is both the storage key and the form field path, so a blank or duplicated key
        // would silently collapse two operator inputs into one stored value.
        foreach (var entry in GetSchema())
        {
            entry.Settings.Should().OnlyContain(setting =>
                !string.IsNullOrWhiteSpace(setting.Key) && !string.IsNullOrWhiteSpace(setting.Label));
            entry.Settings.Select(setting => setting.Key)
                .Should().OnlyHaveUniqueItems($"{entry.ProviderType} settings must not collide");
        }
    }
}
