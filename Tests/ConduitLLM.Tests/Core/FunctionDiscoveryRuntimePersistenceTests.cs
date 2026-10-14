using System.Text.Json;
using System.Text.Json.Nodes;

using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Models;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.Tests.Core;

public sealed class FunctionDiscoveryRuntimePersistenceTests
{
    [Fact]
    public void RepositoryConstructorRemainsTheOnlyPublicConstructor()
    {
        Assert.NotNull(typeof(FunctionDiscoveryService).GetConstructor(
        [
            typeof(IFunctionConfigurationRepository), typeof(IFunctionClientFactory),
            typeof(IFunctionDiscoveryCacheService), typeof(ILogger<FunctionDiscoveryService>)
        ]));
        Assert.Single(typeof(FunctionDiscoveryService).GetConstructors());
    }

    [Theory]
    [InlineData(false, "configuration", "functionConfigRepository")]
    [InlineData(true, "configuration", "configurationStore")]
    [InlineData(false, "client", "clientFactory")]
    [InlineData(true, "client", "clientFactory")]
    [InlineData(false, "logger", "logger")]
    [InlineData(true, "logger", "logger")]
    public void RequiredCollaboratorsRetainTheirNullGuardsAndCachingRemainsOptional(
        bool runtime, string collaborator, string parameterName)
    {
        using var fixture = new Fixture(runtime);
        var error = Assert.Throws<ArgumentNullException>(() => runtime
            ? FunctionDiscoveryService.CreateWithRuntimePersistence(
                collaborator == "configuration" ? null : fixture.Store.Object,
                collaborator == "client" ? null : fixture.Factory.Object,
                null, collaborator == "logger" ? null : fixture.Log)
            : new FunctionDiscoveryService(
                collaborator == "configuration" ? null : fixture.Repository.Object,
                collaborator == "client" ? null : fixture.Factory.Object,
                null, collaborator == "logger" ? null : fixture.Log));
        Assert.Equal(parameterName, error.ParamName);
        fixture.AssertBoundary(0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task NullOrEmptyInputsReturnBeforeCacheReadsClientsAndCancellation(bool runtime, bool nullInput)
    {
        using var fixture = new Fixture(runtime, caching: true);
        fixture.CancelCaller();
        List<int> ids = nullInput ? null : [];
        Assert.Empty(await fixture.Service.GetToolsForFunctionConfigurationsAsync(ids, 73, fixture.CallerToken));
        Assert.Empty(await fixture.Service.GetFunctionNameToIdMappingAsync(ids, fixture.CallerToken));
        if (ids != null) Assert.Empty(ids);
        fixture.AssertBoundary(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolsNormalizeReadIdsWithoutChangingTheInputOrReturnedStoreOrder(bool runtime)
    {
        using var fixture = new Fixture(runtime);
        var first = Configuration(1, "Production Exa Search");
        var second = Configuration(2, "2 Tavily - Answer!", FunctionProviderType.Tavily);
        second.Purpose = FunctionPurpose.Answer;
        second.Description = "Explicit answer description";
        fixture.Rows = [second, first];
        var ids = new List<int> { 2, 1, 2 };
        var tools = await fixture.Service.GetToolsForFunctionConfigurationsAsync(ids, 73, fixture.CallerToken);

        Assert.Equal(new[] { 2, 1, 2 }, ids);
        var read = Assert.Single(fixture.Reads);
        Assert.NotSame(ids, read.Ids);
        Assert.Equal(new[] { 1, 2 }, read.Values);
        Assert.Equal(new[] { "func_2_tavily___answer", "production_exa_search" }, tools.Select(tool => tool.Function.Name));
        Assert.Equal("Explicit answer description", tools[0].Function.Description);
        Assert.Equal("Exa.ai Search - Search the web for information", tools[1].Function.Description);
        Assert.All(tools, tool => Assert.Equal("function", tool.Type));
        fixture.AssertBoundary(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MappingKeepsOriginalIdsAndPermitsMissingDisabledRowsAndLastReturnedNameCollisions(bool runtime)
    {
        using var fixture = new Fixture(runtime, caching: true);
        fixture.ExpectedReadToken = fixture.CallerToken;
        var disabled = Configuration(1, "SHARED-NAME");
        disabled.IsEnabled = false;
        fixture.Rows = [Configuration(2, "Shared Name"), disabled];
        var ids = new List<int> { 3, 2, 1, 2 };
        var mapping = await fixture.Service.GetFunctionNameToIdMappingAsync(ids, fixture.CallerToken);

        var read = Assert.Single(fixture.Reads);
        Assert.Same(ids, read.Ids);
        Assert.Equal(new[] { 3, 2, 1, 2 }, read.Values);
        Assert.Equal(new[] { 3, 2, 1, 2 }, ids);
        Assert.Single(mapping);
        Assert.Equal(new FunctionRoute(1), mapping["shared_name"]);
        fixture.AssertBoundary(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ToolLoadingPreservesExactMissingAndDisabledErrors(bool runtime, bool disabled)
    {
        using var fixture = new Fixture(runtime);
        var row = Configuration(2, "Present");
        if (disabled)
        {
            row.IsEnabled = false;
            fixture.Rows = [row, Configuration(1, "Other Disabled")];
            fixture.Rows[1].IsEnabled = false;
        }
        else fixture.Rows = [row];
        var ids = new List<int> { 3, 2, 1, 2 };
        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.GetToolsForFunctionConfigurationsAsync(
            disabled ? [2, 1, 2] : ids, 73, fixture.CallerToken));
        Assert.Equal(disabled ? "Function configurations are disabled: 2, 1" : "Function configurations not found: 1, 3", error.Message);
        Assert.Equal(new[] { 3, 2, 1, 2 }, ids);
        fixture.AssertBoundary(1);
    }

    [Theory]
    [InlineData(false, "valid")]
    [InlineData(true, "valid")]
    [InlineData(false, "invalid")]
    [InlineData(true, "invalid")]
    [InlineData(false, "array")]
    [InlineData(true, "array")]
    [InlineData(false, "literal-null")]
    [InlineData(true, "literal-null")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    public async Task FixedProviderSchemasUseGeneratedObjectMetadataWithoutConstructingAClient(bool runtime, string schema)
    {
        using var fixture = new Fixture(runtime);
        var row = Configuration(1, "Fixed Search");
        row.Description = "  preserved explicit description  ";
        row.ParameterSchema = schema switch
        {
            "valid" => """{"type":"object","properties":{"query":{"type":"string","description":"雪"}},"required":["query"],"additionalProperties":false,"optional":null}""",
            "invalid" => "{ malformed",
            "array" => "[]",
            "literal-null" => "null",
            _ => null
        };
        fixture.Rows = [row];
        var tool = Assert.Single(await fixture.Service.GetToolsForFunctionConfigurationsAsync([1], 73, fixture.CallerToken));
        Assert.Equal("fixed_search", tool.Function.Name);
        Assert.Equal(row.Description, tool.Function.Description);
        if (schema == "valid")
        {
            Assert.True(JsonNode.DeepEquals(Schema(row.ParameterSchema), tool.Function.Parameters));
            Assert.Equal("雪", tool.Function.Parameters["properties"]["query"]["description"].GetValue<string>());
        }
        else Assert.Null(tool.Function.Parameters);
        Assert.Equal(schema is "invalid" or "array", fixture.Log.Entries.Any(entry => entry.Level == LogLevel.Warning));
        fixture.AssertBoundary(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpToolSchemasDescriptionsUnicodeNamesAndRoutesKeepTheirProviderNamesAndToken(bool runtime)
    {
        using var fixture = new Fixture(runtime);
        fixture.Rows = [Configuration(7, "123 Acme-MCP 雪!", FunctionProviderType.Mcp), Configuration(1, "Fixed Search")];
        fixture.EnableMcp(7);
        var longName = "Get" + new string('x', 90);
        fixture.Discovered =
        [
            new() { Name = "Search-Web!", Description = " ", ParametersSchema = Schema("""{"type":"object","properties":{"query":{"type":"string"}}}""") },
            new() { Name = "Fetch URL 雪", Description = "Fetch 雪", ParametersSchema = null },
            new() { Name = longName, Description = "Long tool", ParametersSchema = Schema("""{"type":"object","additionalProperties":false}""") }
        ];
        var tools = await fixture.Service.GetToolsForFunctionConfigurationsAsync([7, 1], 73, fixture.CallerToken);
        var routes = await fixture.Service.GetFunctionNameToIdMappingAsync([7, 1], fixture.CallerToken);
        var names = new[]
        {
            "fn_123_acme_mcp_雪__search_web", "fn_123_acme_mcp_雪__fetch_url_雪",
            ("fn_123_acme_mcp_雪__" + longName.ToLowerInvariant())[..64], "fixed_search"
        };
        Assert.Equal(names, tools.Select(tool => tool.Function.Name));
        Assert.Equal("MCP Server tool 'Search-Web!'", tools[0].Function.Description);
        Assert.Equal("Fetch 雪", tools[1].Function.Description);
        Assert.Same(fixture.Discovered[0].ParametersSchema, tools[0].Function.Parameters);
        Assert.Null(tools[1].Function.Parameters);
        Assert.Same(fixture.Discovered[2].ParametersSchema, tools[2].Function.Parameters);
        for (var index = 0; index < fixture.Discovered.Count; index++)
            Assert.Equal(new FunctionRoute(7, fixture.Discovered[index].Name), routes[names[index]]);
        Assert.Equal(new FunctionRoute(1), routes["fixed_search"]);
        Assert.Equal(4, routes.Count);
        Assert.All(tools, tool => Assert.True(tool.Function.Name.Length <= 64));
        fixture.AssertBoundary(2, factoryCalls: 2, listCalls: 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpClientsWithoutDiscoveryCapabilityExposeNoToolsOrRoutes(bool runtime)
    {
        using var fixture = new Fixture(runtime);
        fixture.Rows = [Configuration(7, "MCP", FunctionProviderType.Mcp)];
        fixture.EnableMcp(7, dynamic: false);
        Assert.Empty(await fixture.Service.GetToolsForFunctionConfigurationsAsync([7], 73, fixture.CallerToken));
        Assert.Empty(await fixture.Service.GetFunctionNameToIdMappingAsync([7], fixture.CallerToken));
        Assert.Equal(2, fixture.Log.Entries.Count(entry => entry.Level == LogLevel.Warning &&
            entry.Message.Contains("does not support tool discovery")));
        fixture.AssertBoundary(2, factoryCalls: 2);
    }

    [Theory]
    [InlineData(false, "factory")]
    [InlineData(true, "factory")]
    [InlineData(false, "list")]
    [InlineData(true, "list")]
    [InlineData(false, "cancelled")]
    [InlineData(true, "cancelled")]
    public async Task DynamicErrorsAreWrappedForToolsExceptCancellationAndRemainUnwrappedForMapping(bool runtime, string failure)
    {
        using var fixture = new Fixture(runtime);
        fixture.Rows = [Configuration(7, "MCP", FunctionProviderType.Mcp)];
        fixture.EnableMcp(7);
        Exception original = failure == "cancelled"
            ? new OperationCanceledException("discovery cancelled", fixture.CallerToken)
            : new IOException("dynamic discovery failed");
        if (failure == "factory") fixture.FactoryError = original;
        else fixture.ListError = original;
        if (failure == "cancelled")
            Assert.Same(original, await Assert.ThrowsAsync<OperationCanceledException>(() =>
                fixture.Service.GetToolsForFunctionConfigurationsAsync([7], 73, fixture.CallerToken)));
        else
        {
            var wrapped = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Service.GetToolsForFunctionConfigurationsAsync([7], 73, fixture.CallerToken));
            Assert.Equal("Failed to convert function configuration 7 to Tool", wrapped.Message);
            Assert.Same(original, wrapped.InnerException);
        }
        var mappingError = await Record.ExceptionAsync(() => fixture.Service.GetFunctionNameToIdMappingAsync([7], fixture.CallerToken));
        Assert.Same(original, mappingError);
        fixture.AssertBoundary(2, factoryCalls: 2, listCalls: failure == "factory" ? 0 : 2);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PersistenceReadErrorsRemainUnwrappedAndNeverFallBack(bool runtime, bool cancelled)
    {
        using var fixture = new Fixture(runtime);
        Exception original = cancelled
            ? new OperationCanceledException("read cancelled", fixture.CallerToken)
            : new IOException("configuration read failed");
        fixture.ReadError = original;
        Assert.Same(original, await Record.ExceptionAsync(() =>
            fixture.Service.GetToolsForFunctionConfigurationsAsync([1], 73, fixture.CallerToken)));
        Assert.Same(original, await Record.ExceptionAsync(() =>
            fixture.Service.GetFunctionNameToIdMappingAsync([1], fixture.CallerToken)));
        fixture.AssertBoundary(2);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CacheLoadsUseTheirOwnTokenAndMinimumNullableTtlAndHitsSkipPersistence(bool runtime, bool noTtl)
    {
        using var fixture = new Fixture(runtime, caching: true);
        var first = Configuration(1, "First");
        var second = Configuration(2, "Second");
        var third = Configuration(7, "MCP", FunctionProviderType.Mcp);
        first.CacheTtlMinutes = noTtl ? null : 7;
        second.CacheTtlMinutes = noTtl ? null : 2;
        third.CacheTtlMinutes = null;
        fixture.Rows = [third, second, first];
        fixture.EnableMcp(7);
        var ids = new List<int> { 7, 2, 1, 7 };
        var tools = await fixture.Service.GetToolsForFunctionConfigurationsAsync(ids, 73, fixture.CallerToken);
        Assert.NotEqual(fixture.CallerToken, fixture.LoaderToken);
        var read = Assert.Single(fixture.Reads);
        Assert.Equal(fixture.LoaderToken, read.Token);
        Assert.Equal(new[] { 1, 2, 7 }, read.Values);
        Assert.NotNull(fixture.LastLoad);
        Assert.Equal(noTtl ? (int?)null : 2, fixture.LastLoad.TtlMinutes);
        Assert.Same(fixture.LastLoad.Tools, tools);
        Assert.Equal(new[] { 7, 2, 1, 7 }, ids);
        if (!noTtl)
        {
            // The cache owns hits and its loader token. A different virtual key
            // still addresses the same normalized configuration set.
            fixture.ReadError = new IOException("a cache hit must not load");
            var hit = await fixture.Service.GetToolsForFunctionConfigurationsAsync([2, 7, 1], 999, fixture.CallerToken);
            Assert.Same(tools, hit);
        }
        Assert.All(fixture.CacheIds, value => Assert.Equal(new[] { 1, 2, 7 }, value));
        fixture.AssertBoundary(1, factoryCalls: 1, listCalls: 1, cacheCalls: noTtl ? 1 : 2);
    }

    private static FunctionConfiguration Configuration(int id, string name, FunctionProviderType provider = FunctionProviderType.Exa) => new()
    {
        Id = id, ConfigurationName = name, ProviderType = provider, Purpose = FunctionPurpose.Search, IsEnabled = true
    };

    private static JsonObject? Schema(string json) => JsonSerializer.Deserialize(json, AsyncTaskJsonContext.Default.JsonObject);

    private sealed record ReadAttempt(IReadOnlyList<int> Ids, int[] Values, CancellationToken Token);

    private sealed class Fixture : IDisposable
    {
        private readonly bool _runtime;
        private readonly CancellationTokenSource _caller = new();
        private readonly CancellationTokenSource _loader = new();
        private List<Tool>? _cached;
        private Mock<IDynamicToolProvider>? _dynamic;
        private int _dynamicId;
        public CancellationToken CallerToken => _caller.Token;
        public CancellationToken LoaderToken => _loader.Token;
        public CancellationToken ExpectedReadToken { get; set; }
        public FunctionDiscoveryService Service { get; }
        public Mock<IFunctionConfigurationRepository> Repository { get; } = new(MockBehavior.Strict);
        public Mock<IFunctionExecutionConfigurationStore> Store { get; } = new(MockBehavior.Strict);
        public Mock<IFunctionClientFactory> Factory { get; } = new(MockBehavior.Strict);
        public Mock<IFunctionClient> Client { get; } = new(MockBehavior.Strict);
        public Mock<IFunctionDiscoveryCacheService> Cache { get; } = new(MockBehavior.Strict);
        public RecordingLogger Log { get; } = new();
        public List<FunctionConfiguration> Rows { get; set; } = [];
        public IReadOnlyList<DiscoveredTool> Discovered { get; set; } =
        [new() { Name = "search", Description = "Search fixture", ParametersSchema = Schema("""{"type":"object"}""") }];
        public List<ReadAttempt> Reads { get; } = [];
        public List<int[]> CacheIds { get; } = [];
        public FunctionDiscoveryLoad? LastLoad { get; private set; }
        public Exception? ReadError { get; set; }
        public Exception? FactoryError { get; set; }
        public Exception? ListError { get; set; }

        public Fixture(bool runtime, bool caching = false)
        {
            _runtime = runtime;
            ExpectedReadToken = caching ? LoaderToken : CallerToken;
            Repository.Setup(value => value.GetByIdsAsync(It.IsAny<List<int>>(),
                    It.Is<CancellationToken>(token => token == ExpectedReadToken)))
                .Returns((List<int> ids, CancellationToken token) =>
                {
                    Reads.Add(new ReadAttempt(ids, ids.ToArray(), token));
                    return ReadError == null ? Task.FromResult(Rows) : Task.FromException<List<FunctionConfiguration>>(ReadError);
                });
            Store.Setup(value => value.GetConfigurationsAsync(It.IsAny<IReadOnlyList<int>>(),
                    It.Is<CancellationToken>(token => token == ExpectedReadToken)))
                .Returns((IReadOnlyList<int> ids, CancellationToken token) =>
                {
                    Reads.Add(new ReadAttempt(ids, ids.ToArray(), token));
                    return ReadError == null ? Task.FromResult<IReadOnlyList<FunctionConfiguration>>(Rows)
                        : Task.FromException<IReadOnlyList<FunctionConfiguration>>(ReadError);
                });
            Cache.Setup(value => value.GetOrLoadAsync(It.IsAny<List<int>>(),
                    It.IsAny<Func<CancellationToken, Task<FunctionDiscoveryLoad>>>(), null, CallerToken))
                .Returns(async (List<int> ids, Func<CancellationToken, Task<FunctionDiscoveryLoad>> load, int? ttl, CancellationToken token) =>
                {
                    Assert.Equal(CallerToken, token);
                    Assert.Null(ttl);
                    CacheIds.Add(ids.ToArray());
                    if (_cached != null) return _cached;
                    LastLoad = await load(LoaderToken);
                    if (LastLoad.TtlMinutes.HasValue) _cached = LastLoad.Tools;
                    return LastLoad.Tools;
                });
            Service = runtime
                ? FunctionDiscoveryService.CreateWithRuntimePersistence(Store.Object, Factory.Object, caching ? Cache.Object : null, Log)
                : new FunctionDiscoveryService(Repository.Object, Factory.Object, caching ? Cache.Object : null, Log);
        }

        public void CancelCaller() => _caller.Cancel();

        public void EnableMcp(int configurationId, bool dynamic = true)
        {
            _dynamicId = configurationId;
            if (dynamic)
            {
                _dynamic = Client.As<IDynamicToolProvider>();
                _dynamic.Setup(value => value.ListToolsAsync(It.Is<CancellationToken>(token => token == ExpectedReadToken)))
                    .Returns(() => ListError == null ? Task.FromResult(Discovered)
                        : Task.FromException<IReadOnlyList<DiscoveredTool>>(ListError));
            }
            Factory.Setup(value => value.GetClientAsync(FunctionProviderType.Mcp, configurationId))
                .Returns(() => FactoryError == null ? Task.FromResult(Client.Object) : Task.FromException<IFunctionClient>(FactoryError));
        }

        public void AssertBoundary(int reads, int factoryCalls = 0, int listCalls = 0, int cacheCalls = 0)
        {
            Assert.Equal(reads, Reads.Count);
            Assert.All(Reads, read => Assert.Equal(ExpectedReadToken, read.Token));
            if (_runtime)
            {
                Store.Verify(value => value.GetConfigurationsAsync(It.IsAny<IReadOnlyList<int>>(),
                    It.Is<CancellationToken>(token => token == ExpectedReadToken)), Times.Exactly(reads));
                Store.VerifyNoOtherCalls();
                Repository.VerifyNoOtherCalls();
            }
            else
            {
                Repository.Verify(value => value.GetByIdsAsync(It.IsAny<List<int>>(),
                    It.Is<CancellationToken>(token => token == ExpectedReadToken)), Times.Exactly(reads));
                Repository.VerifyNoOtherCalls();
                Store.VerifyNoOtherCalls();
            }
            Factory.Verify(value => value.GetClientAsync(FunctionProviderType.Mcp, _dynamicId), Times.Exactly(factoryCalls));
            Factory.VerifyNoOtherCalls();
            if (_dynamic != null)
                _dynamic.Verify(value => value.ListToolsAsync(It.Is<CancellationToken>(token => token == ExpectedReadToken)), Times.Exactly(listCalls));
            Client.VerifyNoOtherCalls();
            Cache.Verify(value => value.GetOrLoadAsync(It.IsAny<List<int>>(),
                It.IsAny<Func<CancellationToken, Task<FunctionDiscoveryLoad>>>(), null, CallerToken), Times.Exactly(cacheCalls));
            Cache.VerifyNoOtherCalls();
        }

        public void Dispose() { _caller.Dispose(); _loader.Dispose(); }
    }

    private sealed class RecordingLogger : ILogger<FunctionDiscoveryService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
