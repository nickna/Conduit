using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Providers.Exa;
using ConduitLLM.Providers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.IntegrationTests.Tests;

[CollectionDefinition("NativeProviderStub", DisableParallelization = true)]
public sealed class NativeProviderStubCollection;

[Collection("NativeProviderStub")]
public sealed class NativeAgenticProviderTests
{
    private const string ToolName = "Native_Search";
    private const string Schema = """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""";

    [Fact]
    public async Task ProductionClientsRoundTripAdvertisedToolArgumentsAndRichResultAcrossTwoChatPhases()
    {
        await using var fixture = new StubFixture();
        var before = await fixture.ReadStateAsync();
        var marker = Guid.NewGuid().ToString("N");
        var request = Request(marker);
        var chat = fixture.CreateChatClient();

        var first = await chat.CreateChatCompletionAsync(request, cancellationToken: fixture.Token);
        var firstChoice = Assert.Single(first.Choices);
        Assert.Equal("tool_calls", firstChoice.FinishReason);
        Assert.Equal("assistant", firstChoice.Message.Role);
        var call = Assert.Single(firstChoice.Message.ToolCalls!);
        Assert.Equal("call_native_" + marker, call.Id);
        Assert.Equal("function", call.Type);
        Assert.Equal(ToolName, call.Function.Name);
        using var arguments = JsonDocument.Parse(call.Function.Arguments);
        var query = arguments.RootElement.GetProperty("query").GetString();
        Assert.Equal(NativeProviderStub.AgenticQueryPrefix + marker + " café 雪 🔎", query);
        Assert.Single(arguments.RootElement.EnumerateObject());
        var parameters = arguments.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object)property.Value.Clone());
        var exa = fixture.CreateExaClient();
        var execution = await exa.ExecuteAsync(parameters, cancellationToken: fixture.Token);
        Assert.True(execution.IsSuccess);
        Assert.Equal(200, execution.HttpStatusCode);
        Assert.NotNull(execution.ResponseJson);
        using var result = JsonDocument.Parse(execution.ResponseJson);
        Assert.Equal("native-function-search", result.RootElement.GetProperty("requestId").GetString());
        Assert.False(result.RootElement.TryGetProperty("costDollars", out _));
        var match = Assert.Single(result.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal("Native search 雪 🔎", match.GetProperty("title").GetString());
        Assert.Equal("Unicode text café 雪", match.GetProperty("text").GetString());
        Assert.Equal("snow 雪", match.GetProperty("highlights")[0].GetString());
        Assert.Equal("Nested 雪", match.GetProperty("subpages")[0].GetProperty("title").GetString());
        Assert.Equal("https://example.test/image.png", match.GetProperty("extras").GetProperty("imageLinks")[0].GetString());
        Assert.Null(exa.CalculateUsageFromResponse(parameters, execution).ProviderReportedCost);

        request.Messages.Add(firstChoice.Message);
        request.Messages.Add(new Message
        {
            Role = "tool", Name = call.Function.Name, ToolCallId = call.Id, Content = execution.ResponseJson
        });
        var final = await chat.CreateChatCompletionAsync(request, cancellationToken: fixture.Token);
        var finalChoice = Assert.Single(final.Choices);
        Assert.Equal("stop", finalChoice.FinishReason);
        Assert.Equal("assistant", finalChoice.Message.Role);
        var finalContent = Assert.IsType<JsonElement>(finalChoice.Message.Content);
        Assert.Equal(NativeProviderStub.AgenticFinalContent, finalContent.GetString());
        Assert.Null(finalChoice.Message.ToolCalls);
        AssertUsage(first);
        AssertUsage(final);
        Assert.Equal(20, first.Usage!.PromptTokens + final.Usage!.PromptTokens);
        Assert.Equal(10, first.Usage.CompletionTokens + final.Usage.CompletionTokens);
        Assert.Equal(30, first.Usage.TotalTokens + final.Usage.TotalTokens);
        Assert.Equal("Native_Search", request.Tools![0].Function.Name);
        AssertCounters(before, await fixture.ReadStateAsync(),
            ("agentic_tool_call_requests", 1), ("agentic_final_requests", 1),
            ("function_requests", 1), ("function_chunked_requests", 1));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("query_type")]
    [InlineData("required_missing")]
    [InlineData("required_duplicate")]
    public async Task ProductionChatRejectsMalformedAdvertisedToolsWithoutFallingThroughToOrdinaryChat(string defect)
    {
        await using var fixture = new StubFixture();
        var before = await fixture.ReadStateAsync();
        var request = Request(Guid.NewGuid().ToString("N"));
        var tool = request.Tools![0];
        switch (defect)
        {
            case "name": tool.Function.Name = "invalid-tool-name"; break;
            case "type": tool.Type = "browser"; break;
            case "query_type": tool.Function.Parameters!["properties"]!["query"]!["type"] = "boolean"; break;
            case "required_missing": tool.Function.Parameters!["required"] = new JsonArray(); break;
            case "required_duplicate": tool.Function.Parameters!["required"] = new JsonArray("query", "query"); break;
            default: throw new InvalidOperationException("Unknown tool fixture defect.");
        }

        var exception = await Assert.ThrowsAsync<LLMCommunicationException>(() =>
            fixture.CreateChatClient().CreateChatCompletionAsync(request, cancellationToken: fixture.Token));
        AssertContractFailure(exception);
        AssertCounters(before, await fixture.ReadStateAsync(), ("agentic_contract_failures", 1));
    }

    [Theory]
    [InlineData("call_id")]
    [InlineData("call_name")]
    [InlineData("arguments")]
    [InlineData("tool_id")]
    [InlineData("result")]
    public async Task ProductionFollowUpRejectsMismatchedAssistantCallsOrToolResults(string defect)
    {
        await using var fixture = new StubFixture();
        var before = await fixture.ReadStateAsync();
        var marker = Guid.NewGuid().ToString("N");
        var request = Request(marker);
        var chat = fixture.CreateChatClient();
        var first = await chat.CreateChatCompletionAsync(request, cancellationToken: fixture.Token);
        var assistant = Assert.Single(first.Choices).Message;
        var call = Assert.Single(assistant.ToolCalls!);
        var tool = new Message
        {
            Role = "tool", Name = ToolName, ToolCallId = call.Id,
            Content = NativeProviderStub.FunctionSuccessResponse
        };
        switch (defect)
        {
            case "call_id": call.Id = "call_other"; break;
            case "call_name": call.Function.Name = "Other_Search"; break;
            case "arguments": call.Function.Arguments = """{"query":"different query 雪"}"""; break;
            case "tool_id": tool.ToolCallId = "call_other"; break;
            case "result": tool.Content = """{"requestId":"native-function-search","results":[]}"""; break;
            default: throw new InvalidOperationException("Unknown follow-up fixture defect.");
        }
        request.Messages.Add(assistant);
        request.Messages.Add(tool);

        var exception = await Assert.ThrowsAsync<LLMCommunicationException>(() =>
            chat.CreateChatCompletionAsync(request, cancellationToken: fixture.Token));
        AssertContractFailure(exception);
        AssertCounters(before, await fixture.ReadStateAsync(),
            ("agentic_tool_call_requests", 1), ("agentic_contract_failures", 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("incorrect-provider-key")]
    public async Task AgenticTransportRequiresActualBearerAuthentication(string? key)
    {
        await using var fixture = new StubFixture();
        var before = await fixture.ReadStateAsync();
        var payload = JsonSerializer.SerializeToElement(Request(Guid.NewGuid().ToString("N")),
            CoreHttpJsonContext.Default.ChatCompletionRequest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(payload, NativeProbeJsonContext.Default.JsonElement)
        };
        if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await fixture.Http.SendAsync(request, fixture.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(fixture.Token));
        Assert.Equal("native provider credential rejected", body.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal("authentication_error", body.RootElement.GetProperty("error").GetProperty("type").GetString());
        AssertCounters(before, await fixture.ReadStateAsync(), ("authorization_failures", 1));
    }

    [Fact]
    public async Task ProductionStreamingCannotSilentlyUseTheNonstreamAgenticProof()
    {
        await using var fixture = new StubFixture();
        var before = await fixture.ReadStateAsync();
        var request = Request(Guid.NewGuid().ToString("N"));
        var received = 0;
        var exception = await Assert.ThrowsAsync<LLMCommunicationException>(async () =>
        {
            await foreach (var _ in fixture.CreateChatClient().StreamChatCompletionAsync(request,
                cancellationToken: fixture.Token)) received++;
        });
        Assert.Equal(0, received);
        AssertContractFailure(exception);
        AssertCounters(before, await fixture.ReadStateAsync(), ("agentic_contract_failures", 1));
    }

    private static ChatCompletionRequest Request(string marker) => new()
    {
        Model = "native-agentic-alias",
        Messages = [new Message { Role = "user", Content = NativeProviderStub.AgenticPromptPrefix + marker }],
        Tools = [new Tool { Function = new FunctionDefinition
        {
            Name = ToolName, Description = "Search Unicode content café 雪 🔎",
            Parameters = JsonNode.Parse(Schema)!.AsObject()
        } }],
        Stream = false
    };

    private static void AssertUsage(ChatCompletionResponse response)
    {
        Assert.NotNull(response.Usage);
        Assert.Equal(10, response.Usage.PromptTokens);
        Assert.Equal(5, response.Usage.CompletionTokens);
        Assert.Equal(15, response.Usage.TotalTokens);
        Assert.Null(response.Usage.ProviderReportedCostUsd);
        Assert.Null(response.ProviderToolUsage);
    }

    private static void AssertContractFailure(LLMCommunicationException exception)
    {
        var detailed = LLMCommunicationException.FindWithStatus(exception);
        Assert.NotNull(detailed);
        Assert.Equal(HttpStatusCode.BadRequest, detailed.StatusCode);
        Assert.NotNull(detailed.ResponseBody);
        using var body = JsonDocument.Parse(detailed.ResponseBody);
        Assert.Equal(["error"], body.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        var error = body.RootElement.GetProperty("error");
        Assert.Equal(["code", "message", "type"], error.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("native_agentic_contract_error", error.GetProperty("code").GetString());
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("Native agentic provider contract rejected.", error.GetProperty("message").GetString());
    }

    private static void AssertCounters(JsonElement before, JsonElement after, params (string Name, int Delta)[] expected)
    {
        foreach (var name in expected.Select(value => value.Name))
            Assert.True(after.TryGetProperty(name, out _), "Expected provider counter " + name);
        Assert.Equal(before.EnumerateObject().Select(property => property.Name).Order().ToArray(),
            after.EnumerateObject().Select(property => property.Name).Order().ToArray());
        foreach (var property in after.EnumerateObject())
        {
            var change = property.Value.GetInt32() - before.GetProperty(property.Name).GetInt32();
            Assert.Equal(expected.SingleOrDefault(value => value.Name == property.Name).Delta, change);
        }
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
                BaseAddress = address, Timeout = Timeout.InfiniteTimeSpan
            };
            var services = new ServiceCollection();
            foreach (var name in new[] { "openai-compatibleLLMClient", "ExaFunctionClient" })
                services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false });
            _services = services.BuildServiceProvider();
        }

        public OpenAICompatibleGenericClient CreateChatClient() => new(
            new Provider { ProviderType = ProviderType.OpenAICompatible, ProviderName = "Native agentic fixture",
                BaseUrl = Http.BaseAddress!.ToString() },
            new ProviderKeyCredential { ApiKey = NativeProviderStub.ApiKey }, "native-agentic-provider-model",
            NullLogger<OpenAICompatibleGenericClient>.Instance, _services.GetRequiredService<IHttpClientFactory>());

        public ExaClient CreateExaClient() => new(
            new FunctionConfiguration { ConfigurationName = "Native agentic fixture", ProviderType = FunctionProviderType.Exa,
                Purpose = FunctionPurpose.Search, BaseUrl = Http.BaseAddress!.ToString(), TimeoutSeconds = 10 },
            new FunctionCredential { ApiKey = NativeFunctionFixture.ApiKey },
            _services.GetRequiredService<IHttpClientFactory>(), NullLogger<ExaClient>.Instance);

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
            try { await _server.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            finally { _stop.Dispose(); }
        }
    }
}
