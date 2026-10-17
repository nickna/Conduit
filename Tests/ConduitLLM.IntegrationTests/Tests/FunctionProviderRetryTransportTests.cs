using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

using ConduitLLM.Core.Extensions;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Exceptions;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Providers.Exa;
using ConduitLLM.Functions.Providers.Tavily;
using ConduitLLM.Functions.Security;
using ConduitLLM.Functions.Services;
using ConduitLLM.IntegrationTests.Infrastructure;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

using Xunit.Abstractions;

namespace ConduitLLM.IntegrationTests.Tests;

[Trait("Category", "Integration")]
[Trait("Component", "Functions")]
public sealed class FunctionProviderRetryTransportTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(FunctionProviderType.Exa, false)]
    [InlineData(FunctionProviderType.Tavily, false)]
    [InlineData(FunctionProviderType.Exa, true)]
    [InlineData(FunctionProviderType.Tavily, true)]
    public async Task AcceptedOperationWithLostResponseIsNotHiddenlyRedispatched(FunctionProviderType providerType, bool keyed)
    {
        await WithReceiverAsync(async receiver =>
        {
            var methods = DropAfterAcceptance(receiver);
            using var registration = new ServiceCollection().AddLogging().AddSharedHttpClients().BuildServiceProvider();
            var http = new RecordingHttpFactory(registration.GetRequiredService<IHttpClientFactory>());
            var configuration = Configuration(providerType, receiver.Url);
            var credential = Credential(providerType);
            var client = Client(configuration, credential, http);
            var configurations = new Mock<IFunctionConfigurationRepository>(MockBehavior.Strict);
            configurations.Setup(value => value.GetByIdAsync(7, It.IsAny<CancellationToken>())).ReturnsAsync(configuration);
            var credentials = new Mock<IFunctionCredentialRepository>(MockBehavior.Strict);
            credentials.Setup(value => value.GetByProviderTypeAsync(providerType, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<FunctionCredential> { credential });
            var executions = new Mock<IFunctionExecutionRepository>(MockBehavior.Strict);
            FunctionExecution? recorded = null;
            executions.Setup(value => value.CreateAsync(It.IsAny<FunctionExecution>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((FunctionExecution execution, CancellationToken _) => { recorded = execution; return execution.Id; });
            executions.Setup(value => value.UpdateAsync(It.IsAny<FunctionExecution>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((FunctionExecution execution, CancellationToken _) => { recorded = execution; return true; });
            var costs = new Mock<IFunctionCostCalculationService>(MockBehavior.Strict);
            costs.Setup(value => value.EstimateCostAsync(7, It.IsAny<Dictionary<string, object>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(0.25m);
            FunctionExecutionUsage? calculatedUsage = null;
            costs.Setup(value => value.CalculateCostAsync(7, It.IsAny<FunctionExecutionUsage>(), It.IsAny<CancellationToken>()))
                .Callback<int, FunctionExecutionUsage, CancellationToken>((_, usage, _) => calculatedUsage = usage)
                // This is a local pricing-policy result, not a measured external provider charge.
                .ReturnsAsync(0.125m);
            var clients = new Mock<IFunctionClientFactory>(MockBehavior.Strict);
            clients.Setup(value => value.GetClientAsync(providerType, 7)).ReturnsAsync(client);
            var protector = new Mock<IFunctionCredentialProtector>(MockBehavior.Strict);
            protector.Setup(value => value.Reveal("fixture-key")).Returns("fixture-key");
            var claims = new Mock<IFunctionExecutionIdempotencyStore>(MockBehavior.Strict);
            var executionId = Guid.NewGuid();
            var uncertain = false;
            claims.Setup(value => value.ClaimAsync(11, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((int owner, string hash, string fingerprint, CancellationToken _) =>
                    new FunctionExecutionClaim(owner, hash, fingerprint, executionId, 1, DateTime.UtcNow,
                        uncertain ? FunctionExecutionClaimState.Uncertain : FunctionExecutionClaimState.Claimed,
                        null, null, IsOwner: !uncertain));
            claims.Setup(value => value.MarkDispatchedAsync(It.IsAny<FunctionExecutionClaim>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            claims.Setup(value => value.MarkUncertainAsync(It.IsAny<FunctionExecutionClaim>(), It.IsAny<CancellationToken>()))
                .Callback(() => uncertain = true).ReturnsAsync(true);
            var service = FunctionExecutionService.CreateWithIdempotency(configurations.Object, credentials.Object,
                executions.Object, costs.Object, clients.Object, protector.Object,
                NullLogger<FunctionExecutionService>.Instance, claims.Object);
            var query = "accepted-" + Guid.NewGuid().ToString("N");
            var parameters = new Dictionary<string, object> { ["query"] = query };
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));

            if (keyed)
            {
                var error = await Assert.ThrowsAsync<FunctionIdempotencyException>(() => service.ExecuteAsync(
                    7, 11, parameters, idempotencyKey: "same-owned-operation", cancellationToken: deadline.Token));
                AssertUnknown(error);
                // The claim boundary is controlled here; its PostgreSQL durability is
                // covered separately. A repeated uncertain claim must not reach HTTP.
                AssertUnknown(await Assert.ThrowsAsync<FunctionIdempotencyException>(() => service.ExecuteAsync(
                    7, 11, parameters, idempotencyKey: "same-owned-operation", cancellationToken: deadline.Token)));
                Assert.Null(calculatedUsage);
                claims.Verify(value => value.MarkDispatchedAsync(It.IsAny<FunctionExecutionClaim>(), It.IsAny<CancellationToken>()), Times.Once);
                claims.Verify(value => value.MarkUncertainAsync(It.IsAny<FunctionExecutionClaim>(), It.IsAny<CancellationToken>()), Times.Once);
                claims.Verify(value => value.CompleteAsync(It.IsAny<FunctionExecutionClaim>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
                claims.Verify(value => value.SettleBillingAsync(It.IsAny<FunctionExecutionClaim>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()), Times.Never);
            }
            else
            {
                var result = await service.ExecuteAsync(7, 11, parameters, cancellationToken: deadline.Token);
                Assert.Same(recorded, result);
                Assert.Equal(ExecutionState.Failed, result.State);
                Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
                Assert.Null(result.ResponseJson);
                Assert.False(result.IsIdempotencyBillingSettled);
                Assert.Equal(0.25m, result.EstimatedCost);
                Assert.Equal(0.125m, result.ActualCost);
                Assert.NotNull(calculatedUsage);
                Assert.Equal(0, calculatedUsage.ResultCount);
                Assert.Null(calculatedUsage.ProviderReportedCost);
                Assert.NotNull(calculatedUsage.ExecutionDuration);
                costs.Verify(value => value.CalculateCostAsync(7, It.IsAny<FunctionExecutionUsage>(), It.IsAny<CancellationToken>()), Times.Once);
                claims.VerifyNoOtherCalls();
            }

            Assert.NotNull(recorded);
            Assert.Equal(ExecutionState.Failed, recorded.State);
            Assert.False(string.IsNullOrWhiteSpace(recorded.ErrorMessage));
            Assert.Null(recorded.ResponseJson);
            clients.Verify(value => value.GetClientAsync(providerType, 7), Times.Once);
            executions.Verify(value => value.CreateAsync(It.IsAny<FunctionExecution>(), It.IsAny<CancellationToken>()), Times.Once);
            executions.Verify(value => value.UpdateAsync(It.IsAny<FunctionExecution>(), It.IsAny<CancellationToken>()), Times.Once);
            var accepted = Assert.Single(receiver.Posts);
            Assert.Equal("POST", Assert.Single(methods));
            using var body = JsonDocument.Parse(accepted.Json);
            Assert.Equal(query, body.RootElement.GetProperty("query").GetString());
            Assert.Equal(providerType == FunctionProviderType.Exa
                ? (keyed ? "ExaFunctionClient.SingleDispatch" : "ExaFunctionClient")
                : (keyed ? "TavilyFunctionClient.SingleDispatch" : "TavilyFunctionClient"), Assert.Single(http.Names));
            output.WriteLine($"FUNCTION_ACCEPTED_DROP provider={providerType}; keyed={keyed}; acceptedPostCount={receiver.Posts.Count}; result=failed; claimUncertain={uncertain}; costCalculated={calculatedUsage is not null}");

            void AssertUnknown(FunctionIdempotencyException error)
            {
                Assert.Equal(409, error.StatusCode);
                Assert.Equal("idempotency_outcome_unknown", error.Code);
                Assert.Equal(executionId, error.ExecutionId);
                Assert.Contains("uncertain outcome", error.Message, StringComparison.Ordinal);
                Assert.Contains("will not be dispatched again", error.Message, StringComparison.Ordinal);
            }
        });
    }

    [Theory]
    [InlineData(FunctionProviderType.Exa)]
    [InlineData(FunctionProviderType.Tavily)]
    public async Task ActualCredentialSearchPostIsNotRetriedAfterItsResponseIsLost(FunctionProviderType providerType)
    {
        await WithReceiverAsync(async receiver =>
        {
            var methods = DropAfterAcceptance(receiver);
            using var registration = new ServiceCollection().AddLogging().AddSharedHttpClients().BuildServiceProvider();
            var client = Client(Configuration(providerType, receiver.Url), Credential(providerType),
                registration.GetRequiredService<IHttpClientFactory>());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var result = await client.VerifyAuthenticationAsync(cancellationToken: deadline.Token);
            Assert.False(result.IsSuccess);
            Assert.StartsWith("Network error:", result.Message, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(result.Details));
            Assert.Equal("POST", Assert.Single(methods));
            using var accepted = JsonDocument.Parse(Assert.Single(receiver.Posts).Json);
            Assert.Equal("test", accepted.RootElement.GetProperty("query").GetString());
            output.WriteLine($"FUNCTION_CREDENTIAL_DROP provider={providerType}; acceptedPostCount={receiver.Posts.Count}; authenticated=false");
        });
    }

    [Theory]
    [InlineData("ExaFunctionClient")]
    [InlineData("TavilyFunctionClient")]
    public async Task ExplicitReadOnlyGetRetainsTheRealNamedClientRetryPolicy(string clientName)
    {
        await WithReceiverAsync(async receiver =>
        {
            var methods = new ConcurrentQueue<string>();
            receiver.Respond = context =>
            {
                methods.Enqueue(context.Request.Method);
                context.Response.StatusCode = receiver.Posts.Count == 1 ? 503 : 200;
                return Task.CompletedTask;
            };
            using var registration = new ServiceCollection().AddLogging().AddSharedHttpClients().BuildServiceProvider();
            using var client = registration.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            // This is a synthetic safe GET control. Current provider credential
            // verification uses POST /search and is tested separately above.
            using var response = await client.GetAsync(receiver.Url + "/read-only-probe", deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(new[] { "GET", "GET" }, methods);
            Assert.Equal(2, receiver.Posts.Count);
            Assert.All(receiver.Posts, request => Assert.Empty(request.Json));
            output.WriteLine($"FUNCTION_SAFE_GET client={clientName}; requestCount={receiver.Posts.Count}; status=200");
        });
    }

    private static ConcurrentQueue<string> DropAfterAcceptance(WebhookReceiver receiver)
    {
        var methods = new ConcurrentQueue<string>();
        receiver.Respond = context =>
        {
            // WebhookReceiver has fully consumed and enqueued the request body
            // before invoking this callback. No response headers are sent.
            methods.Enqueue(context.Request.Method);
            context.Abort();
            return Task.CompletedTask;
        };
        return methods;
    }

    private static FunctionConfiguration Configuration(FunctionProviderType provider, string baseUrl) => new()
    {
        Id = 7, ConfigurationName = "owned loopback retry test", ProviderType = provider,
        Purpose = FunctionPurpose.Search, IsEnabled = true, BaseUrl = baseUrl, TimeoutSeconds = 30
    };

    private static FunctionCredential Credential(FunctionProviderType provider) => new()
        { ProviderType = provider, ApiKey = "fixture-key", IsEnabled = true, IsPrimary = true };

    private static IFunctionClient Client(FunctionConfiguration configuration, FunctionCredential credential, IHttpClientFactory http) =>
        configuration.ProviderType == FunctionProviderType.Exa
            ? new ExaClient(configuration, credential, http, NullLogger<ExaClient>.Instance)
            : new TavilyClient(configuration, credential, http, NullLogger<TavilyClient>.Instance);

    private static async Task WithReceiverAsync(Func<WebhookReceiver, Task> exercise)
    {
        var receiver = await WebhookReceiver.StartAsync();
        await IntegrationProofCleanup.RunAsync(() => exercise(receiver),
            () => receiver.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)),
            "Function provider transport regression and receiver cleanup failed.");
    }

    private sealed class RecordingHttpFactory(IHttpClientFactory inner) : IHttpClientFactory
    {
        public List<string> Names { get; } = [];
        public HttpClient CreateClient(string name) { Names.Add(name); return inner.CreateClient(name); }
    }
}
