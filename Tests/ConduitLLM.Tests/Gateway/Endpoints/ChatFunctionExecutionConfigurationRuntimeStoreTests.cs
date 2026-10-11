using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Gateway.Constants;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.UsageTracking;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

/// <summary>
/// Runs both configuration-read implementations through the actual chat routes,
/// with the Gateway's generated JSON contracts and no reflection resolver.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Component", "FunctionRuntimePersistence")]
public sealed class ChatFunctionExecutionConfigurationRuntimeStoreTests
{
    private const string StandardRoute = "/v1/chat/completions";
    private const string EnhancedRoute = "/v1/conduit/chat/completions";

    [Theory]
    [InlineData(false, StandardRoute)]
    [InlineData(true, StandardRoute)]
    [InlineData(false, EnhancedRoute)]
    [InlineData(true, EnhancedRoute)]
    public async Task EnabledConfigurations_KeepBoundIdsTokenMetadataAndGeneratedResponse(bool runtime, string route)
    {
        await using var fixture = new Fixture(runtime);
        fixture.Rows = [Configuration(8), Configuration(0), Configuration(4), Configuration(-2)];
        fixture.AllowIterationBounds(2, 6);
        fixture.AllowProvider();

        using var response = await fixture.PostAsync(route, Request([-2, 4, 4, 0, 8]));

        await AssertCompletionAsync(response, fixture, hasFunctions: true);
        fixture.AssertRead([-2, 4, 4, 0, 8]);
        Assert.Same(fixture.ReadIds, fixture.ProviderRequest.FunctionConfigurationIds);
        Assert.Same(fixture.ReadIds, fixture.ToolIds);
        Assert.Same(fixture.ReadIds, fixture.RouteIds);
        Assert.Equal(fixture.ReadToken, fixture.ProviderToken);
        Assert.Equal(fixture.ReadToken, fixture.ToolToken);
        Assert.Equal(fixture.ReadToken, fixture.RouteToken);
        Assert.Equal(["min", "max"], fixture.SettingsCalls);
        Assert.Equal(3, fixture.ProviderRequest.MaxAgenticIterations);
        Assert.False(fixture.ProviderRequest.EnableAgenticMode);
        Assert.Equal("session-α", fixture.ProviderRequest.SessionId);
        Assert.Equal(JsonValueKind.String, Assert.IsType<JsonElement>(fixture.ProviderRequest.Messages[0].Content).ValueKind);
        Assert.Equal("Hello 世界", Assert.IsType<JsonElement>(fixture.ProviderRequest.Messages[0].Content).GetString());
        fixture.AssertProviderCalls(hasFunctions: true);

        var accounting = fixture.Accounting;
        Assert.NotNull(accounting);
        Assert.Equal(RequestOperation.ChatCompletion, accounting.Operation);
        Assert.Equal(1, accounting.VirtualKeyId);
        Assert.Equal("test-model", accounting.RequestedModel);
        Assert.Equal(UsageEvidenceSource.Provider, accounting.ProviderUsage.Source);
        Assert.Equal(5, accounting.ProviderUsage.Usage.TotalTokens);
        Assert.Single(accounting.ProviderCalls);
        Assert.Empty(accounting.FunctionExecutions);
        Assert.Null(accounting.DirectCost);
        Assert.Equal(accounting.BillingRequestId, response.Headers.GetValues("x-request-id").Single());
    }

