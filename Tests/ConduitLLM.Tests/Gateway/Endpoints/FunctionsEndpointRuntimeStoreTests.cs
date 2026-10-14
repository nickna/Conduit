using System.Net;
using System.Security.Claims;
using System.Text.Json;

using ConduitLLM.Core.Configuration;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Functions.Entities;
using ConduitLLM.Functions.Enums;
using ConduitLLM.Functions.Exceptions;
using ConduitLLM.Functions.Interfaces;
using ConduitLLM.Functions.Serialization;
using ConduitLLM.Functions.Services;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.UsageTracking;
using ConduitLLM.Persistence.Interfaces;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

public sealed class FunctionsEndpointRuntimeStoreTests
{
    [Fact]
    public void ExistingRepositoryConstructorRemainsTheOnlyPublicConstructor()
    {
        Assert.NotNull(typeof(FunctionsEndpoints).GetConstructor(
        [
            typeof(IFunctionExecutionService), typeof(IFunctionConfigurationRepository),
            typeof(FunctionParameterValidationService), typeof(IEventPublisher),
            typeof(IHttpContextAccessor), typeof(ILogger<FunctionsEndpoints>), typeof(IProviderErrorTranslator)
        ]));
        Assert.Single(typeof(FunctionsEndpoints).GetConstructors());
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "not-an-integer")]
    [InlineData(true, "not-an-integer")]
    public async Task AuthenticationRejectsExecuteAndReadBeforeAnyPersistence(bool runtime, string claim)
    {
        using var fixture = new Fixture(runtime);
        fixture.Context.User = claim == null
            ? new ClaimsPrincipal()
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim("VirtualKeyId", claim)], "fixture"));
        using var execute = await fixture.RenderAsync(await fixture.ExecuteAsync(), 401);
        using var read = await fixture.RenderAsync(await fixture.Handler.GetExecution(fixture.Execution.Id, fixture.Token), 401);
        AssertError(execute, "Invalid authentication", "invalid_auth", "authentication_error");
        AssertError(read, "Invalid authentication", "invalid_auth", "authentication_error");
        Assert.Null(fixture.Context.GetRequestAccountingSnapshot());
        fixture.AssertBoundary(0, 0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullRequestIsBadRequestBeforeAuthenticationAndPersistence(bool runtime)
    {
        using var fixture = new Fixture(runtime);
        fixture.Context.User = new ClaimsPrincipal();
        using var body = await fixture.RenderAsync(
            await fixture.Handler.ExecuteFunction(null, Fixture.Key, fixture.Token), 400);
        AssertError(body, "Request body is required", "invalid_request", "invalid_request_error");
        fixture.AssertBoundary(0, 0, 0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MissingAndDisabledConfigurationsRejectBeforeExecution(bool runtime, bool disabled)
    {
        using var fixture = new Fixture(runtime);
        if (disabled) fixture.Configuration.IsEnabled = false;
        else fixture.Configuration = null;
        using var body = await fixture.RenderAsync(await fixture.ExecuteAsync(), disabled ? 400 : 404);
        AssertError(body,
            $"Function configuration {Fixture.ConfigurationId} {(disabled ? "is disabled" : "not found")}",
            disabled ? "invalid_request" : "not_found", disabled ? "invalid_request_error" : "not_found_error");
        Assert.Null(fixture.Context.GetRequestAccountingSnapshot());
        Assert.False(fixture.Context.Items.ContainsKey("ProviderId"));
        fixture.AssertBoundary(1, 0, 0);
    }

    [Theory]
    [InlineData(false, "invalid", 400)]
    [InlineData(true, "invalid", 400)]
    [InlineData(false, "unexpected", 500)]
    [InlineData(true, "unexpected", 500)]
    [InlineData(false, "cancelled", 500)]
    [InlineData(true, "cancelled", 500)]
    public async Task ConfigurationLookupFailuresPreserveResponsesWithoutExecutionAccountingOrFallback(
        bool runtime, string failure, int status)
    {
        using var fixture = new Fixture(runtime);
        const string detail = "configuration lookup failed";
        Exception error = failure switch
        {
            "invalid" => new InvalidOperationException(detail),
            "cancelled" => new OperationCanceledException(detail, fixture.Token),
            _ => new Exception(detail)
        };
        if (runtime)
            fixture.Store.Setup(value => value.GetConfigurationAsync(Fixture.ConfigurationId, fixture.Token)).ThrowsAsync(error);
        else
            fixture.Repository.Setup(value => value.GetByIdAsync(Fixture.ConfigurationId, fixture.Token)).ThrowsAsync(error);

        using var body = await fixture.RenderAsync(await fixture.ExecuteAsync(), status);
        AssertError(body, status == 400 ? detail : "An unexpected error occurred during function execution",
            status == 400 ? "invalid_request" : "internal_error", status == 400 ? "invalid_request_error" : "server_error");
        Assert.Null(fixture.Context.GetRequestAccountingSnapshot());
        Assert.False(fixture.Context.Items.ContainsKey("ProviderId"));
        Assert.False(fixture.Context.Items.ContainsKey("ProviderType"));
        Assert.False(fixture.Context.Items.ContainsKey("FunctionConfigurationId"));
        Assert.False(fixture.Context.Items.ContainsKey("FunctionConfigurationName"));
        Assert.False(fixture.Context.Items.ContainsKey("FunctionExecutionId"));
        fixture.AssertBoundary(1, 0, 0);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task RequiredAndNumericTypeValidationUseTheActualValidationService(bool runtime, bool missingRequired)
    {
        using var fixture = new Fixture(runtime);
        fixture.Request.Parameters = Dictionary(missingRequired
            ? """{"numResults":3}"""
            : """{"query":"fixture","numResults":"three"}""");
        var message = missingRequired
            ? "Missing required parameter: 'query'"
            : "Parameter 'numResults' must be a number";
        using var body = await fixture.RenderAsync(await fixture.ExecuteAsync(), 400);
        AssertError(body, "Parameter validation failed: " + message, "invalid_request", "invalid_request_error");
        Assert.Contains(fixture.EndpointLog.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains(message));
        Assert.Null(fixture.Context.GetRequestAccountingSnapshot());
        fixture.AssertBoundary(1, 0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedSchemaLogsWarningsAndStillExecutesTheOriginalRequest(bool runtime)
    {
        using var fixture = new Fixture(runtime);
        fixture.Configuration.ParameterSchema = "{ malformed";
        using var body = await fixture.RenderAsync(await fixture.ExecuteAsync());
        Assert.Equal("completed", body.RootElement.GetProperty("status").GetString());
        Assert.Contains(fixture.EndpointLog.Entries, entry => entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Schema validation error, proceeding without validation"));
        Assert.Contains(fixture.ValidationLog.Entries, entry => entry.Level == LogLevel.Error);
        fixture.AssertForwardedRequest();
        fixture.AssertBoundary(1, 1, 0);
    }

    [Theory]
    [InlineData(false, "string")]
    [InlineData(true, "string")]
    [InlineData(false, "boolean")]
    [InlineData(true, "boolean")]
    [InlineData(false, "bounded-number")]
    [InlineData(true, "bounded-number")]
    [InlineData(false, "select")]
    [InlineData(true, "select")]
    public async Task JsonBoundValuesPreserveTheInheritedTypeAndNumericBoundValidationBehavior(bool runtime, string type)
    {
        using var fixture = new Fixture(runtime);
        var value = type switch { "string" => "\"fixture\"", "boolean" => "true", "select" => "\"not-listed\"", _ => "999" };
        fixture.Request = JsonSerializer.Deserialize<FunctionsEndpoints.FunctionExecutionRequest>(
            "{\"function_configuration_id\":" + Fixture.ConfigurationId + ",\"parameters\":{\"value\":" + value + "}}",
            fixture.WireOptions);
        fixture.Configuration.ParameterSchema = type switch
        {
            "bounded-number" => """{"optional":{"value":{"type":"number","min":1,"max":2}}}""",
            "select" => """{"optional":{"value":{"type":"select","options":["allowed"]}}}""",
            _ => "{\"optional\":{\"value\":{\"type\":\"" + type + "\"}}}"
        };
        Assert.IsType<JsonElement>(fixture.Request.Parameters["value"]);
        var accepted = type is "bounded-number" or "select";
        using var body = await fixture.RenderAsync(await fixture.ExecuteAsync(), accepted ? 200 : 400);
        if (accepted)
        {
            if (type == "bounded-number")
            {
                Assert.Equal(999, ((JsonElement)fixture.ExecutedParameters["value"]).GetInt32());
                Assert.Contains(fixture.ValidationLog.Entries, entry => entry.Level == LogLevel.Warning &&
                    entry.Message.Contains("Error validating parameter type for value"));
            }
            else
                Assert.Equal("not-listed", ((JsonElement)fixture.ExecutedParameters["value"]).GetString());
            fixture.AssertForwardedRequest();
            fixture.AssertBoundary(1, 1, 0);
        }
        else
        {
            AssertError(body, $"Parameter validation failed: Parameter 'value' must be a {(type == "string" ? "string" : "boolean")}",
                "invalid_request", "invalid_request_error");
            fixture.AssertBoundary(1, 0, 0);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullParametersAreExecutedAsAnEmptyDictionaryWithoutChangingTheRequest(bool runtime)
    {
        using var fixture = new Fixture(runtime);
        fixture.Request.Parameters = null;
        fixture.Configuration.ParameterSchema = null;
        using var body = await fixture.RenderAsync(await fixture.ExecuteAsync());
        Assert.NotNull(fixture.ExecutedParameters);
        Assert.Empty(fixture.ExecutedParameters);
        Assert.Null(fixture.Request.Parameters);
        fixture.AssertForwardedRequest();
        fixture.AssertBoundary(1, 1, 0);
    }

    [Theory]
    [InlineData(false, "actual")]
    [InlineData(true, "actual")]
    [InlineData(false, "estimate")]
    [InlineData(true, "estimate")]
    [InlineData(false, "zero")]
    [InlineData(true, "zero")]
    public async Task SuccessfulDtoAndAccountingPreserveProductionJsonAndCostFallbacks(bool runtime, string costSource)
    {
        using var fixture = new Fixture(runtime);
        fixture.Execution.ActualCost = costSource == "actual" ? .75m : null;
        fixture.Execution.EstimatedCost = costSource == "zero" ? null : 1.25m;
        var expectedCost = costSource switch { "actual" => .75m, "estimate" => 1.25m, _ => 0m };
        using var body = await fixture.RenderAsync(await fixture.ExecuteAsync());
        AssertExecutionContract(body.RootElement, fixture.Execution);
        fixture.AssertForwardedRequest();
        Assert.Equal(FunctionProviderType.Exa, Assert.IsType<FunctionProviderType>(fixture.Context.Items["ProviderId"]));
        Assert.Equal(FunctionProviderType.Exa, Assert.IsType<FunctionProviderType>(fixture.Context.Items["ProviderType"]));
        Assert.Equal(Fixture.ConfigurationId, fixture.Context.Items["FunctionConfigurationId"]);
        Assert.Equal(fixture.Configuration.ConfigurationName, fixture.Context.Items["FunctionConfigurationName"]);
        Assert.Equal(fixture.Execution.Id, fixture.Context.Items["FunctionExecutionId"]);
        Assert.Equal((object)fixture.Execution.EstimatedCost, fixture.Context.Items["EstimatedCost"]);
        Assert.Equal((object)fixture.Execution.ActualCost, fixture.Context.Items["ActualCost"]);
        var accounting = fixture.Context.GetRequestAccountingSnapshot();
        Assert.NotNull(accounting);
        Assert.Equal(RequestOperation.Function, accounting.Operation);
        Assert.Equal(Fixture.VirtualKeyId, accounting.VirtualKeyId);
        Assert.Equal(fixture.Configuration.ConfigurationName, accounting.RequestedModel);
        Assert.NotNull(accounting.DirectCost);
        Assert.Equal(expectedCost, accounting.DirectCost.ActualCost);
        Assert.Equal(fixture.Configuration.ConfigurationName, accounting.DirectCost.OperationName);
        Assert.Equal(fixture.Execution.Id.ToString(), accounting.DirectCost.ExecutionId);
        using var costMetadata = JsonDocument.Parse(accounting.DirectCost.MetadataJson);
        Assert.Equal(Fixture.ConfigurationId, costMetadata.RootElement.GetProperty("functionConfigurationId").GetInt32());
        Assert.Equal(fixture.Execution.Id, costMetadata.RootElement.GetProperty("executionId").GetGuid());
        Assert.Equal("Completed", costMetadata.RootElement.GetProperty("state").GetString());
        Assert.Equal(3, costMetadata.RootElement.EnumerateObject().Count());
        fixture.AssertBoundary(1, 1, 0);
    }

    [Theory]
    [InlineData(false, "rate-limit", 429, "rate_limit_exceeded", "rate_limit_error")]
    [InlineData(true, "rate-limit", 429, "rate_limit_exceeded", "rate_limit_error")]
    [InlineData(false, "provider-auth", 502, "provider_authentication_error", "server_error")]
    [InlineData(true, "provider-auth", 502, "provider_authentication_error", "server_error")]
    [InlineData(false, "provider-unavailable", 503, "provider_unavailable", "server_error")]
    [InlineData(true, "provider-unavailable", 503, "provider_unavailable", "server_error")]
    [InlineData(false, "invalid", 400, "invalid_request", "invalid_request_error")]
    [InlineData(true, "invalid", 400, "invalid_request", "invalid_request_error")]
    [InlineData(false, "unexpected", 500, "internal_error", "server_error")]
    [InlineData(true, "unexpected", 500, "internal_error", "server_error")]
    [InlineData(false, "cancelled", 500, "internal_error", "server_error")]
    [InlineData(true, "cancelled", 500, "internal_error", "server_error")]
    public async Task ExecutionExceptionsPreserveProviderMappingsAndInheritedCancellationResponse(
        bool runtime, string failure, int status, string code, string type)
    {
        using var fixture = new Fixture(runtime);
        const string detail = "provider or execution detail";
        fixture.ExecutionError = failure switch
        {
            "rate-limit" => new FunctionCommunicationException("Exa", detail, HttpStatusCode.TooManyRequests, "private provider body"),
            "provider-auth" => new FunctionCommunicationException("Exa", detail, HttpStatusCode.Unauthorized, "private provider body"),
            "provider-unavailable" => new FunctionCommunicationException("Exa", detail, HttpStatusCode.ServiceUnavailable, "private provider body"),
            "invalid" => new InvalidOperationException(detail),
            "cancelled" => new OperationCanceledException(detail, fixture.Token),
            _ => new Exception(detail)
        };
        using var body = await fixture.RenderAsync(await fixture.ExecuteAsync(), status);
        var expectedMessage = failure switch
        {
            "rate-limit" => "The model provider rate-limited this request. Please retry later.",
            "provider-auth" => "The model provider rejected the request due to an upstream configuration issue.",
            "provider-unavailable" => "The model provider is temporarily unavailable. Please retry later.",
            "invalid" => detail,
            _ => "An unexpected error occurred during function execution"
        };
        AssertError(body, expectedMessage, code, type);
        Assert.DoesNotContain("private provider body", body.RootElement.GetRawText());
        fixture.AssertForwardedRequest();
        var accounting = fixture.Context.GetRequestAccountingSnapshot();
        Assert.Equal(RequestOperation.Function, accounting.Operation);
        Assert.Null(accounting.DirectCost);
        Assert.False(fixture.Context.Items.ContainsKey("FunctionExecutionId"));
        fixture.AssertBoundary(1, 1, 0);
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "other-owner")]
    [InlineData(true, "other-owner")]
    [InlineData(false, "own")]
    [InlineData(true, "own")]
    public async Task ExecutionReadHidesOtherOwnersAndNeverReadsConfiguration(bool runtime, string ownership)
    {
        using var fixture = new Fixture(runtime);
        if (ownership == "missing") fixture.ReadExecution = null;
        if (ownership == "other-owner") fixture.Execution.VirtualKeyId = Fixture.VirtualKeyId + 1;
        using var body = await fixture.RenderAsync(
            await fixture.Handler.GetExecution(fixture.Execution.Id, fixture.Token), ownership == "own" ? 200 : 404);
        if (ownership == "own") AssertExecutionContract(body.RootElement, fixture.Execution);
        else AssertError(body, $"Function execution {fixture.Execution.Id} not found", "not_found", "not_found_error");
        Assert.Null(fixture.Context.GetRequestAccountingSnapshot());
        Assert.False(fixture.Context.Items.ContainsKey("ProviderId"));
        fixture.AssertBoundary(0, 0, 1);
    }

    private static void AssertError(JsonDocument document, string message, string code, string type)
    {
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(message, error.GetProperty("message").GetString());
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(type, error.GetProperty("type").GetString());
        Assert.Single(document.RootElement.EnumerateObject());
    }

    private static void AssertExecutionContract(JsonElement root, FunctionExecution execution)
    {
        Assert.Equal(execution.Id, root.GetProperty("id").GetGuid());
        Assert.Equal(Fixture.ConfigurationId, root.GetProperty("function_id").GetInt32());
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal("雪 fixture", root.GetProperty("input").GetProperty("parameters").GetProperty("query").GetString());
        Assert.Equal(Fixture.Key, root.GetProperty("input").GetProperty("idempotencyKey").GetString());
        Assert.Equal("fixture-trace", root.GetProperty("input").GetProperty("metadata").GetProperty("trace").GetString());
        Assert.Equal("雪 result", root.GetProperty("output").GetProperty("results")[0].GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("output").GetProperty("optional").ValueKind);
        Assert.Equal(execution.RequestedAt, root.GetProperty("created_at").GetDateTime());
        Assert.Equal(execution.StartedAt, root.GetProperty("started_at").GetDateTime());
        Assert.Equal(execution.CompletedAt, root.GetProperty("completed_at").GetDateTime());
        Assert.Equal(13L, root.GetProperty("duration_ms").GetInt64());
        Assert.False(root.TryGetProperty("error", out _));
        Assert.False(root.TryGetProperty("virtual_key_id", out _));
        Assert.False(root.TryGetProperty("function_configuration", out _));
        Assert.False(root.TryGetProperty("audit_events", out _));
        Assert.Equal(10, root.EnumerateObject().Count());
        var cost = root.GetProperty("cost");
        Assert.Equal("USD", cost.GetProperty("currency").GetString());
        Assert.Equal(execution.EstimatedCost.HasValue, cost.TryGetProperty("estimated", out var estimated));
        if (execution.EstimatedCost.HasValue) Assert.Equal(execution.EstimatedCost.Value, estimated.GetDecimal());
        Assert.Equal(execution.ActualCost.HasValue, cost.TryGetProperty("actual", out var actual));
        if (execution.ActualCost.HasValue) Assert.Equal(execution.ActualCost.Value, actual.GetDecimal());
        Assert.Equal(3, cost.GetProperty("breakdown").GetProperty("units").GetInt32());
        Assert.True(cost.GetProperty("breakdown").GetProperty("provider").GetProperty("reported").GetBoolean());
        Assert.Equal(2 + (execution.EstimatedCost.HasValue ? 1 : 0) + (execution.ActualCost.HasValue ? 1 : 0), cost.EnumerateObject().Count());
    }

    private static Dictionary<string, object> Dictionary(string json) =>
        JsonSerializer.Deserialize(json, FunctionsJsonContext.Default.DictionaryStringObject);

    private sealed class Fixture : IDisposable
    {
        public const int ConfigurationId = 9253;
        public const int VirtualKeyId = 4853;
        public const string Key = "endpoint-idempotency";
        private readonly bool _runtime;
        private readonly CancellationTokenSource _source = new();
        private readonly ServiceProvider _services;
        public CancellationToken Token => _source.Token;
        public JsonSerializerOptions WireOptions { get; } = GatewayJsonOptions.Create();
        public DefaultHttpContext Context { get; }
        public FunctionsEndpoints Handler { get; }
        public Mock<IFunctionConfigurationRepository> Repository { get; } = new(MockBehavior.Strict);
        public Mock<IFunctionExecutionConfigurationStore> Store { get; } = new(MockBehavior.Strict);
        public Mock<IFunctionExecutionService> Service { get; } = new(MockBehavior.Strict);
        public Mock<IEventPublisher> Events { get; } = new(MockBehavior.Strict);
        public RecordingLogger<FunctionsEndpoints> EndpointLog { get; } = new();
        public RecordingLogger<FunctionParameterValidationService> ValidationLog { get; } = new();
        public FunctionConfiguration? Configuration { get; set; } = new()
        {
            Id = ConfigurationId, ConfigurationName = "Search 雪", ProviderType = FunctionProviderType.Exa,
            IsEnabled = true, ParameterSchema = """{"required":["query"],"optional":{"numResults":{"type":"number"}}}"""
        };
        public FunctionsEndpoints.FunctionExecutionRequest Request { get; set; } = new()
        {
            FunctionConfigurationId = ConfigurationId,
            Parameters = Dictionary("""{"query":"雪 fixture","numResults":3,"filters":{"sites":["fixture.test"]}}"""),
            Metadata = Dictionary("""{"trace":"fixture-trace","nested":{"enabled":true},"optional":null}""")
        };
        public FunctionExecution Execution { get; }
        public FunctionExecution? ReadExecution { get; set; }
        public Exception? ExecutionError { get; set; }
        public Dictionary<string, object>? ExecutedParameters { get; private set; }
        public Dictionary<string, object>? ExecutedMetadata { get; private set; }
        public string? ExecutedKey { get; private set; }
        public string? ExecutedToolName { get; private set; }
        public CancellationToken ExecutedToken { get; private set; }

        public Fixture(bool runtime)
        {
            _runtime = runtime;
            _services = new ServiceCollection().AddLogging()
                .Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
                {
                    options.SerializerOptions.TypeInfoResolverChain.Clear();
                    GatewayJsonOptions.Configure(options.SerializerOptions);
                }).BuildServiceProvider();
            Context = new DefaultHttpContext
            {
                RequestServices = _services,
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("VirtualKeyId", VirtualKeyId.ToString())], "fixture"))
            };
            Execution = new FunctionExecution
            {
                Id = Guid.NewGuid(), FunctionConfigurationId = ConfigurationId, VirtualKeyId = VirtualKeyId,
                State = ExecutionState.Completed, ExecutionMode = ExecutionMode.Asynchronous,
                RequestedAt = new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc),
                StartedAt = new DateTime(2026, 10, 11, 12, 0, 1, DateTimeKind.Utc),
                CompletedAt = new DateTime(2026, 10, 11, 12, 0, 2, DateTimeKind.Utc),
                Duration = TimeSpan.FromMilliseconds(12.5), EstimatedCost = 1.25m, ActualCost = .75m,
                RequestJson = JsonSerializer.Serialize(new FunctionExecutionRequestData
                {
                    Parameters = Request.Parameters, Metadata = Request.Metadata, IdempotencyKey = Key
                }, FunctionsJsonContext.Default.FunctionExecutionRequestData),
                ResponseJson = """{"results":[{"title":"雪 result"}],"optional":null}""",
                CostCalculationDetails = """{"units":3,"provider":{"reported":true}}""",
                FunctionConfiguration = Configuration
            };
            Configuration.Executions.Add(Execution);
            ReadExecution = Execution;
            Repository.Setup(value => value.GetByIdAsync(ConfigurationId, Token)).Returns(() => Task.FromResult(Configuration));
            Store.Setup(value => value.GetConfigurationAsync(ConfigurationId, Token)).Returns(() => Task.FromResult(Configuration));
            Service.Setup(value => value.ExecuteAsync(ConfigurationId, VirtualKeyId,
                    It.IsAny<Dictionary<string, object>>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object>>(), null, Token))
                .Returns((int _, int __, Dictionary<string, object> parameters, string key,
                    Dictionary<string, object> metadata, string toolName, CancellationToken token) =>
                {
                    ExecutedParameters = parameters;
                    ExecutedMetadata = metadata;
                    ExecutedKey = key;
                    ExecutedToolName = toolName;
                    ExecutedToken = token;
                    return ExecutionError == null ? Task.FromResult(Execution) : Task.FromException<FunctionExecution>(ExecutionError);
                });
            Service.Setup(value => value.GetExecutionAsync(Execution.Id, Token)).Returns(() => Task.FromResult(ReadExecution));
            var validation = new FunctionParameterValidationService(ValidationLog);
            var accessor = new HttpContextAccessor { HttpContext = Context };
            var errorTranslator = new ProviderErrorTranslator(new CustomerErrorOptions { Mode = CustomerErrorMode.External });
            Handler = runtime
                ? FunctionsEndpoints.CreateWithRuntimePersistence(Service.Object, Store.Object, validation, Events.Object, accessor, EndpointLog, errorTranslator)
                : new FunctionsEndpoints(Service.Object, Repository.Object, validation, Events.Object, accessor, EndpointLog, errorTranslator);
        }

        public Task<IResult> ExecuteAsync() => Handler.ExecuteFunction(Request, Key, Token);

        public void AssertForwardedRequest()
        {
            if (Request.Parameters != null) Assert.Same(Request.Parameters, ExecutedParameters);
            Assert.Same(Request.Metadata, ExecutedMetadata);
            Assert.Equal(Key, ExecutedKey);
            Assert.Null(ExecutedToolName);
            Assert.Equal(Token, ExecutedToken);
        }

        public void AssertBoundary(int configurationReads, int executions, int executionReads)
        {
            if (_runtime)
            {
                Store.Verify(value => value.GetConfigurationAsync(ConfigurationId, Token), Times.Exactly(configurationReads));
                Store.VerifyNoOtherCalls();
                Repository.VerifyNoOtherCalls();
            }
            else
            {
                Repository.Verify(value => value.GetByIdAsync(ConfigurationId, Token), Times.Exactly(configurationReads));
                Repository.VerifyNoOtherCalls();
                Store.VerifyNoOtherCalls();
            }
            Service.Verify(value => value.ExecuteAsync(ConfigurationId, VirtualKeyId,
                It.IsAny<Dictionary<string, object>>(), Key, It.IsAny<Dictionary<string, object>>(), null, Token), Times.Exactly(executions));
            Service.Verify(value => value.GetExecutionAsync(Execution.Id, Token), Times.Exactly(executionReads));
            Service.VerifyNoOtherCalls();
            Events.VerifyNoOtherCalls();
        }

        public async Task<JsonDocument> RenderAsync(IResult result, int status = 200)
        {
            var context = new DefaultHttpContext { RequestServices = _services };
            await using var body = new MemoryStream();
            context.Response.Body = body;
            await result.ExecuteAsync(context);
            Assert.Equal(status, context.Response.StatusCode);
            Assert.StartsWith("application/json", context.Response.ContentType);
            body.Position = 0;
            return await JsonDocument.ParseAsync(body);
        }

        public void Dispose()
        {
            _source.Dispose();
            _services.Dispose();
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }
}
