using System.Net;

using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Exceptions;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Providers.Exa;
using ConduitLLM.Functions.Providers.Tavily;
using ConduitLLM.Functions.Security;
using ConduitLLM.Functions.Services;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace ConduitLLM.Tests.Functions.Providers;

public sealed class FunctionSingleDispatchTransportTests
{
    [Theory]
    [InlineData(FunctionProviderType.Exa, false)]
    [InlineData(FunctionProviderType.Exa, true)]
    [InlineData(FunctionProviderType.Tavily, false)]
    [InlineData(FunctionProviderType.Tavily, true)]
    public async Task KeyedProviderCallsSelectTheSingleDispatchNamedTransport(FunctionProviderType providerType, bool keyed)
    {
        var configuration = new FunctionConfiguration
        {
            Id = 7, ConfigurationName = "Single dispatch", ProviderType = providerType,
            Purpose = FunctionPurpose.Search, IsEnabled = true
        };
        var credential = new FunctionCredential
        {
            ProviderType = providerType, ApiKey = "test-secret", IsEnabled = true
        };
        var httpFactory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"requestId":"single-dispatch","query":"test","results":[]}""")
        };
        var requestedClients = new List<string>();
        httpFactory.Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns((string name) =>
            {
                requestedClients.Add(name);
                return new HttpClient(new ResponseHandler(response));
            });
        IFunctionClient client = providerType == FunctionProviderType.Exa
            ? new ExaClient(configuration, credential, httpFactory.Object, NullLogger<ExaClient>.Instance)
            : new TavilyClient(configuration, credential, httpFactory.Object, NullLogger<TavilyClient>.Instance);
        var clients = new Mock<IFunctionClientFactory>();
        clients.Setup(factory => factory.GetClientAsync(providerType, 7)).ReturnsAsync(client);
        var configurations = new Mock<IFunctionConfigurationRepository>();
        configurations.Setup(repository => repository.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(configuration);
        var credentials = new Mock<IFunctionCredentialRepository>();
        credentials.Setup(repository => repository.GetByProviderTypeAsync(providerType, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FunctionCredential> { credential });
        var executions = new Mock<IFunctionExecutionRepository>();
        executions.Setup(repository => repository.UpdateAsync(It.IsAny<FunctionExecution>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var costs = new Mock<IFunctionCostCalculationService>();
        costs.Setup(calculator => calculator.EstimateCostAsync(7, It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1m);
        costs.Setup(calculator => calculator.CalculateCostAsync(7, It.IsAny<FunctionExecutionUsage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1m);
        var protector = new Mock<IFunctionCredentialProtector>();
        protector.Setup(value => value.Reveal("test-secret")).Returns("test-secret");
        var claims = new Mock<IFunctionExecutionIdempotencyStore>(MockBehavior.Strict);
        claims.Setup(store => store.ClaimAsync(11, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int key, string hash, string fingerprint, CancellationToken _) =>
                new FunctionExecutionClaim(key, hash, fingerprint, Guid.NewGuid(), 3, DateTime.UtcNow,
                    FunctionExecutionClaimState.Claimed, null, null, IsOwner: true));
        claims.Setup(store => store.MarkDispatchedAsync(It.IsAny<FunctionExecutionClaim>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        claims.Setup(store => store.CompleteAsync(It.IsAny<FunctionExecutionClaim>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        claims.Setup(store => store.SettleBillingAsync(It.IsAny<FunctionExecutionClaim>(), 1m, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = FunctionExecutionService.CreateWithIdempotency(
            configurations.Object, credentials.Object, executions.Object, costs.Object, clients.Object,
            protector.Object, NullLogger<FunctionExecutionService>.Instance, claims.Object);

        var result = await service.ExecuteAsync(7, 11, new Dictionary<string, object> { ["query"] = "test" },
            idempotencyKey: keyed ? "single-dispatch" : null);

        Assert.Equal(ExecutionState.Completed, result.State);
        Assert.Equal(keyed, result.IsIdempotencyBillingSettled);
        Assert.Equal($"{client.ProviderName}FunctionClient{(keyed ? ".SingleDispatch" : "")}", Assert.Single(requestedClients));
        claims.Verify(store => store.MarkDispatchedAsync(It.IsAny<FunctionExecutionClaim>(), It.IsAny<CancellationToken>()),
            keyed ? Times.Once() : Times.Never());
    }

    [Theory]
    [InlineData(FunctionProviderType.Exa, false, true)]
    [InlineData(FunctionProviderType.Exa, false, false)]
    [InlineData(FunctionProviderType.Exa, true, true)]
    [InlineData(FunctionProviderType.Exa, true, false)]
    [InlineData(FunctionProviderType.Tavily, false, true)]
    [InlineData(FunctionProviderType.Tavily, false, false)]
    public async Task ProviderDisposesResponseOnSuccessAndFailure(
        FunctionProviderType providerType, bool contents, bool success)
    {
        using var content = new DisposalTrackingContent(success
            ? """{"requestId":"dispose-response","query":"test","results":[]}"""
            : """{"error":"invalid request"}""");
        using var response = new HttpResponseMessage(success ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
        {
            Content = content
        };
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(new ResponseHandler(response)));
        var configuration = new FunctionConfiguration { Id = 7, ConfigurationName = "Response disposal", ProviderType = providerType };
        var credential = new FunctionCredential { ProviderType = providerType, ApiKey = "test-secret" };
        IFunctionClient client = providerType == FunctionProviderType.Exa
            ? new ExaClient(configuration, credential, httpFactory.Object, NullLogger<ExaClient>.Instance)
            : new TavilyClient(configuration, credential, httpFactory.Object, NullLogger<TavilyClient>.Instance);
        var parameters = contents
            ? new Dictionary<string, object> { ["urls"] = new List<string> { "https://example.com" } }
            : new Dictionary<string, object> { ["query"] = "test" };

        if (success)
            Assert.True((await client.ExecuteAsync(parameters)).IsSuccess);
        else
            await Assert.ThrowsAsync<FunctionCommunicationException>(() => client.ExecuteAsync(parameters));

        // Assert before this test's fallback cleanup; HttpClient does not own its response.
        Assert.True(content.IsDisposed);
    }

    private sealed class ResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class DisposalTrackingContent(string value) : StringContent(value)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing) IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