    [Theory]
    [InlineData(false, StandardRoute)]
    [InlineData(true, StandardRoute)]
    [InlineData(false, EnhancedRoute)]
    [InlineData(true, EnhancedRoute)]
    public async Task MissingIds_PrecedeDisabledRowsAndIterationValidation(bool runtime, string route)
    {
        await using var fixture = new Fixture(runtime);
        fixture.Rows = [Configuration(8, enabled: false)];

        using var response = await fixture.PostAsync(route, Request([9, 8, 9, 6, -1, 6], iterations: 100));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest,
            "Function configuration IDs not found: 9, 6, -1", "invalid_function_configuration_ids");
        fixture.AssertRead([9, 8, 9, 6, -1, 6]);
        fixture.AssertGuardStopped();
        Assert.Empty(fixture.SettingsCalls);
    }

    [Theory]
    [InlineData(false, StandardRoute)]
    [InlineData(true, StandardRoute)]
    [InlineData(false, EnhancedRoute)]
    [InlineData(true, EnhancedRoute)]
    public async Task DisabledRows_KeepReturnedOrderAndDuplicatesBeforeIterationValidation(bool runtime, string route)
    {
        await using var fixture = new Fixture(runtime);
        fixture.Rows = [Configuration(3, false), Configuration(5, false), Configuration(3, false)];

        using var response = await fixture.PostAsync(route, Request([5, 3, 5], iterations: 100));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest,
            "Function configurations are disabled: 3, 5, 3", "disabled_function_configurations");
        fixture.AssertRead([5, 3, 5]);
        fixture.AssertGuardStopped();
        Assert.Empty(fixture.SettingsCalls);
    }

    [Theory]
    [InlineData(false, StandardRoute, 2)]
    [InlineData(true, StandardRoute, 2)]
    [InlineData(false, EnhancedRoute, 8)]
    [InlineData(true, EnhancedRoute, 8)]
    public async Task EnabledRows_ValidateExplicitIterationBoundsAfterReading(bool runtime, string route, int iterations)
    {
        await using var fixture = new Fixture(runtime);
        fixture.Rows = [Configuration(4)];
        fixture.AllowIterationBounds(3, 7);

        using var response = await fixture.PostAsync(route, Request([4, 4], iterations));

        await AssertErrorAsync(response, HttpStatusCode.BadRequest,
            "MaxAgenticIterations must be between 3 and 7", "invalid_max_agentic_iterations");
        fixture.AssertRead([4, 4]);
        fixture.AssertGuardStopped();
        Assert.Equal(["min", "max"], fixture.SettingsCalls);
    }

    [Theory]
    [InlineData(false, StandardRoute, false)]
    [InlineData(true, StandardRoute, false)]
    [InlineData(false, EnhancedRoute, true)]
    [InlineData(true, EnhancedRoute, true)]
    public async Task NullOrEmptyIds_SkipConfigurationAndIterationValidation(bool runtime, string route, bool empty)
    {
        await using var fixture = new Fixture(runtime);
        fixture.AllowProvider();

        using var response = await fixture.PostAsync(route, Request(empty ? [] : null, iterations: 999));

        await AssertCompletionAsync(response, fixture, hasFunctions: false);
        Assert.Empty(fixture.Repository.Invocations);
        Assert.Empty(fixture.Store.Invocations);
        Assert.Empty(fixture.SettingsCalls);
        Assert.Equal(999, fixture.ProviderRequest.MaxAgenticIterations);
        if (empty)
            Assert.Empty(fixture.ProviderRequest.FunctionConfigurationIds);
        else
            Assert.Null(fixture.ProviderRequest.FunctionConfigurationIds);
        fixture.AssertProviderCalls(hasFunctions: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullableFlagsAndOptionalServices_ApplyDefaultsOnlyAfterConfigurationValidation(bool runtime)
    {
        await using var fixture = new Fixture(runtime) { NullEventPublisher = true };
        fixture.Rows = [Configuration(4)];
        fixture.AllowDefaults(maximum: 7, agentic: true);
        fixture.AllowProvider();

        using var response = await fixture.PostAsync(StandardRoute, Request([4], iterations: null, agentic: null));

        await AssertCompletionAsync(response, fixture, hasFunctions: true);
        fixture.AssertRead([4]);
        Assert.Equal(0, fixture.SettingsCallsAtRead);
        Assert.Equal(["max", "default"], fixture.SettingsCalls);
        Assert.Equal(7, fixture.ProviderRequest.MaxAgenticIterations);
        Assert.True(fixture.ProviderRequest.EnableAgenticMode);
        fixture.AssertProviderCalls(hasFunctions: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OmittedFlags_KeepDtoInitializersAndOnlyValidateTheirExplicitIterationValue(bool runtime)
    {
        await using var fixture = new Fixture(runtime);
        fixture.Rows = [Configuration(4)];
        fixture.AllowIterationBounds(minimum: 2, maximum: 6);
        fixture.AllowProvider();

        using var response = await fixture.PostAsync(StandardRoute,
            Request([4], iterations: null, agentic: null), preserveExplicitNullFlags: false);

        await AssertCompletionAsync(response, fixture, hasFunctions: true);
        fixture.AssertRead([4]);
        Assert.Equal(["min", "max"], fixture.SettingsCalls);
        Assert.Equal(5, fixture.ProviderRequest.MaxAgenticIterations);
        Assert.True(fixture.ProviderRequest.EnableAgenticMode);
        fixture.AssertProviderCalls(hasFunctions: true);
    }

    [Theory]
    [InlineData(false, "invalid")]
    [InlineData(true, "invalid")]
    [InlineData(false, "unexpected")]
    [InlineData(true, "unexpected")]
    [InlineData(false, "cancelled")]
    [InlineData(true, "cancelled")]
    public async Task ConfigurationReadFailures_IncludingCancellation_KeepValidation500WithoutFallback(bool runtime, string failure)
    {
        await using var fixture = new Fixture(runtime);
        fixture.ReadFailure = token => failure switch
        {
            "invalid" => new InvalidOperationException("private configuration diagnostic"),
            "cancelled" => new OperationCanceledException("private cancellation diagnostic", token),
            _ => new Exception("private store diagnostic")
        };

        using var response = await fixture.PostAsync(EnhancedRoute, Request([4, 4], iterations: null, agentic: null));

        await AssertErrorAsync(response, HttpStatusCode.InternalServerError,
            "Error validating function calling request", "function_validation_error", "server_error");
        fixture.AssertRead([4, 4]);
        fixture.AssertGuardStopped();
        Assert.Empty(fixture.SettingsCalls);
    }

    [Fact]
    public async Task LegacyUnavailableRepository_KeepsDistinct500BeforeDefaultsAndAccounting()
    {
        await using var fixture = new Fixture(runtime: false) { UnavailableRepository = true };

        using var response = await fixture.PostAsync(EnhancedRoute, Request([4], iterations: null, agentic: null));

        await AssertErrorAsync(response, HttpStatusCode.InternalServerError,
            "Function calling is not configured on this server", "function_calling_unavailable", "server_error");
        Assert.Empty(fixture.Repository.Invocations);
        Assert.Empty(fixture.Store.Invocations);
        Assert.Empty(fixture.SettingsCalls);
        fixture.AssertGuardStopped();
    }

    [Fact]
    public async Task PublicConstructor_RemainsTheSingleFourteenArgumentRepositoryConstructor()
    {
        var constructor = Assert.Single(typeof(ChatEndpoints).GetConstructors());
        var parameters = constructor.GetParameters();
        Assert.Equal(14, parameters.Length);
        Assert.Equal(typeof(IFunctionConfigurationRepository), parameters[7].ParameterType);
        Assert.All(parameters.Skip(7), parameter => Assert.True(parameter.IsOptional));

        await using var legacy = new Fixture(runtime: false) { NullEventPublisher = true, UnavailableRepository = true };
        await using var runtime = new Fixture(runtime: true) { NullEventPublisher = true };
        Assert.NotNull(legacy.CreateHandler());
        Assert.NotNull(runtime.CreateHandler());
    }

    [Theory]
    [InlineData(false, "conduit")]
    [InlineData(true, "conduit")]
    [InlineData(false, "logger")]
    [InlineData(true, "logger")]
    [InlineData(false, "modelMappingService")]
    [InlineData(true, "modelMappingService")]
    [InlineData(false, "jsonSerializerOptions")]
    [InlineData(true, "jsonSerializerOptions")]
    [InlineData(false, "globalSettingsCacheService")]
    [InlineData(true, "globalSettingsCacheService")]
    [InlineData(false, "usageEstimationService")]
    [InlineData(true, "usageEstimationService")]
    [InlineData(false, "httpContextAccessor")]
    [InlineData(true, "httpContextAccessor")]
    [InlineData(true, "configurationStore")]
    public async Task ConstructorsAndFactory_PreserveRequiredArgumentGuardNames(bool runtime, string parameter)
    {
        await using var fixture = new Fixture(runtime);
        var exception = Assert.Throws<ArgumentNullException>(() => fixture.CreateHandler(parameter));
        Assert.Equal(parameter, exception.ParamName);
    }

    private static ChatCompletionRequest Request(List<int>? ids, int? iterations = 3, bool? agentic = false) => new()
    {
        Model = "test-model",
        Messages = [new Message { Role = "user", Content = "Hello 世界" }],
        Stream = false,
        SessionId = "session-α",
        FunctionConfigurationIds = ids,
        MaxAgenticIterations = iterations,
        EnableAgenticMode = agentic
    };

    private static FunctionConfiguration Configuration(int id, bool enabled = true) => new()
    {
        Id = id,
        ConfigurationName = $"configuration-{id}",
        IsEnabled = enabled
    };

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status,
        string message, string code, string type = "invalid_request_error")
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType.MediaType);
        Assert.False(response.Headers.Contains("x-request-id"));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(["error"], root.EnumerateObject().Select(property => property.Name).ToArray());
        var error = root.GetProperty("error");
        Assert.Equal(message, error.GetProperty("message").GetString());
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(type, error.GetProperty("type").GetString());
        Assert.False(error.TryGetProperty("param", out _));
        Assert.False(error.TryGetProperty("metadata", out _));
    }

    private static async Task AssertCompletionAsync(HttpResponseMessage response, Fixture fixture, bool hasFunctions)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType.MediaType);
        var payload = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        Assert.Equal("chatcmpl-runtime-config", root.GetProperty("id").GetString());
        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        Assert.Equal("provider-model", root.GetProperty("model").GetString());
        Assert.Equal(1_760_000_000L, root.GetProperty("created").GetInt64());
        var choice = Assert.Single(root.GetProperty("choices").EnumerateArray());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
        Assert.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("Answer 世界 🚀", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(2, root.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(3, root.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
        Assert.Equal(5, root.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.False(root.TryGetProperty("system_fingerprint", out _));
        Assert.False(root.TryGetProperty("original_model_alias", out _));
        Assert.False(root.TryGetProperty("provider_tool_usage", out _));
        if (hasFunctions)
        {
            var metrics = root.GetProperty("agentic_metrics");
            Assert.Equal(1, metrics.GetProperty("total_iterations").GetInt32());
            Assert.Equal(0, metrics.GetProperty("total_function_calls").GetInt32());
            Assert.Empty(metrics.GetProperty("function_calls").EnumerateArray());
            Assert.False(metrics.TryGetProperty("provider_calls", out _));
        }
        else
        {
            Assert.False(root.TryGetProperty("agentic_metrics", out _));
        }

        // The same generated-only options also consume the actual HTTP response.
        var decoded = JsonSerializer.Deserialize<ChatCompletionResponse>(payload, fixture.WireOptions);
        Assert.NotNull(decoded);
        Assert.Equal("chatcmpl-runtime-config", decoded.Id);
        Assert.Equal(5, decoded.Usage.TotalTokens);
        Assert.All(fixture.WireOptions.TypeInfoResolverChain,
            resolver => Assert.IsNotType<DefaultJsonTypeInfoResolver>(resolver));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly bool _runtime;
        private readonly HttpContextAccessor _accessor = new();
        private readonly Conduit _conduit;
        private readonly TaskCompletionSource _snapshotCaptured = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private GatewayEndpointTestHost? _host;
        private HttpContext Context => _accessor.HttpContext
            ?? throw new InvalidOperationException("The server request is not active.");

        public readonly Mock<IFunctionConfigurationRepository> Repository = new(MockBehavior.Strict);
        public readonly Mock<IFunctionExecutionConfigurationStore> Store = new(MockBehavior.Strict);
        public readonly Mock<IModelProviderMappingService> Mappings = new(MockBehavior.Strict);
        public readonly Mock<IGlobalSettingsCacheService> Settings = new(MockBehavior.Strict);
        public readonly Mock<IUsageEstimationService> UsageEstimator = new(MockBehavior.Strict);
        public readonly Mock<ILLMClientFactory> ClientFactory = new(MockBehavior.Strict);
        public readonly Mock<ILLMClient> Client = new(MockBehavior.Strict);
        public readonly Mock<IFunctionDiscoveryService> Discovery = new(MockBehavior.Strict);
        public readonly Mock<IAgenticOrchestrationService> Orchestration = new(MockBehavior.Strict);
        public readonly Mock<IEventPublisher> Events = new(MockBehavior.Strict);
        public readonly JsonSerializerOptions WireOptions = GatewayJsonOptions.Create();
        public readonly List<string> SettingsCalls = [];

        public IReadOnlyList<FunctionConfiguration> Rows = [];
        public Func<CancellationToken, Exception>? ReadFailure;
        public bool UnavailableRepository;
        public bool NullEventPublisher;
        public IReadOnlyDictionary<object, object?> Metadata;
        public RequestAccountingSnapshot? Accounting;
        public string ResponseRequestId;
        public IReadOnlyList<int> ReadIds;
        public CancellationToken ReadToken;
        public int SettingsCallsAtRead;
        public bool AccountingExistedAtRead;
        public bool RequestIdExistedAtRead;
        public ChatCompletionRequest ProviderRequest;
        public CancellationToken ProviderToken;
        public List<int> ToolIds;
        public CancellationToken ToolToken;
        public List<int> RouteIds;
        public CancellationToken RouteToken;

        public Fixture(bool runtime)
        {
            _runtime = runtime;
            _conduit = new Conduit(ClientFactory.Object, NullLogger<Conduit>.Instance,
                functionDiscoveryService: Discovery.Object, agenticOrchestrationService: Orchestration.Object);
            Mappings.Setup(service => service.GetMappingByModelAliasAsync("test-model"))
                .Returns(() =>
                {
                    return Task.FromResult(new ModelProviderMapping
                    {
                        Id = 11,
                        ModelAlias = "test-model",
                        ProviderModelId = "provider-model",
                        ProviderId = 77,
                        Provider = new Provider
                        {
                            ProviderType = ProviderType.OpenAI,
                            TrustProviderReportedCosts = true,
                            ProviderCostMarkupMultiplier = 1.25m
                        },
                        ModelProviderTypeAssociation = new ModelProviderTypeAssociation { ModelCostId = 81 }
                    });
                });
            Repository.Setup(repository => repository.GetByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
                .Returns((List<int> ids, CancellationToken token) => Task.FromResult(Read(ids, token).ToList()));
            Store.Setup(store => store.GetConfigurationsAsync(It.IsAny<IReadOnlyList<int>>(), It.IsAny<CancellationToken>()))
                .Returns((IReadOnlyList<int> ids, CancellationToken token) => Task.FromResult(Read(ids, token)));
        }

        private IReadOnlyList<FunctionConfiguration> Read(IReadOnlyList<int> ids, CancellationToken token)
        {
            ReadIds = ids;
            ReadToken = token;
            Assert.Equal(Context.RequestAborted, token);
            SettingsCallsAtRead = SettingsCalls.Count;
            AccountingExistedAtRead = Context.GetRequestAccountingSnapshot() is not null;
            RequestIdExistedAtRead = Context.Response.Headers.ContainsKey("x-request-id");
            if (ReadFailure is not null)
                throw ReadFailure(token);
            return Rows;
        }

        public void AllowIterationBounds(int minimum, int maximum)
        {
            Settings.Setup(service => service.GetMinAgenticIterationsAsync())
                .Returns(() => { SettingsCalls.Add("min"); return Task.FromResult(minimum); });
            Settings.Setup(service => service.GetMaxAgenticIterationsAsync())
                .Returns(() => { SettingsCalls.Add("max"); return Task.FromResult(maximum); });
        }

        public void AllowDefaults(int maximum, bool agentic)
        {
            Settings.Setup(service => service.GetMaxAgenticIterationsAsync())
                .Returns(() => { SettingsCalls.Add("max"); return Task.FromResult(maximum); });
            Settings.Setup(service => service.GetDefaultAgenticModeEnabledAsync())
                .Returns(() => { SettingsCalls.Add("default"); return Task.FromResult(agentic); });
        }

        public void AllowProvider()
        {
            ClientFactory.Setup(factory => factory.GetClientForChatAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Client.Object);
            Client.Setup(client => client.CreateChatCompletionAsync(It.IsAny<ChatCompletionRequest>(), null, It.IsAny<CancellationToken>()))
                .Returns((ChatCompletionRequest request, string apiKey, CancellationToken token) =>
                {
                    ProviderRequest = request;
                    ProviderToken = token;
                    Assert.Equal(Context.RequestAborted, token);
                    Assert.Equal(RequestOperation.ChatCompletion, Context.GetRequestAccountingSnapshot().Operation);
                    return Task.FromResult(new ChatCompletionResponse
                    {
                        Id = "chatcmpl-runtime-config",
                        Object = "chat.completion",
                        Model = "provider-model",
                        Created = 1_760_000_000,
                        Choices = [new Choice
                        {
                            Index = 0,
                            FinishReason = "stop",
                            Message = new Message { Role = "assistant", Content = "Answer 世界 🚀" }
                        }],
                        Usage = new Usage { PromptTokens = 2, CompletionTokens = 3, TotalTokens = 5 },
                        OriginalModelAlias = "server-only-alias"
                    });
                });
            Discovery.Setup(service => service.GetToolsForFunctionConfigurationsAsync(It.IsAny<List<int>>(), 1, It.IsAny<CancellationToken>()))
                .Returns((List<int> ids, int virtualKey, CancellationToken token) =>
                {
                    ToolIds = ids;
                    ToolToken = token;
                    return Task.FromResult(new List<Tool>());
                });
            Discovery.Setup(service => service.GetFunctionNameToIdMappingAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
                .Returns((List<int> ids, CancellationToken token) =>
                {
                    RouteIds = ids;
                    RouteToken = token;
                    return Task.FromResult(new Dictionary<string, FunctionRoute>());
                });
        }

        public ChatEndpoints CreateHandler(string nullParameter = null)
        {
            var conduit = nullParameter == "conduit" ? null : _conduit;
            var logger = nullParameter == "logger" ? null : NullLogger<ChatEndpoints>.Instance;
            var mappings = nullParameter == "modelMappingService" ? null : Mappings.Object;
            var options = nullParameter == "jsonSerializerOptions" ? null : WireOptions;
            var settings = nullParameter == "globalSettingsCacheService" ? null : Settings.Object;
            var estimator = nullParameter == "usageEstimationService" ? null : UsageEstimator.Object;
            var accessor = nullParameter == "httpContextAccessor" ? null : _accessor;
            var publisher = NullEventPublisher ? null : Events.Object;
            return _runtime
                ? ChatEndpoints.CreateWithRuntimePersistence(conduit, logger, mappings, options, publisher, settings, estimator,
                    nullParameter == "configurationStore" ? null : Store.Object, httpContextAccessor: accessor)
                : new ChatEndpoints(conduit, logger, mappings, options, publisher, settings, estimator,
                    UnavailableRepository ? null : Repository.Object, httpContextAccessor: accessor);
        }

        public async Task<HttpResponseMessage> PostAsync(string route, ChatCompletionRequest request,
            bool preserveExplicitNullFlags = true)
        {
            var virtualKeys = new Mock<IVirtualKeyRuntimeService>(MockBehavior.Strict);
            virtualKeys.Setup(service => service.ValidateVirtualKeyAsync("test-key", null))
                .ReturnsAsync(VirtualKeyValidationOutcome.Success(new VirtualKey
                {
                    Id = 1, KeyName = "test-key", KeyHash = "hash"
                }));
            _host = await GatewayEndpointTestHost.StartAsync(services =>
            {
                services.ConfigureHttpJsonOptions(options =>
                {
                    options.SerializerOptions.TypeInfoResolverChain.Clear();
                    GatewayJsonOptions.Configure(options.SerializerOptions);
                    Assert.All(options.SerializerOptions.TypeInfoResolverChain,
                        resolver => Assert.IsNotType<DefaultJsonTypeInfoResolver>(resolver));
                });
                services.AddSingleton<IHttpContextAccessor>(_accessor);
                services.AddSingleton<IVirtualKeyRuntimeService>(virtualKeys.Object);
                services.AddSingleton<IStartupFilter>(new CaptureRequestFilter(this));
                services.AddScoped(_ => CreateHandler());
            });
            using var content = new StringContent(SerializeRequest(request, preserveExplicitNullFlags), Encoding.UTF8, "application/json");
            var response = await _host.Client.PostAsync(route, content);
            try
            {
                await _snapshotCaptured.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        private string SerializeRequest(ChatCompletionRequest request, bool preserveExplicitNullFlags)
        {
            var generated = JsonSerializer.SerializeToElement(request, WireOptions);
            if (!preserveExplicitNullFlags)
                return generated.GetRawText();

            // These DTO members omit null when written and have non-null initializers
            // when bound. Send an explicit null to exercise the endpoint defaults.
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in generated.EnumerateObject())
                    property.WriteTo(writer);
                if (request.MaxAgenticIterations is null)
                    writer.WriteNull("max_agentic_iterations");
                if (request.EnableAgenticMode is null)
                    writer.WriteNull("enable_agentic_mode");
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private void CaptureRequest(HttpContext context)
        {
            Metadata = new Dictionary<object, object?>(context.Items);
            Accounting = context.GetRequestAccountingSnapshot();
            ResponseRequestId = context.Response.Headers["x-request-id"].ToString();
            _snapshotCaptured.TrySetResult();
        }

        private sealed class CaptureRequestFilter(Fixture fixture) : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
            {
                application.Use(nextRequest => async context =>
                {
                    try
                    {
                        await nextRequest(context);
                    }
                    finally
                    {
                        // TestServer releases its feature collection after this pipeline
                        // returns. Copy evidence while the server context is still live.
                        fixture.CaptureRequest(context);
                    }
                });
                next(application);
            };
        }

        public void AssertRead(int[] expected)
        {
            Assert.Equal(expected, ReadIds);
            Assert.False(AccountingExistedAtRead);
            Assert.False(RequestIdExistedAtRead);
            Assert.Equal(0, SettingsCallsAtRead);
            Assert.Single(_runtime ? Store.Invocations : Repository.Invocations);
            Assert.Empty(_runtime ? Repository.Invocations : Store.Invocations);
            AssertMetadata();
        }

        private void AssertMetadata()
        {
            Assert.NotNull(Metadata);
            Assert.False(Assert.IsType<bool>(Metadata["IsStreamingRequest"]));
            Assert.Equal(77, Assert.IsType<int>(Metadata["ProviderId"]));
            Assert.Equal(ProviderType.OpenAI, Assert.IsType<ProviderType>(Metadata["ProviderType"]));
            Assert.Equal(81, Assert.IsType<int>(Metadata[HttpContextKeys.ModelCostId]));
            var policy = Assert.IsType<ProviderCostBillingPolicy>(Metadata[HttpContextKeys.ProviderBillingPolicy]);
            Assert.True(policy.TrustProviderReportedCost);
            Assert.Equal(1.25m, policy.MarkupMultiplier);
        }

        public void AssertGuardStopped()
        {
            AssertMetadata();
            Assert.Null(Accounting);
            Assert.Empty(ResponseRequestId);
            Assert.Empty(ClientFactory.Invocations);
            Assert.Empty(Client.Invocations);
            Assert.Empty(Discovery.Invocations);
            Assert.Empty(Orchestration.Invocations);
            Assert.Empty(UsageEstimator.Invocations);
            Assert.Empty(Events.Invocations);
        }

        public void AssertProviderCalls(bool hasFunctions)
        {
            Assert.Single(ClientFactory.Invocations);
            Assert.Single(Client.Invocations);
            Assert.Equal(hasFunctions ? 2 : 0, Discovery.Invocations.Count);
            Assert.Empty(Orchestration.Invocations);
            Assert.Empty(UsageEstimator.Invocations);
            Assert.Empty(Events.Invocations);
            AssertMetadata();
            Assert.NotNull(Accounting);
            Assert.Equal(RequestOperation.ChatCompletion, Accounting.Operation);
            Assert.Equal(Accounting.BillingRequestId, ResponseRequestId);
        }

        public async ValueTask DisposeAsync()
        {
            if (_host is not null)
                await _host.DisposeAsync();
        }
    }
}
