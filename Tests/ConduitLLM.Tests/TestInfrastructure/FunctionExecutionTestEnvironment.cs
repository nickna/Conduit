using System.Net;
using System.Text;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Providers.Exa;
using ConduitLLM.Functions.Security;
using ConduitLLM.Functions.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ConduitLLM.Tests.TestInfrastructure;

/// <summary>Runs function execution with production services, repositories and a controlled provider response.</summary>
internal sealed class FunctionExecutionTestEnvironment : IAsyncDisposable
{
    public const string Diagnostic = """{"error":{"message":"Exa upstream private diagnostic","code":"private_request"}}""";
    public SqliteTestDatabase Database { get; } = new();
    public Mock<IFunctionCostCalculationService> Costs { get; } = new();
    public Mock<IFunctionClientFactory> Clients { get; } = new();
    public int ProviderRequests { get; private set; }

    private readonly IFunctionCredentialProtector _protector = new FunctionCredentialProtector(
        new EphemeralDataProtectionProvider(), NullLogger<FunctionCredentialProtector>.Instance);

    public static async Task<FunctionExecutionTestEnvironment> CreateAsync(HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        var environment = new FunctionExecutionTestEnvironment();
        var configuration = new FunctionConfiguration
        {
            Id = 7,
            ConfigurationName = "search",
            ProviderType = FunctionProviderType.Exa,
            Purpose = FunctionPurpose.Search,
            IsEnabled = true
        };
        var credential = new FunctionCredential
        {
            ProviderType = FunctionProviderType.Exa,
            ApiKey = environment._protector.Protect("exa-test"),
            IsEnabled = true,
            IsPrimary = true
        };
        await environment.Database.SeedAsync(async db =>
        {
            db.VirtualKeyGroups.Add(new VirtualKeyGroup { Id = 1, GroupName = "function-test", Balance = 10m });
            db.VirtualKeys.Add(new VirtualKey { Id = 1, KeyName = "function-test", KeyHash = "function-test", VirtualKeyGroupId = 1 });
            db.FunctionConfigurations.Add(configuration);
            db.FunctionCredentials.Add(credential);
            await db.SaveChangesAsync();
        });
        environment.Costs.Setup(service => service.EstimateCostAsync(
                7, It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0.25m);
        var provider = new ExaClient(configuration, credential,
            new ProviderHttpClientFactory(environment, status), NullLogger<ExaClient>.Instance);
        environment.Clients.Setup(factory => factory.GetClientAsync(FunctionProviderType.Exa, 7))
            .ReturnsAsync(provider);
        return environment;
    }

    public FunctionConfigurationRepository CreateConfigurationRepository() =>
        new(Database.CreateDbContextFactory(), NullLogger<FunctionConfigurationRepository>.Instance);

    public FunctionExecutionRepository CreateExecutionRepository() =>
        new(Database.CreateDbContextFactory(), NullLogger<FunctionExecutionRepository>.Instance);

    public FunctionExecutionService CreateService() => new(
        CreateConfigurationRepository(),
        new FunctionCredentialRepository(Database.CreateDbContextFactory(), NullLogger<FunctionCredentialRepository>.Instance),
        CreateExecutionRepository(), Costs.Object, Clients.Object, _protector,
        NullLogger<FunctionExecutionService>.Instance);

    public ValueTask DisposeAsync() => Database.DisposeAsync();

    private sealed class ProviderHttpClientFactory(FunctionExecutionTestEnvironment environment, HttpStatusCode status)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ProviderHttpHandler(environment, status));
    }

    private sealed class ProviderHttpHandler(FunctionExecutionTestEnvironment environment, HttpStatusCode status)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("exa-test", Assert.Single(request.Headers.GetValues("x-api-key")));
            environment.ProviderRequests++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(Diagnostic, Encoding.UTF8, "application/json")
            });
        }
    }
}
