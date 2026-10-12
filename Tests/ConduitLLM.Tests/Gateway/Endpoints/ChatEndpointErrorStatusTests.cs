using System.Net;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Options;

using AwesomeAssertions;

using Microsoft.Extensions.DependencyInjection;

using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

/// <summary>
/// End-to-end status-code contract for <c>POST /v1/chat/completions</c> (#1191).
///
/// These run over the real minimal-API pipeline — endpoint filters, the endpoint handler and
/// OpenAIErrorMiddleware — because the bug lived in the seams between them, not in any one unit.
/// <see cref="ExceptionToResponseMapper"/> already mapped ModelNotFoundException to 404 and had a
/// passing unit test, yet the live endpoint answered 500: two blanket catch handlers
/// (ChatEndpoints and RequireBalanceEndpointFilter) converted the exception first.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Component", "GatewayErrorContract")]
public sealed class ChatEndpointErrorStatusTests
{
    [Fact]
    public async Task ModelNotFoundException_Returns404ModelNotFound()
    {
        var response = await PostChatAsync(
            new ModelNotFoundException("ghost-model", "The model 'ghost-model' does not exist or is not available."));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var error = await ReadErrorAsync(response);
        error.Code.Should().Be("model_not_found");
        error.Type.Should().Be("invalid_request_error");
        error.Param.Should().Be("model");
        error.Message.Should().Contain("ghost-model");
    }

    [Fact]
    public async Task ServiceUnavailableException_Returns503()
    {
        // Route health exhaustion (open circuit / no usable credential) stays retryable.
        var response = await PostChatAsync(
            new ServiceUnavailableException("No healthy provider route for model 'test-model'.", "Routing"));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var error = await ReadErrorAsync(response);
        error.Code.Should().Be("service_unavailable");
    }

