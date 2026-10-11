using System.Net;
using System.Security.Cryptography;

using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Providers.Mcp;
using ConduitLLM.Functions.Security;
using ConduitLLM.Functions.Services;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace ConduitLLM.Tests.Functions.Services;

public sealed class FunctionClientFactoryRuntimeStoreTests
{
    [Theory]
    [InlineData(false, false, FunctionProviderType.Exa)]
    [InlineData(false, true, FunctionProviderType.Exa)]
    [InlineData(true, false, FunctionProviderType.Exa)]
    [InlineData(true, true, FunctionProviderType.Exa)]
    [InlineData(false, false, FunctionProviderType.Tavily)]
    [InlineData(false, true, FunctionProviderType.Tavily)]
    [InlineData(true, false, FunctionProviderType.Tavily)]
    [InlineData(true, true, FunctionProviderType.Tavily)]
    public async Task ClientCreationPreservesCredentialScopeProviderChoiceAndHttpConfiguration(
        bool runtime, bool scoped, FunctionProviderType provider)
    {
        var fixture = new Fixture(scoped);
        var factory = fixture.Factory(runtime);
        // Stored provider remains Exa and the configuration is disabled. The caller
        // still chooses the client type, matching the existing factory's semantics.
        var first = await factory.GetClientAsync(provider, Fixture.ConfigurationId);
        Assert.Equal(provider, first.ProviderType);
        Assert.True((await first.VerifyAuthenticationAsync()).IsSuccess);
        var second = await factory.GetClientAsync(provider, Fixture.ConfigurationId);
        Assert.NotSame(first, second);
        Assert.True((await second.VerifyAuthenticationAsync()).IsSuccess);
        Assert.Equal(2, fixture.Http.Requests.Count);
        foreach (var request in fixture.Http.Requests)
        {
            Assert.Equal(new Uri(scoped ? "https://credential.test/search" : "https://configuration.test/search"), request.Uri);
            Assert.Equal(TimeSpan.FromSeconds(41), request.Timeout);
            Assert.Equal(provider == FunctionProviderType.Exa ? "ExaFunctionClient" : "TavilyFunctionClient", request.ClientName);
            Assert.Equal(scoped ? "plain-scoped" : "plain-global", request.Secret);
            Assert.Equal("application/json", request.Accept);
            Assert.Equal("ConduitLLM-Functions", request.UserAgent);
        }
        fixture.Protector.Verify(value => value.Reveal(scoped ? "opaque-scoped" : "opaque-global"), Times.Exactly(2));
        fixture.Protector.Verify(value => value.Reveal("opaque-disabled"), Times.Never);
        Assert.All(fixture.ReturnedCredentials.SelectMany(rows => rows).Where(row => row.Id != (scoped ? 3 : 1)),
            row => Assert.StartsWith("opaque-", row.ApiKey));
        fixture.AssertReads(runtime, 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingConfigurationAndDisabledCredentialsRetainExistingFailures(bool runtime)
    {
        var fixture = new Fixture(true);
        fixture.Configuration = null;
        var factory = fixture.Factory(runtime);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.GetClientAsync(FunctionProviderType.Exa, Fixture.ConfigurationId));
        Assert.Equal($"Function configuration {Fixture.ConfigurationId} not found", missing.Message);
        fixture.AssertCredentialReads(runtime, 0);
        fixture.Configuration = Fixture.Config();
        fixture.DisableCredentials = true;
        var credentials = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.GetClientAsync(FunctionProviderType.Exa, Fixture.ConfigurationId));
        Assert.Contains("No enabled credentials", credentials.Message);
        fixture.Protector.Verify(value => value.Reveal(It.IsAny<string>()), Times.Never);
        Assert.Empty(fixture.Http.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptScopedSecretFailsWithoutGlobalFallbackOrHttp(bool runtime)
    {
        var fixture = new Fixture(true);
        fixture.Protector.Setup(value => value.Reveal("opaque-scoped")).Throws(new CryptographicException("invalid protected secret"));
        await Assert.ThrowsAsync<CryptographicException>(() => fixture.Factory(runtime).GetClientAsync(FunctionProviderType.Exa, Fixture.ConfigurationId));
        fixture.Protector.Verify(value => value.Reveal("opaque-global"), Times.Never);
        Assert.Empty(fixture.Http.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownProviderFailsAfterExistingCredentialResolution(bool runtime)
    {
        var fixture = new Fixture(true);
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Factory(runtime).GetClientAsync((FunctionProviderType)197, Fixture.ConfigurationId));
        fixture.Protector.Verify(value => value.Reveal("opaque-scoped"), Times.Once);
        fixture.AssertReads(runtime, 1);
        Assert.Empty(fixture.Http.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpSettingsRetainAllowlistBeforeOpeningTransport(bool runtime)
    {
        var fixture = new Fixture(true);
        fixture.Configuration.ProviderSettings = """{"allowedTools":["allowed"],"allowPrivateNetwork":false}""";
        var client = Assert.IsType<McpFunctionClient>(await fixture.Factory(runtime).GetClientAsync(FunctionProviderType.Mcp, Fixture.ConfigurationId));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ExecuteAsync(new Dictionary<string, object>
            { [McpReservedParameterKeys.ToolName] = "forbidden" }));
        Assert.Empty(fixture.Http.Requests);
    }

    [Fact]
    public void OriginalPublicConstructorRemainsAvailable()
    {
        Assert.NotNull(typeof(FunctionClientFactory).GetConstructor(
            [typeof(IFunctionConfigurationRepository), typeof(IFunctionCredentialRepository),
                typeof(IFunctionCredentialProtector), typeof(ILoggerFactory), typeof(IHttpClientFactory)]));
    }

    private sealed class Fixture
    {
        internal const int ConfigurationId = 9027;
        internal FunctionConfiguration Configuration = Config();
        internal bool DisableCredentials;
        internal readonly Mock<IFunctionExecutionConfigurationStore> Store = new(MockBehavior.Strict);
        internal readonly Mock<IFunctionConfigurationRepository> Configurations = new(MockBehavior.Strict);
        internal readonly Mock<IFunctionCredentialRepository> Credentials = new(MockBehavior.Strict);
        internal readonly Mock<IFunctionCredentialProtector> Protector = new(MockBehavior.Strict);
        internal readonly RecordingHttpFactory Http = new();
        internal readonly List<List<FunctionCredential>> ReturnedCredentials = [];

        internal Fixture(bool scoped)
        {
            Store.Setup(value => value.GetConfigurationAsync(ConfigurationId, CancellationToken.None)).ReturnsAsync(() => Configuration);
            Configurations.Setup(value => value.GetByIdAsync(ConfigurationId, CancellationToken.None)).ReturnsAsync(() => Configuration);
            Store.Setup(value => value.ListCredentialsForProviderAsync(FunctionProviderType.Exa, CancellationToken.None)).ReturnsAsync(() => Rows(scoped));
            Credentials.Setup(value => value.GetByProviderTypeAsync(FunctionProviderType.Exa, CancellationToken.None)).ReturnsAsync(() => Rows(scoped));
            Protector.Setup(value => value.Reveal(It.IsAny<string>())).Returns<string>(value => value.Replace("opaque-", "plain-", StringComparison.Ordinal));
        }

        internal FunctionClientFactory Factory(bool runtime) => runtime
            ? new FunctionClientFactory(Store.Object, Protector.Object, NullLoggerFactory.Instance, Http)
            : new FunctionClientFactory(Configurations.Object, Credentials.Object, Protector.Object, NullLoggerFactory.Instance, Http);

        private List<FunctionCredential> Rows(bool scoped)
        {
            var rows = new List<FunctionCredential>
            {
                new() { Id = 5, FunctionConfigurationId = ConfigurationId, IsPrimary = true, IsEnabled = false, ApiKey = "opaque-disabled" },
                new() { Id = 1, IsPrimary = true, IsEnabled = !DisableCredentials, ApiKey = "opaque-global" },
                new() { Id = 2, FunctionConfigurationId = 99, IsPrimary = true, IsEnabled = !DisableCredentials, ApiKey = "opaque-other" }
            };
            if (scoped)
            {
                rows.Add(new FunctionCredential { Id = 4, FunctionConfigurationId = ConfigurationId, IsEnabled = !DisableCredentials,
                    ApiKey = "opaque-secondary" });
                rows.Add(new FunctionCredential { Id = 3, FunctionConfigurationId = ConfigurationId, IsPrimary = true,
                    IsEnabled = !DisableCredentials, ApiKey = "opaque-scoped", BaseUrl = "https://credential.test/" });
            }
            ReturnedCredentials.Add(rows);
            return rows;
        }

        internal void AssertReads(bool runtime, int count)
        {
            if (runtime)
            {
                Store.Verify(value => value.GetConfigurationAsync(ConfigurationId, CancellationToken.None), Times.Exactly(count));
                Configurations.VerifyNoOtherCalls();
                Credentials.VerifyNoOtherCalls();
            }
            else
            {
                Configurations.Verify(value => value.GetByIdAsync(ConfigurationId, CancellationToken.None), Times.Exactly(count));
                Store.VerifyNoOtherCalls();
            }
            AssertCredentialReads(runtime, count);
        }

        internal void AssertCredentialReads(bool runtime, int count)
        {
            if (runtime) Store.Verify(value => value.ListCredentialsForProviderAsync(FunctionProviderType.Exa, CancellationToken.None), Times.Exactly(count));
            else Credentials.Verify(value => value.GetByProviderTypeAsync(FunctionProviderType.Exa, CancellationToken.None), Times.Exactly(count));
        }

        internal static FunctionConfiguration Config() => new()
        {
            Id = ConfigurationId, ConfigurationName = "O'Hare 雪", ProviderType = FunctionProviderType.Exa,
            Purpose = FunctionPurpose.Search, IsEnabled = false, BaseUrl = "https://configuration.test/", TimeoutSeconds = 41
        };
    }

    private sealed record RequestSnapshot(Uri Uri, TimeSpan Timeout, string ClientName, string Secret, string Accept, string UserAgent);

    private sealed class RecordingHttpFactory : IHttpClientFactory
    {
        internal readonly List<RequestSnapshot> Requests = [];
        public HttpClient CreateClient(string name)
        {
            HttpClient client = null;
            client = new HttpClient(new RecordingHandler(request => Requests.Add(new RequestSnapshot(
                request.RequestUri!, client.Timeout, name,
                request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : request.Headers.Authorization!.Parameter!,
                request.Headers.Accept.ToString(), request.Headers.UserAgent.ToString()))));
            return client;
        }
    }

    private sealed class RecordingHandler(Action<HttpRequestMessage> record) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            record(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
