using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Providers;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Providers;
using ConduitLLM.Providers.Configuration;

using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace ConduitLLM.Tests.Providers;

public class ProviderDefaultsRegistryTests
{
    [Fact]
    public void ClientCreators_Should_Cover_Exactly_The_Configurable_ProviderTypes()
    {
        ClientCreatorRegistry.GetSupportedProviderTypes()
            .OrderBy(x => x)
            .Should()
            .Equal(ProviderTypeCatalog.ConfigurableTypes.OrderBy(x => x));
    }

    [Theory]
    [InlineData(ProviderType.Ultravox)]
    [InlineData(ProviderType.ElevenLabs)]
    [InlineData((ProviderType)999)]
    public void Unimplemented_Types_Should_Not_Be_Configurable_Or_Registered(ProviderType providerType)
    {
        ProviderTypeCatalog.IsConfigurable(providerType).Should().BeFalse();
        ProviderTypeCatalog.ConfigurableTypes.Should().NotContain(providerType);
        ClientCreatorRegistry.IsSupported(providerType).Should().BeFalse();
        ProviderAdapterDefaultsRegistry.TryGet(providerType, out _).Should().BeFalse();
        ProviderConfigurationRegistry.TryGetConfiguration(providerType, out _).Should().BeFalse();
    }

    [Fact]
    public void AdapterDefaults_Should_Cover_Every_Configurable_ProviderType()
    {
        ProviderAdapterDefaultsRegistry.GetRegisteredProviderTypes()
            .OrderBy(x => x)
            .Should()
            .Equal(ProviderTypeCatalog.ConfigurableTypes.OrderBy(x => x));
    }

    [Fact]
    public void ProviderConfigurations_Should_Cover_Every_Configurable_ProviderType_And_Use_Canonical_Urls()
    {
        ProviderConfigurationRegistry.GetRegisteredProviderTypes()
            .OrderBy(x => x)
            .Should()
            .Equal(ProviderTypeCatalog.ConfigurableTypes.OrderBy(x => x));

        foreach (var providerType in ProviderTypeCatalog.ConfigurableTypes)
        {
            ProviderConfigurationRegistry.GetDefaultBaseUrl(providerType)
                .Should()
                .Be(ProviderAdapterDefaultsRegistry.GetRequired(providerType).DefaultBaseUrl);
        }
    }

    [Fact]
    public void Unknown_Should_Be_A_Report_Only_Sentinel()
    {
        ProviderTypeCatalog.IsConfigurable(ProviderType.Unknown).Should().BeFalse();
        ProviderAdapterDefaultsRegistry.TryGet(ProviderType.Unknown, out _).Should().BeFalse();
        ProviderConfigurationRegistry.TryGetConfiguration(ProviderType.Unknown, out _).Should().BeFalse();
    }

    [Fact]
    public void OpenAICompatible_Should_Keep_Explicit_Database_Url_Requirement()
    {
        ProviderAdapterDefaultsRegistry.GetRequired(ProviderType.OpenAICompatible)
            .RequiresBaseUrlOverride
            .Should()
            .BeTrue();

        var provider = new Provider
        {
            ProviderType = ProviderType.OpenAICompatible,
            ProviderName = "custom"
        };
        var credential = new ProviderKeyCredential { ApiKey = "key" };
        var factory = Mock.Of<IHttpClientFactory>();

        var action = () => new OpenAICompatibleGenericClient(
            provider,
            credential,
            "model",
            NullLogger<OpenAICompatibleGenericClient>.Instance,
            factory);

        action.Should().Throw<ConfigurationException>()
            .WithMessage("*Base URL is required*");

        provider.BaseUrl = "http://localhost:11434/v1";
        action.Should().NotThrow();
    }

    [Fact]
    public void ResolveBaseUrl_Should_Prefer_And_Normalize_Database_Override()
    {
        var provider = new Provider
        {
            ProviderType = ProviderType.Groq,
            BaseUrl = "https://proxy.example.test/groq/"
        };

        ProviderConfigurationRegistry.ResolveBaseUrl(provider)
            .Should()
            .Be("https://proxy.example.test/groq");
    }

    [Fact]
    public void Cloudflare_Should_Declare_A_Required_AccountId_UrlPathToken_Setting()
    {
        ProviderConfigurationRegistry.TryGetConfiguration(ProviderType.Cloudflare, out var config)
            .Should().BeTrue();

        var accountId = config!.Settings.Should().ContainSingle(s => s.Key == "account_id").Subject;
        accountId.Required.Should().BeTrue();
        accountId.Secret.Should().BeFalse();
        accountId.Binding.Should().Be(ProviderSettingBinding.UrlPathToken);
    }

    [Fact]
    public void ResolveBaseUrl_Should_Substitute_AccountId_Into_The_Cloudflare_Default_Url()
    {
        var provider = new Provider
        {
            ProviderType = ProviderType.Cloudflare,
            ProviderName = "cf",
            Settings = new Dictionary<string, string> { ["account_id"] = "0123456789abcdef0123456789abcdef" }
        };

        ProviderConfigurationRegistry.ResolveBaseUrl(provider)
            .Should()
            .Be("https://api.cloudflare.com/client/v4/accounts/0123456789abcdef0123456789abcdef/ai/v1");
    }

