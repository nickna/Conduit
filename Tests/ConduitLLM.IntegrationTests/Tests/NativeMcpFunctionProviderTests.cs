using System.Text.Json;
using System.Text.Json.Nodes;

using ConduitLLM.Core.Extensions;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Models;
using ConduitLLM.Functions.Providers.Mcp;
using ConduitLLM.Functions.Serialization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed class NativeMcpFunctionProviderTests
{
    private const string Text = "Native MCP result café 雪 🔎";
    private const string Schema = """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""";
    private const string Structured = """{"unicode":"café 雪 🔎","items":[null,true,2,{"name":"nested 雪"}]}""";

    [Fact]
    public async Task ProductionClientDiscoversAndExecutesModernMcpWithGeneratedJsonAndUnchangedCallerArguments()
    {
        await using var fixture = new McpFixture();
        var client = fixture.CreateClient();

        var tools = await client.ListToolsAsync(fixture.Token);
        Assert.Equal(new[] { "native_search", "native_echo" }, tools.Select(tool => tool.Name));
        Assert.Equal("Search Unicode content café 雪 🔎", tools[0].Description);
        Assert.Equal("Echo Unicode content café 雪", tools[1].Description);
        foreach (var tool in tools)
        {
            Assert.NotNull(tool.ParametersSchema);
            using var schema = JsonDocument.Parse(tool.ParametersSchema.ToJsonString());
            AssertJson(Schema, schema.RootElement);
        }

        var parameters = Parameters();
        var original = SerializeParameters(parameters);
        var result = await client.ExecuteAsync(parameters, cancellationToken: fixture.Token);

        AssertSuccessfulResult(result, fixture.Stub.Endpoint);
        Assert.Equal(original, SerializeParameters(parameters));
        Assert.Equal("native_search", Assert.IsType<JsonElement>(parameters[McpReservedParameterKeys.ToolName]).GetString());
        Assert.NotNull(fixture.Stub.LastCallArguments);
        AssertJson("""{"query":"café 雪 🔎","limit":3,"enabled":true,"optional":null,"nested":{"items":["雪",2,false,null]}}""",
            fixture.Stub.LastCallArguments.Value);
        AssertProtocolMetadata(fixture.Stub.LastRequestMetadata);
        AssertUsage(client, parameters, result, expectedResults: 1);
        await fixture.AssertCountersAsync(discover: 2, list: 1, call: 1);
        Assert.True(fixture.Stub.Counters.ChunkedRequests > 0);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("subset")]
    [InlineData("different_case")]
    public async Task AllowlistPreservesNullVersusEmptyAndOrdinalToolFilteringOverActualTransport(string setting)
    {
        await using var fixture = new McpFixture();
        var settings = JsonNode.Parse(Settings())!.AsObject();
        settings.Remove("allowedTools");
        switch (setting)
        {
            case "null": settings["allowedTools"] = null; break;
            case "empty": settings["allowedTools"] = new JsonArray(); break;
            case "subset": settings["allowedTools"] = new JsonArray("native_search"); break;
            case "different_case": settings["allowedTools"] = new JsonArray("NATIVE_SEARCH"); break;
        }
        var client = fixture.CreateClient(settings.ToJsonString());
        var tools = await client.ListToolsAsync(fixture.Token);
        var names = setting switch
        {
            "absent" or "null" => new[] { "native_search", "native_echo" },
            "subset" => new[] { "native_search" },
            _ => Array.Empty<string>()
        };
        Assert.Equal(names, tools.Select(tool => tool.Name));
        var parameters = Parameters();
        var original = SerializeParameters(parameters);
        if (setting is "empty" or "different_case")
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                client.ExecuteAsync(parameters, cancellationToken: fixture.Token));
            Assert.Equal("parameters", exception.ParamName);
            Assert.Contains("not in the configured allowlist", exception.Message);
            await fixture.AssertCountersAsync(discover: 1, list: 1, call: 0);
        }
        else
        {
            AssertSuccessfulResult(await client.ExecuteAsync(parameters, cancellationToken: fixture.Token), fixture.Stub.Endpoint);
            await fixture.AssertCountersAsync(discover: 2, list: 1, call: 1);
        }
        Assert.Equal(original, SerializeParameters(parameters));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("blank")]
    [InlineData("denied")]
    public async Task MissingOrDisallowedRoutingThrowsBeforeAnyTcpRequest(string routing)
    {
        await using var fixture = new McpFixture();
        var client = fixture.CreateClient(Settings(allowedTools: ["native_search"]));
        var parameters = Parameters();
        switch (routing)
        {
            case "missing": parameters.Remove(McpReservedParameterKeys.ToolName); break;
            case "null": parameters[McpReservedParameterKeys.ToolName] = JsonValue("null"); break;
            case "blank": parameters[McpReservedParameterKeys.ToolName] = " \t"; break;
            case "denied": parameters[McpReservedParameterKeys.ToolName] = "native_echo"; break;
        }
        var original = SerializeParameters(parameters);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ExecuteAsync(parameters, cancellationToken: fixture.Token));

        Assert.Equal("parameters", exception.ParamName);
        Assert.Equal(original, SerializeParameters(parameters));
        await fixture.AssertCountersAsync(discover: 0, list: 0, call: 0);
        Assert.Equal(0, fixture.Stub.Counters.ChunkedRequests);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("scheme")]
    [InlineData("custom_header")]
    [InlineData("override")]
    public async Task GeneratedSettingsAndCredentialOverridesProduceTheActualExpectedAuthenticationHeader(string authentication)
    {
        var header = authentication == "custom_header" ? "X-Api-Key" : "Authorization";
        var expected = authentication switch
        {
            "scheme" => "Basic " + NativeMcpProviderStub.ApiKey,
            "custom_header" => NativeMcpProviderStub.ApiKey,
            "override" => "Bearer native-override-key",
            _ => "Bearer " + NativeMcpProviderStub.ApiKey
        };
        await using var fixture = new McpFixture(authHeader: header, authValue: expected);
        var client = fixture.CreateClient(Settings(
            authHeader: authentication == "custom_header" ? header : null,
            authScheme: authentication == "scheme" ? "Basic" : authentication == "custom_header" ? "IgnoredScheme" : null));

        var result = await client.ExecuteAsync(Parameters(),
            apiKey: authentication == "override" ? "native-override-key" : null, cancellationToken: fixture.Token);

        AssertSuccessfulResult(result, fixture.Stub.Endpoint);
        AssertProtocolMetadata(fixture.Stub.LastRequestMetadata);
        await fixture.AssertCountersAsync(discover: 1, list: 0, call: 1);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong")]
    [InlineData("empty_override")]
    public async Task InvalidAuthenticationCannotDiscoverOrCallAndProducesControlledClientFailures(string authentication)
    {
        await using var fixture = new McpFixture();
        var client = fixture.CreateClient(credentialKey: authentication switch
        {
            "missing" => null,
            "wrong" => "incorrect-provider-key",
            _ => NativeMcpProviderStub.ApiKey
        });
        var overrideKey = authentication == "empty_override" ? string.Empty : null;

        var result = await client.ExecuteAsync(Parameters(), apiKey: overrideKey, cancellationToken: fixture.Token);
        AssertControlledFailure(result);
        var verification = await client.VerifyAuthenticationAsync(overrideKey, fixture.Token);
        Assert.False(verification.IsSuccess);
        Assert.StartsWith("Failed to connect to MCP server:", verification.Message);
        Assert.False(string.IsNullOrWhiteSpace(verification.Details));
        await fixture.Stub.WaitForIdleAsync(fixture.Token);
        Assert.True(fixture.Stub.Counters.AuthorizationFailures >= 2);
        Assert.Equal(0, fixture.Stub.Counters.ListRequests);
        Assert.Equal(0, fixture.Stub.Counters.CallRequests);
        Assert.Equal(0, fixture.Stub.Counters.ProtocolFailures);
        AssertIdle(fixture.Stub.Counters);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CredentialUrlOverridesConfigurationAndBlankCredentialFallsBackWithoutContactingTheOtherServer(bool hasOverride)
    {
        await using var configurationServer = new McpFixture();
        await using var credentialServer = new McpFixture();
        var client = configurationServer.CreateClient(
            credentialUrl: hasOverride ? "  " + credentialServer.Stub.Endpoint + "  " : " \t",
            configuredUrl: "  " + configurationServer.Stub.Endpoint + "  ");

        var result = await client.ExecuteAsync(Parameters(), cancellationToken: configurationServer.Token);

        var selected = hasOverride ? credentialServer : configurationServer;
        var unused = hasOverride ? configurationServer : credentialServer;
        AssertSuccessfulResult(result, selected.Stub.Endpoint);
        await selected.AssertCountersAsync(discover: 1, list: 0, call: 1);
        await unused.AssertCountersAsync(discover: 0, list: 0, call: 0);
    }

    [Fact]
    public async Task DefaultPrivateNetworkGuardRejectsDiscoveryAndControlsExecutionFailureWithoutTcpAccess()
    {
        await using var fixture = new McpFixture();
        var client = fixture.CreateClient(Settings(allowPrivateNetwork: false));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListToolsAsync(fixture.Token));
        Assert.Contains("allowPrivateNetwork", exception.Message);
        var result = await client.ExecuteAsync(Parameters(), cancellationToken: fixture.Token);
        AssertControlledFailure(result);
        Assert.Contains("allowPrivateNetwork", result.ErrorMessage);
        await fixture.AssertCountersAsync(discover: 0, list: 0, call: 0);
        Assert.Equal(0, fixture.Stub.Counters.ChunkedRequests);
    }

    [Fact]
    public async Task ToolErrorRetainsTextAndStructuredResponseAndMetadataWhileUsageCountsNoResult()
    {
        await using var fixture = new McpFixture(NativeMcpCallMode.ToolError);
        var client = fixture.CreateClient();
        var parameters = Parameters();

        var result = await client.ExecuteAsync(parameters, cancellationToken: fixture.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal("The MCP tool reported an error (see response content).", result.ErrorMessage);
        AssertResponse(result, isError: true);
        AssertMetadata(result, fixture.Stub.Endpoint);
        Assert.Null(result.HttpStatusCode);
        AssertUsage(client, parameters, result, expectedResults: 0);
        await fixture.AssertCountersAsync(discover: 1, list: 0, call: 1);
    }

    [Fact]
    public async Task JsonRpcErrorProducesFailedExecutionWithoutToolResponseOrMetadata()
    {
        await using var fixture = new McpFixture(NativeMcpCallMode.RpcError);
        var client = fixture.CreateClient();
        var parameters = Parameters();

        var result = await client.ExecuteAsync(parameters, cancellationToken: fixture.Token);

        AssertControlledFailure(result);
        Assert.Contains("Native MCP RPC failed.", result.ErrorMessage);
        AssertUsage(client, parameters, result, expectedResults: 0);
        await fixture.AssertCountersAsync(discover: 1, list: 0, call: 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualLargeWireResultsProduceBoundedValidJsonWithoutSplittingUnicodeOrRetainingOversizedStructuredContent(bool structured)
    {
        await using var fixture = new McpFixture(structured ? NativeMcpCallMode.LargeStructured : NativeMcpCallMode.LargeText);
        var client = fixture.CreateClient();
        var parameters = Parameters();

        var result = await client.ExecuteAsync(parameters, cancellationToken: fixture.Token);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.HttpStatusCode);
        AssertMetadata(result, fixture.Stub.Endpoint);
        var json = Assert.IsType<string>(result.ResponseJson);
        Assert.InRange(json.Length, 1, 100_000);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.False(root.GetProperty("isError").GetBoolean());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.False(root.TryGetProperty("structuredContent", out _));
        var content = Assert.Single(root.GetProperty("content").EnumerateArray()).GetString()!;
        AssertValidSurrogates(content);
        if (structured)
        {
            AssertKeys(root, "isError", "content", "omittedNonTextBlocks", "truncated", "omittedStructuredContent");
            Assert.True(root.GetProperty("omittedStructuredContent").GetBoolean());
            Assert.Equal(1, root.GetProperty("omittedNonTextBlocks").GetInt32());
            Assert.Equal(Text, content);
        }
        else
        {
            AssertKeys(root, "isError", "content", "truncated");
            Assert.StartsWith("😀", content);
            Assert.EndsWith("…[truncated]", content);
            Assert.DoesNotContain("\uFFFD", content);
        }
        AssertUsage(client, parameters, result, expectedResults: 1);
        await fixture.AssertCountersAsync(discover: 1, list: 0, call: 1);
    }

    [Fact]
    public async Task DiscoveryCancellationAfterServerReceiptPropagatesAndTheSameClientCanRetry()
    {
        await using var fixture = new McpFixture(NativeMcpCallMode.HoldFirstList);
        var client = fixture.CreateClient();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        var pending = client.ListToolsAsync(cancellation.Token);
        await fixture.Stub.ListReceived.WaitAsync(fixture.Token);
        Assert.Equal(1, fixture.Stub.Counters.ListRequests);
        Assert.Equal(1, fixture.Stub.Counters.HeldRequests);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(fixture.Token));

        fixture.Stub.ReleaseHeldRequests();
        await fixture.Stub.WaitForIdleAsync(fixture.Token);
        var tools = await client.ListToolsAsync(fixture.Token);
        Assert.Equal(new[] { "native_search", "native_echo" }, tools.Select(tool => tool.Name));
        await fixture.AssertCountersAsync(discover: 2, list: 2, call: 0);
    }

    [Fact]
    public async Task ExecutionCancellationAfterServerReceiptReturnsControlledFailureAndTheSameClientCanRetry()
    {
        await using var fixture = new McpFixture(NativeMcpCallMode.HoldFirstCall);
        var client = fixture.CreateClient();
        var parameters = Parameters();
        var original = SerializeParameters(parameters);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        var pending = client.ExecuteAsync(parameters, cancellationToken: cancellation.Token);
        await fixture.Stub.CallReceived.WaitAsync(fixture.Token);
        Assert.Equal(1, fixture.Stub.Counters.CallRequests);
        Assert.Equal(1, fixture.Stub.Counters.HeldRequests);
        cancellation.Cancel();

        var result = await pending.WaitAsync(fixture.Token);
        AssertControlledFailure(result);
        AssertUsage(client, parameters, result, expectedResults: 0);
        Assert.Equal(original, SerializeParameters(parameters));

        fixture.Stub.ReleaseHeldRequests();
        await fixture.Stub.WaitForIdleAsync(fixture.Token);
        AssertSuccessfulResult(await client.ExecuteAsync(parameters, cancellationToken: fixture.Token), fixture.Stub.Endpoint);
        Assert.Equal(original, SerializeParameters(parameters));
        await fixture.AssertCountersAsync(discover: 2, list: 0, call: 2);
    }

    [Fact]
    public async Task AuthenticationVerificationCountsAdvertisedToolsWithoutApplyingTheExecutionAllowlist()
    {
        await using var fixture = new McpFixture();
        var client = fixture.CreateClient(Settings(allowedTools: []));

        var result = await client.VerifyAuthenticationAsync(cancellationToken: fixture.Token);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("Connected to MCP server; 2 tool(s) available.", result.Message);
        Assert.Null(result.Details);
        Assert.True(double.IsFinite(result.ResponseTimeMs));
        Assert.True(result.ResponseTimeMs >= 0);
        await fixture.AssertCountersAsync(discover: 1, list: 1, call: 0);
    }

    private static Dictionary<string, object> Parameters()
    {
        using var document = JsonDocument.Parse("""{"query":"café 雪 🔎","limit":3,"enabled":true,"optional":null,"nested":{"items":["雪",2,false,null]},"__conduit_mcp_tool":"native_search"}""");
        return document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value.Clone());
    }

    private static JsonElement JsonValue(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string SerializeParameters(Dictionary<string, object> parameters) =>
        JsonSerializer.Serialize(parameters, FunctionsJsonContext.Default.DictionaryStringObject);

    private static string Settings(IReadOnlyList<string>? allowedTools = null, string? authHeader = null,
        string? authScheme = null, bool allowPrivateNetwork = true) => JsonSerializer.Serialize(
        new McpServerSettings
        {
            AllowedTools = allowedTools, AuthHeader = authHeader, AuthScheme = authScheme,
            AllowPrivateNetwork = allowPrivateNetwork
        }, FunctionsJsonContext.Default.McpServerSettings);

    private static void AssertSuccessfulResult(FunctionExecutionResult result, Uri endpoint)
    {
        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.HttpStatusCode);
        Assert.True(result.Duration >= TimeSpan.Zero);
        AssertResponse(result, isError: false);
        AssertMetadata(result, endpoint);
    }

    private static void AssertResponse(FunctionExecutionResult result, bool isError)
    {
        using var document = JsonDocument.Parse(Assert.IsType<string>(result.ResponseJson));
        var root = document.RootElement;
        AssertKeys(root, "isError", "content", "omittedNonTextBlocks", "structuredContent");
        Assert.Equal(isError, root.GetProperty("isError").GetBoolean());
        Assert.Equal(Text, Assert.Single(root.GetProperty("content").EnumerateArray()).GetString());
        Assert.Equal(1, root.GetProperty("omittedNonTextBlocks").GetInt32());
        AssertJson(Structured, root.GetProperty("structuredContent"));
    }

    private static void AssertMetadata(FunctionExecutionResult result, Uri endpoint)
    {
        Assert.NotNull(result.Metadata);
        Assert.Equal(new[] { "mcpTool", "serverUrl" }, result.Metadata.Keys.Order().ToArray());
        Assert.Equal("native_search", Assert.IsType<string>(result.Metadata["mcpTool"]));
        Assert.Equal(endpoint.ToString(), Assert.IsType<string>(result.Metadata["serverUrl"]));
    }

    private static void AssertControlledFailure(FunctionExecutionResult result)
    {
        Assert.False(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Null(result.ResponseJson);
        Assert.Null(result.Metadata);
        Assert.Null(result.HttpStatusCode);
        Assert.True(result.Duration >= TimeSpan.Zero);
    }

    private static void AssertUsage(McpFunctionClient client, Dictionary<string, object> parameters,
        FunctionExecutionResult result, int expectedResults)
    {
        var usage = client.CalculateUsageFromResponse(parameters, result);
        Assert.Equal(expectedResults, usage.ResultCount);
        Assert.Equal(result.Duration, usage.ExecutionDuration);
        Assert.Null(usage.ProviderReportedCost);
        Assert.Null(usage.TokensConsumed);
        Assert.Null(usage.InputTokensConsumed);
        Assert.Null(usage.OutputTokensConsumed);
        Assert.Null(usage.SearchType);
        Assert.Null(usage.TextPagesExtracted);
        Assert.Null(usage.HighlightPagesExtracted);
        Assert.Null(usage.SummaryPagesGenerated);
        Assert.Null(usage.DocumentsIndexed);
        Assert.Null(usage.VectorDimensions);
        Assert.Null(usage.ExtensionData);
        var metadata = Assert.Single(usage.Metadata!);
        Assert.Equal("responseChars", metadata.Key);
        Assert.Equal(result.ResponseJson?.Length ?? 0, Assert.IsType<int>(metadata.Value));
    }

    private static void AssertProtocolMetadata(JsonElement? metadata)
    {
        Assert.NotNull(metadata);
        Assert.Equal("2026-07-28", metadata.Value.GetProperty("io.modelcontextprotocol/protocolVersion").GetString());
        Assert.Equal(JsonValueKind.Object, metadata.Value.GetProperty("io.modelcontextprotocol/clientInfo").ValueKind);
        Assert.Equal(JsonValueKind.Object, metadata.Value.GetProperty("io.modelcontextprotocol/clientCapabilities").ValueKind);
    }

    private static void AssertJson(string expectedJson, JsonElement actual)
    {
        using var expected = JsonDocument.Parse(expectedJson);
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual), "The MCP JSON contract differs from the independent expected value.");
    }

    private static void AssertKeys(JsonElement value, params string[] names) =>
        Assert.Equal(names.Order().ToArray(), value.EnumerateObject().Select(property => property.Name).Order().ToArray());

    private static void AssertValidSurrogates(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                Assert.True(index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]));
                index++;
            }
            else Assert.False(char.IsLowSurrogate(text[index]));
        }
    }

    private static void AssertIdle(NativeMcpCounters counters)
    {
        Assert.Equal(0, counters.ActiveHandlers);
        Assert.Equal(0, counters.HeldRequests);
    }

    private sealed class McpFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(20));
        private readonly ServiceProvider _services;
        public NativeMcpProviderStub Stub { get; }
        public CancellationToken Token => _deadline.Token;

        public McpFixture(NativeMcpCallMode mode = NativeMcpCallMode.Success,
            string authHeader = "Authorization", string? authValue = null)
        {
            Stub = new NativeMcpProviderStub(mode, authHeader, authValue);
            var services = new ServiceCollection();
            services.AddSharedHttpClients();
            services.AddHttpClient("MCPFunctionClient")
                .ConfigurePrimaryHttpMessageHandler((handler, _) => ((SocketsHttpHandler)handler).UseProxy = false);
            _services = services.BuildServiceProvider();
        }

        public McpFunctionClient CreateClient(string? providerSettings = null, string? credentialUrl = null,
            string? configuredUrl = null, string? credentialKey = NativeMcpProviderStub.ApiKey) => new(
            new FunctionConfiguration
            {
                ConfigurationName = "Native MCP TCP fixture", ProviderType = FunctionProviderType.Mcp,
                Purpose = FunctionPurpose.Search, BaseUrl = configuredUrl ?? Stub.Endpoint.ToString(),
                TimeoutSeconds = 10, ProviderSettings = providerSettings ?? Settings()
            },
            new FunctionCredential { ProviderType = FunctionProviderType.Mcp, ApiKey = credentialKey, BaseUrl = credentialUrl },
            _services.GetRequiredService<IHttpClientFactory>(), NullLoggerFactory.Instance);

        public async Task AssertCountersAsync(int discover, int list, int call)
        {
            await Stub.WaitForIdleAsync(Token);
            var counters = Stub.Counters;
            Assert.Equal(discover, counters.DiscoverRequests);
            Assert.Equal(list, counters.ListRequests);
            Assert.Equal(call, counters.CallRequests);
            Assert.Equal(0, counters.AuthorizationFailures);
            Assert.Equal(0, counters.ProtocolFailures);
            if (discover == 0 && list == 0 && call == 0)
                Assert.Equal(0, counters.AcceptedConnections);
            AssertIdle(counters);
        }

        public async ValueTask DisposeAsync()
        {
            _deadline.Cancel();
            try { await Stub.DisposeAsync(); }
            finally
            {
                _services.Dispose();
                _deadline.Dispose();
            }
        }
    }
}
