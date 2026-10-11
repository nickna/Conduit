using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Exceptions;
using ConduitLLM.Functions.Providers.Exa;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.IntegrationTests.Tests;

[Collection("NativeProviderStub")]
public sealed class NativeFunctionProviderTests
{
    [Fact]
    public async Task ProductionExaClientSendsChunkedGeneratedJsonAndReadsRichUnicodeWithoutReportedCost()
    {
        await using var fixture = new StubFixture();
        var before = await fixture.ReadStateAsync();
        using var parametersJson = JsonDocument.Parse(
            """{"query":"native function 雪 café 🔎","type":"neural","numResults":2,"contents":{"text":true,"highlights":true,"summary":true}}""");
        var parameters = parametersJson.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object)property.Value.Clone());
        var client = fixture.CreateExaClient(NativeFunctionFixture.ApiKey);
        var result = await client.ExecuteAsync(parameters, cancellationToken: fixture.Token);
        Assert.True(result.IsSuccess);
        Assert.Equal(200, result.HttpStatusCode);
        using var response = JsonDocument.Parse(result.ResponseJson!);
        var body = response.RootElement;
        Assert.Equal("native-function-search", body.GetProperty("requestId").GetString());
        Assert.Equal("neural", body.GetProperty("resolvedSearchType").GetString());
        Assert.False(body.TryGetProperty("costDollars", out _));
        var match = Assert.Single(body.GetProperty("results").EnumerateArray());
        Assert.Equal("Native search 雪 🔎", match.GetProperty("title").GetString());
        Assert.Equal("Unicode text café 雪", match.GetProperty("text").GetString());
        Assert.Equal("snow 雪", match.GetProperty("highlights")[0].GetString());
        Assert.Equal("Nested 雪", match.GetProperty("subpages")[0].GetProperty("title").GetString());
        Assert.Equal("https://example.test/image.png", match.GetProperty("extras").GetProperty("imageLinks")[0].GetString());
        var usage = client.CalculateUsageFromResponse(parameters, result);
        Assert.Null(usage.ProviderReportedCost);
        Assert.Equal("neural", usage.SearchType);
        var after = await fixture.ReadStateAsync();
        Assert.Equal(1, Delta(before, after, "function_requests"));
        Assert.Equal(1, Delta(before, after, "function_chunked_requests"));
        AssertExistingCountersUnchanged(before, after);
    }

    [Theory]
    [InlineData("")]
    [InlineData("incorrect-function-key")]
    public async Task ProductionExaClientReceivesActualUnauthorizedForMissingOrWrongCredentials(string key)
    {
        await using var fixture = new StubFixture();
        var before = await fixture.ReadStateAsync();
        var exception = await Assert.ThrowsAsync<FunctionCommunicationException>(() =>
            fixture.CreateExaClient(key).ExecuteAsync(new() { ["query"] = "credential boundary" },
                cancellationToken: fixture.Token));
        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal("Invalid API key for Exa", exception.Message);
        var after = await fixture.ReadStateAsync();
        Assert.Equal(1, Delta(before, after, "function_authorization_failures"));
        Assert.Equal(0, Delta(before, after, "function_requests"));
        AssertExistingCountersUnchanged(before, after);
    }

    [Fact]
    public async Task ProductionExaClientReceivesActualProvider400WithItsDiagnosticBody()
    {
        await using var fixture = new StubFixture();
        var before = await fixture.ReadStateAsync();
        var exception = await Assert.ThrowsAsync<FunctionCommunicationException>(() =>
            fixture.CreateExaClient(NativeFunctionFixture.ApiKey).ExecuteAsync(
                new() { ["query"] = "native-function-error owned-marker" }, cancellationToken: fixture.Token));
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.StartsWith("Bad request to Exa API: ", exception.Message);
        Assert.Contains("native function upstream private diagnostic", exception.Message);
        var after = await fixture.ReadStateAsync();
        Assert.Equal(1, Delta(before, after, "function_error_requests"));
        Assert.Equal(0, Delta(before, after, "function_requests"));
        AssertExistingCountersUnchanged(before, after);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"query\":false}")]
    [InlineData("{")]
    public async Task MalformedSearchPayloadsReturnStructured400WithoutInvokingChat(string json)
    {
        await using var fixture = new StubFixture();
        var before = await fixture.ReadStateAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "search")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", NativeFunctionFixture.ApiKey);
        using var response = await fixture.Http.SendAsync(request, fixture.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(fixture.Token));
        Assert.Equal("native function query is required", body.RootElement.GetProperty("error").GetProperty("message").GetString());
        var after = await fixture.ReadStateAsync();
        Assert.Equal(1, Delta(before, after, "function_error_requests"));
        AssertExistingCountersUnchanged(before, after);
    }

    [Fact]
    public async Task ExistingBearerChatRouteRetainsItsContractAndSeparateCounters()
    {
        await using var fixture = new StubFixture();
        var before = await fixture.ReadStateAsync();
        using var payload = JsonDocument.Parse("""{"model":"native-model","messages":[{"role":"user","content":"chat control"}],"stream":false}""");
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(payload.RootElement, NativeProbeJsonContext.Default.JsonElement)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NativeProviderStub.ApiKey);
        using var response = await fixture.Http.SendAsync(request, fixture.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(fixture.Token));
        Assert.Equal("native provider response", body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        var after = await fixture.ReadStateAsync();
        Assert.Equal(1, Delta(before, after, "non_stream_requests"));
        foreach (var name in new[] { "function_requests", "function_error_requests", "function_authorization_failures", "function_chunked_requests",
                     "agentic_tool_call_requests", "agentic_final_requests", "agentic_contract_failures" })
            Assert.Equal(0, Delta(before, after, name));
    }

    private static int Delta(JsonElement before, JsonElement after, string name) =>
        after.GetProperty(name).GetInt32() - before.GetProperty(name).GetInt32();

    private static void AssertExistingCountersUnchanged(JsonElement before, JsonElement after)
    {
        foreach (var name in new[] { "non_stream_requests", "stream_requests", "image_requests", "error_requests", "authorization_failures",
                     "agentic_tool_call_requests", "agentic_final_requests", "agentic_contract_failures" })
            Assert.Equal(0, Delta(before, after, name));
    }

    private sealed class StubFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(15));
        private readonly Task _server;
        private readonly ServiceProvider _services;
        public HttpClient Http { get; }
        public CancellationToken Token => _stop.Token;

        public StubFixture()
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var address = new Uri($"http://127.0.0.1:{((IPEndPoint)reservation.LocalEndpoint).Port}/");
            reservation.Stop();
            _server = NativeProviderStub.RunAsync(address, Token);
            Http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            {
                BaseAddress = address,
                Timeout = Timeout.InfiniteTimeSpan
            };
            var services = new ServiceCollection();
            services.AddHttpClient("ExaFunctionClient")
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false });
            _services = services.BuildServiceProvider();
        }

        public ExaClient CreateExaClient(string key) => new(
            new FunctionConfiguration { ConfigurationName = "Native HTTP fixture", ProviderType = FunctionProviderType.Exa,
                Purpose = FunctionPurpose.Search, BaseUrl = Http.BaseAddress!.ToString(), TimeoutSeconds = 10 },
            new FunctionCredential { ApiKey = key }, _services.GetRequiredService<IHttpClientFactory>(), NullLogger<ExaClient>.Instance);

        public async Task<JsonElement> ReadStateAsync()
        {
            using var response = await Http.GetAsync("probe/state", Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            return body.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            _services.Dispose();
            _stop.Cancel();
            try
            {
                await _server.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { _stop.Dispose(); }
        }
    }
}