    [Fact]
    public void ResolveBaseUrl_Should_Throw_Actionable_Error_When_AccountId_Missing()
    {
        var provider = new Provider
        {
            ProviderType = ProviderType.Cloudflare,
            ProviderName = "cf"
        };

        var action = () => ProviderConfigurationRegistry.ResolveBaseUrl(provider);

        action.Should().Throw<ConfigurationException>()
            .WithMessage("*Account ID*");
    }

    [Fact]
    public void ResolveBaseUrl_Should_Honor_A_Raw_BaseUrl_With_The_AccountId_Already_Embedded()
    {
        // Dual-read: operators who configured the full URL before structured settings keep working.
        var provider = new Provider
        {
            ProviderType = ProviderType.Cloudflare,
            ProviderName = "cf",
            BaseUrl = "https://api.cloudflare.com/client/v4/accounts/deadbeefdeadbeefdeadbeefdeadbeef/ai/v1"
        };

        ProviderConfigurationRegistry.ResolveBaseUrl(provider)
            .Should()
            .Be("https://api.cloudflare.com/client/v4/accounts/deadbeefdeadbeefdeadbeefdeadbeef/ai/v1");
    }

    [Fact]
    public void ResolveBaseUrl_Should_Treat_Every_Remaining_BaseUrl_As_An_Operator_Override()
    {
        // The contract migration clears pre-settings URLs that only bake an account ID into the
        // default shape. After that migration, any stored value is deliberately operator-owned and
        // must not be reclassified by application code.
        var provider = new Provider
        {
            ProviderType = ProviderType.Cloudflare,
            ProviderName = "cf",
            BaseUrl = "https://api.cloudflare.com/client/v4/accounts/deadbeefdeadbeefdeadbeefdeadbeef/ai/v1",
            Settings = new Dictionary<string, string> { ["account_id"] = "0123456789abcdef0123456789abcdef" }
        };

        ProviderConfigurationRegistry.ResolveBaseUrl(provider)
            .Should()
            .Be("https://api.cloudflare.com/client/v4/accounts/deadbeefdeadbeefdeadbeefdeadbeef/ai/v1");
    }

    [Fact]
    public void ResolveBaseUrl_Should_Keep_A_Genuinely_Custom_BaseUrl_Over_Settings()
    {
        // A URL pointing somewhere other than the registered default is a deliberate override
        // (a proxy or private gateway) and carries routing the settings cannot reconstruct.
        var provider = new Provider
        {
            ProviderType = ProviderType.Cloudflare,
            ProviderName = "cf",
            BaseUrl = "https://gateway.example.test/cf-proxy/ai/v1",
            Settings = new Dictionary<string, string> { ["account_id"] = "0123456789abcdef0123456789abcdef" }
        };

        ProviderConfigurationRegistry.ResolveBaseUrl(provider)
            .Should()
            .Be("https://gateway.example.test/cf-proxy/ai/v1");
    }

    [Fact]
    public void OpenAI_Should_Declare_Organization_And_Project_As_Header_Settings()
    {
        ProviderConfigurationRegistry.TryGetConfiguration(ProviderType.OpenAI, out var config)
            .Should().BeTrue();

        var organization = config!.Settings.Should().ContainSingle(s => s.Key == "organization").Subject;
        organization.Binding.Should().Be(ProviderSettingBinding.Header);
        organization.EffectiveBindingTarget.Should().Be("OpenAI-Organization");
        organization.Required.Should().BeFalse();

        var project = config.Settings.Should().ContainSingle(s => s.Key == "project").Subject;
        project.Binding.Should().Be(ProviderSettingBinding.Header);
        project.EffectiveBindingTarget.Should().Be("OpenAI-Project");
        project.Required.Should().BeFalse();
    }

    [Fact]
    public void GetHeaderSettings_Should_Map_Supplied_Values_Onto_Their_Header_Names()
    {
        var headers = ProviderConfigurationRegistry.GetHeaderSettings(
            ProviderType.OpenAI,
            new Dictionary<string, string>
            {
                ["organization"] = " org-acme ",
                ["project"] = "proj_widgets"
            });

        headers.Should().BeEquivalentTo(new[]
        {
            new KeyValuePair<string, string>("OpenAI-Organization", "org-acme"),
            new KeyValuePair<string, string>("OpenAI-Project", "proj_widgets")
        });
    }

    [Fact]
    public void GetHeaderSettings_Should_Skip_Blank_Values_And_NonHeader_Bindings()
    {
        ProviderConfigurationRegistry.GetHeaderSettings(
                ProviderType.OpenAI,
                new Dictionary<string, string> { ["organization"] = "   " })
            .Should().BeEmpty();

        // Cloudflare's account_id is a URL token, not a header, and must never leak into one.
        ProviderConfigurationRegistry.GetHeaderSettings(
                ProviderType.Cloudflare,
                new Dictionary<string, string> { ["account_id"] = "0123456789abcdef0123456789abcdef" })
            .Should().BeEmpty();
    }

    [Fact]
    public void ResolveBaseUrl_Should_Keep_A_Database_Override_For_Providers_Without_UrlTokenSettings()
    {
        // Database values are explicit operator overrides regardless of whether a provider declares
        // URL-path-token settings.
        var provider = new Provider
        {
            ProviderType = ProviderType.Groq,
            ProviderName = "groq",
            BaseUrl = "https://api.groq.com/openai/v2"
        };

        ProviderConfigurationRegistry.ResolveBaseUrl(provider)
            .Should()
            .Be("https://api.groq.com/openai/v2");
    }
}