    [Fact]
    public async Task ValidationException_Returns400()
    {
        var response = await PostChatAsync(new ValidationException("messages collection cannot be null or empty"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var error = await ReadErrorAsync(response);
        error.Code.Should().Be("validation_error");
        error.Type.Should().Be("invalid_request_error");
    }

    [Fact]
    public async Task RateLimitExceededException_Returns429WithRetryAfter()
    {
        var response = await PostChatAsync(
            new RateLimitExceededException("Provider rate limit reached.", retryAfterSeconds: 30));

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter?.Delta.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(typeof(ModelNotFoundException))]
    [InlineData(typeof(ServiceUnavailableException))]
    [InlineData(typeof(ValidationException))]
    public async Task ClientErrors_AreNeverReportedAsBalanceCheckFailures(Type exceptionType)
    {
        // Guards the RequireBalanceEndpointFilter regression: it used to wrap next(context) in its
        // own try/catch, so every downstream exception came back as 500 balance_check_error.
        Exception exception = exceptionType == typeof(ModelNotFoundException)
            ? new ModelNotFoundException("test-model")
            : exceptionType == typeof(ServiceUnavailableException)
                ? new ServiceUnavailableException("No healthy provider route.", "Routing")
                : new ValidationException("bad input");

        var response = await PostChatAsync(exception);

        response.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);

        var error = await ReadErrorAsync(response);
        error.Code.Should().NotBe("balance_check_error");
        error.Code.Should().NotBe("internal_error");
    }

    [Fact]
    public async Task UnexpectedException_Returns500WithRedactedMessage()
    {
        // The catch-all still yields 500 — but must not echo internals back to the caller,
        // which the removed blanket catch did via OpenAIError(500, ex.Message, ...).
        var response = await PostChatAsync(
            new InvalidCastException("Unable to cast Npgsql.Internal.SecretConnectionString"));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var error = await ReadErrorAsync(response);
        error.Code.Should().Be("internal_error");
        error.Type.Should().Be("server_error");
        error.Message.Should().Be("An unexpected error occurred");
        error.Message.Should().NotContain("Npgsql");
    }

    [Fact]
    public async Task ProviderCommunicationError_DefaultExternalMode_SanitizesProviderText()
    {
        // CONDUIT_CUSTOMER_MODE defaults to External: raw provider bodies must not
        // reach the caller, while the status/code contract stays identical.
        var response = await PostChatAsync(new LLMCommunicationException(
            "API returned an error: 429 TooManyRequests - secret provider quota text",
            HttpStatusCode.TooManyRequests,
            "secret provider quota text"));

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var error = await ReadErrorAsync(response);
        error.Code.Should().Be("rate_limit_exceeded");
        error.Message.Should().NotContain("secret provider quota text");
        error.Message.Should().Contain("rate-limited");
        error.Metadata.Should().BeNull();
    }

    [Fact]
    public async Task BodyMissingRequiredMessages_Returns400WithOpenAIEnvelope()
    {
        // Minimal-API binding failure. Before ThrowOnBadRequest this was a bare 400 with an
        // empty body — not a valid OpenAI-compatible error response.
        await using var host = await StartHostAsync(new ModelNotFoundException("unused"));

        using var content = new StringContent(
            """{"model":"test-model"}""", Encoding.UTF8, "application/json");
        var response = await host.Client.PostAsync("/v1/chat/completions", content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var error = await ReadErrorAsync(response);
        error.Should().NotBeNull();
        error.Code.Should().Be("invalid_request_body");
        error.Type.Should().Be("invalid_request_error");
        error.Message.Should().NotContain("ConduitLLM.Core.Models");
    }

    [Theory]
    [InlineData("\"wrong\"")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("""{"function":{"name":"x"}}""")]
    [InlineData("""{"type":"wrong","function":{"name":"x"}}""")]
    [InlineData("""{"type":"custom","custom":{"name":"x"}}""")]
    [InlineData("""{"type":"allowed_tools","allowed_tools":{"mode":"auto","tools":[]}}""")]
    [InlineData("""{"type":1,"function":{"name":"x"}}""")]
    [InlineData("""{"type":"function","function":null}""")]
    [InlineData("""{"type":"function","function":"x"}""")]
    [InlineData("""{"type":"function","function":{}}""")]
    [InlineData("""{"type":"function","function":{"name":1}}""")]
    [InlineData("""{"type":"function","function":{"name":""}}""")]
    public async Task MalformedToolChoice_ReturnsOpenAI400(string choice)
    {
        await using var host = await StartHostAsync(new ModelNotFoundException("unused"));
        using var content = new StringContent(
            $$"""{"model":"test-model","messages":[{"role":"user","content":"Hello"}],"tool_choice":{{choice}}}""",
            Encoding.UTF8, "application/json");
        using var response = await host.Client.PostAsync("/v1/chat/completions", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.Type.Should().Be("invalid_request_error");
        error.Code.Should().Be("invalid_request_body");
    }

    [Theory]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[\"text\"]")]
    [InlineData("[{}]")]
    [InlineData("""[{"type":1}]""")]
    [InlineData("""[{"type":"wrong","text":"x"}]""")]
    [InlineData("""[{"type":"text"}]""")]
    [InlineData("""[{"type":"text","text":null}]""")]
    [InlineData("""[{"type":"text","text":42}]""")]
    [InlineData("""[{"type":"image_url","image_url":"https://example.com/a.png"}]""")]
    [InlineData("""[{"type":"image_url","image_url":{}}]""")]
    [InlineData("""[{"type":"image_url","image_url":{"url":"invalid-uri"}}]""")]
    [InlineData("""[{"type":"image_url","image_url":{"url":"https://example.com/a.png","detail":"wrong"}}]""")]
    [InlineData("""[{"type":"input_audio","input_audio":{"format":"wav"}}]""")]
    [InlineData("""[{"type":"input_audio","input_audio":{"data":"AAAA","format":"flac"}}]""")]
    [InlineData("""[{"type":"file","file":null}]""")]
    [InlineData("""[{"type":"file","file":{"file_id":42}}]""")]
    [InlineData("""[{"type":"refusal","refusal":"no"}]""")]
    [InlineData("""[{"type":"text","text":"hi","prompt_cache_breakpoint":{"mode":"wrong"}}]""")]
    public async Task MalformedContent_ReturnsOpenAI400BeforeProviderInvocation(string parts)
    {
        await using var host = await StartHostAsync(new ModelNotFoundException("unused"));
        using var content = new StringContent(
            $$"""{"model":"test-model","messages":[{"role":"user","content":{{parts}}}]}""",
            Encoding.UTF8, "application/json");
        using var response = await host.Client.PostAsync("/v1/chat/completions", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.Type.Should().Be("invalid_request_error");
        error.Code.Should().Be("invalid_chat_content");
        error.Param.Should().StartWith("messages[0].content");
    }

    [Theory]
    [InlineData("user", "\"Hello\"")]
    [InlineData("assistant", "null")]
    [InlineData("assistant", """[{"type":"refusal","refusal":"no"}]""")]
    [InlineData("user", """[{"type":"text","text":"Hi"},{"type":"image_url","image_url":{"url":"https://example.com/a.png","detail":"auto"}},{"type":"input_audio","input_audio":{"data":"AAAA","format":"wav"}},{"type":"file","file":{"file_id":"file-1"}}]""")]
    public async Task SupportedContentAndNamedFunction_ReachProvider(string role, string parts)
    {
        await using var host = await StartHostAsync(new ModelNotFoundException("provider-invoked"));
        using var content = new StringContent(
            $$$$"""{"model":"test-model","messages":[{"role":"{{{{role}}}}","content":{{{{parts}}}}}],"tool_choice":{"type":"function","function":{"name":"x"}}}""",
            Encoding.UTF8, "application/json");
        using var response = await host.Client.PostAsync("/v1/chat/completions", content);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadErrorAsync(response)).Code.Should().Be("model_not_found");
    }

    [Theory]
    [InlineData("/v1/chat/completions", HttpStatusCode.BadRequest, """[{"type":"future_part","provider_value":42}]""")]
    [InlineData("/v1/conduit/chat/completions", HttpStatusCode.NotFound, """[{"type":"future_part","provider_value":42}]""")]
    [InlineData("/v1/conduit/chat/completions/", HttpStatusCode.NotFound, """[{"type":"future_part","provider_value":42}]""")]
    [InlineData("/V1/Conduit/Chat/Completions", HttpStatusCode.NotFound, """[{"type":"future_part","provider_value":42}]""")]
    [InlineData("/v1/chat/completions", HttpStatusCode.BadRequest, """[{"type":"video_url","video_url":{"url":"https://example.com/a.mp4"}}]""")]
    [InlineData("/v1/conduit/chat/completions", HttpStatusCode.NotFound, """[{"type":"video_url","video_url":{"url":"https://example.com/a.mp4"}}]""")]
    [InlineData("/v1/chat/completions", HttpStatusCode.BadRequest, """[{"type":"input_audio","input_audio":{"data":"AAAA","format":"flac"}}]""")]
    [InlineData("/v1/conduit/chat/completions", HttpStatusCode.NotFound, """[{"type":"input_audio","input_audio":{"data":"AAAA","format":"flac"}}]""")]
    public async Task ProviderContent_IsExplicitlyLimitedToConduitRoute(string route, HttpStatusCode status, string parts)
    {
        await using var host = await StartHostAsync(new ModelNotFoundException("provider-invoked"));
        using var content = new StringContent(
            $$"""{"model":"test-model","messages":[{"role":"user","content":{{parts}}}]}""",
            Encoding.UTF8, "application/json");
        using var response = await host.Client.PostAsync(route, content);
        response.StatusCode.Should().Be(status);
    }

    [Theory]
    [InlineData("""[{"type":""}]""")]
    [InlineData("""[{"type":"text","text":42}]""")]
    [InlineData("""[{"type":"input_audio","input_audio":{"format":"flac"}}]""")]
    [InlineData("""[{"type":"video_url","video_url":{}}]""")]
    [InlineData("""[{"type":"video_url","video_url":{"url":"https://example.com/a.mp4","max_frames":"two"}}]""")]
    public async Task ConduitRoute_CannotBypassKnownPartValidation(string parts)
    {
        await using var host = await StartHostAsync(new ModelNotFoundException("unused"));
        using var content = new StringContent(
            $$"""{"model":"test-model","messages":[{"role":"user","content":{{parts}}}]}""",
            Encoding.UTF8, "application/json");
        using var response = await host.Client.PostAsync("/v1/conduit/chat/completions", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadErrorAsync(response)).Code.Should().Be("invalid_chat_content");
    }

    [Fact]
    public async Task ProviderContent_PreservesOrderAndUnknownFieldsWhenForwarding()
    {
        ChatCompletionRequest captured = null;
        await using var host = await StartHostAsync(new ModelNotFoundException("provider-invoked"), request => captured = request);
        const string parts = """[{"type":"text","text":"first"},{"type":"input_audio","input_audio":{"data":"AAAA","format":"flac"}},{"type":"video_url","video_url":{"url":"https://example.com/a.mp4"}},{"type":"future_part","provider_value":42}]""";
        using var content = new StringContent(
            $$"""{"model":"test-model","messages":[{"role":"user","content":{{parts}}}]}""",
            Encoding.UTF8, "application/json");
        using var response = await host.Client.PostAsync("/v1/conduit/chat/completions", content);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        captured.Should().NotBeNull();
        var forwarded = (JsonElement)captured.Messages[0].Content;
        forwarded.EnumerateArray().Select(part => part.GetProperty("type").GetString())
            .Should().Equal("text", "input_audio", "video_url", "future_part");
        forwarded[3].GetProperty("provider_value").GetInt32().Should().Be(42);
    }

    // -----------------------------------------------------------------------------------------

    private static async Task<HttpResponseMessage> PostChatAsync(Exception routingFailure)
    {
        await using var host = await StartHostAsync(routingFailure);

        using var content = new StringContent(
            """{"model":"test-model","messages":[{"role":"user","content":"Hello"}]}""",
            Encoding.UTF8, "application/json");
        return await host.Client.PostAsync("/v1/chat/completions", content);
    }

    /// <summary>
    /// Boots the Gateway endpoint pipeline with a client factory that fails model resolution,
    /// which is where every condition in #1191 originates.
    /// </summary>
    private static Task<GatewayEndpointTestHost> StartHostAsync(Exception routingFailure, Action<ChatCompletionRequest> onRequest = null)
    {
        var clientFactory = new Mock<ILLMClientFactory>();
        clientFactory
            .Setup(factory => factory.GetClientForChatAsync(
                It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ChatCompletionRequest, CancellationToken>((request, _) => onRequest?.Invoke(request))
            .ThrowsAsync(routingFailure);
        clientFactory
            .Setup(factory => factory.GetClientAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(routingFailure);

        var mappingService = new Mock<IModelProviderMappingService>();
        mappingService
            .Setup(service => service.GetMappingByModelAliasAsync(It.IsAny<string>()))
            .ReturnsAsync((ModelProviderMapping?)null);

        // The balance filter must pass through so the request actually reaches the handler.
        var virtualKeyService = new Mock<IVirtualKeyService>();
        virtualKeyService
            .Setup(service => service.ValidateVirtualKeyAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(VirtualKeyValidationOutcome.Success(new VirtualKey
            {
                Id = 1,
                KeyName = "test-key",
                KeyHash = "hash"
            }));

        return GatewayEndpointTestHost.StartAsync(services =>
        {
            services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.TypeInfoResolverChain.Clear();
                GatewayJsonOptions.Configure(options.SerializerOptions);
            });
            services.AddSingleton(clientFactory.Object);
            services.AddSingleton(mappingService.Object);
            services.AddSingleton(GatewayJsonOptions.Create());
            services.AddSingleton(Mock.Of<IEventBus>());
            services.AddSingleton(Mock.Of<IGlobalSettingsCacheService>());
            services.AddSingleton(Mock.Of<IUsageEstimationService>());
            services.AddSingleton(new Mock<ConduitLLM.Persistence.Interfaces.IFunctionExecutionConfigurationStore>(MockBehavior.Strict).Object);
            services.AddSingleton<IVirtualKeyRuntimeService>(virtualKeyService.Object);
            services.AddScoped<Conduit>();
        });
    }

    private static async Task<OpenAIError> ReadErrorAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotBeEmpty("every Gateway error must carry an OpenAI error envelope");

        var envelope = JsonSerializer.Deserialize<OpenAIErrorResponse>(
            payload, GatewayJsonOptions.Create());
        envelope.Should().NotBeNull();
        return envelope!.Error;
    }
}
