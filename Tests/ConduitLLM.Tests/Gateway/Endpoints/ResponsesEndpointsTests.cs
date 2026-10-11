using System.Security.Claims;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Responses;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.UsageTracking;

using AwesomeAssertions;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;

namespace ConduitLLM.Tests.Gateway.Endpoints;

public sealed class ResponsesEndpointsTests
{
    private static readonly JsonSerializerOptions JsonOptions = GatewayJsonOptions.Create();

    [Fact]
    public void Translate_MapsInstructionsAndStringInputToChat()
    {
        var request = Deserialize("""
            {
              "model": "model-a",
              "input": "Hello",
              "instructions": "Be concise",
              "store": false,
              "max_output_tokens": 64,
              "temperature": 0.25,
              "top_p": 0.75,
              "user": "user-1"
            }
            """);

        var result = ResponsesEndpoints.Translate(request);

        result.Error.Should().BeNull();
        result.Request.Should().NotBeNull();
        result.Request!.Messages.Should().HaveCount(2);
        result.Request.Messages[0].Role.Should().Be("developer");
        result.Request.Messages[0].Content.Should().Be("Be concise");
        result.Request.Messages[1].Role.Should().Be("user");
        result.Request.Messages[1].Content.Should().Be("Hello");
        result.Request.MaxCompletionTokens.Should().Be(64);
        result.Request.EnableAgenticMode.Should().BeFalse();
        result.Request.Store.Should().BeFalse();
    }

    [Fact]
    public void Translate_AcceptsTextMessageArray()
    {
        var request = Deserialize("""
            {
              "model": "model-a",
              "store": false,
              "input": [
                {"type": "message", "role": "system", "content": "System"},
                {"role": "user", "content": "Question"},
                {"role": "assistant", "content": "Answer"}
              ]
            }
            """);

        var result = ResponsesEndpoints.Translate(request);

        result.Error.Should().BeNull();
        result.Request!.Messages.Select(message => message.Role)
            .Should().Equal("system", "user", "assistant");
    }

    [Theory]
    [InlineData("""{"model":"m","input":"x"}""", "store")]
    [InlineData("""{"model":"m","input":"x","store":true}""", "store")]
    [InlineData("""{"input":"x","store":false}""", "model")]
    [InlineData("""{"model":"m","store":false}""", "input")]
    [InlineData("""{"model":"m","input":[],"store":false}""", "input")]
    [InlineData("""{"model":"m","input":42,"store":false}""", "input")]
    [InlineData("""{"model":"m","input":"x","store":false,"max_output_tokens":15}""", "max_output_tokens")]
    [InlineData("""{"model":"m","input":"x","store":false,"temperature":2.1}""", "temperature")]
    [InlineData("""{"model":"m","input":"x","store":false,"top_p":-0.1}""", "top_p")]
    [InlineData("""{"model":"m","input":"x","store":false,"future_field":true}""", "future_field")]
    public void Translate_RejectsInvalidCoreInput(string json, string parameter)
    {
        var result = ResponsesEndpoints.Translate(Deserialize(json));

        result.Error.Should().NotBeNull();
        result.Error!.Parameter.Should().Be(parameter);
    }

    [Theory]
    [InlineData("background", "true")]
    [InlineData("context_management", "[{}]")]
    [InlineData("conversation", "\"conv_1\"")]
    [InlineData("include", "[\"message.output_text.logprobs\"]")]
    [InlineData("max_tool_calls", "1")]
    [InlineData("moderation", "{}")]
    [InlineData("parallel_tool_calls", "true")]
    [InlineData("previous_response_id", "\"resp_1\"")]
    [InlineData("prompt", "{}")]
    [InlineData("prompt_cache_key", "\"key\"")]
    [InlineData("prompt_cache_options", "{}")]
    [InlineData("prompt_cache_retention", "\"24h\"")]
    [InlineData("reasoning", "{}")]
    [InlineData("safety_identifier", "\"safe\"")]
    [InlineData("service_tier", "\"default\"")]
    [InlineData("stream_options", "{}")]
    [InlineData("text", "{}")]
    [InlineData("tool_choice", "\"auto\"")]
    [InlineData("tools", "[]")]
    [InlineData("top_logprobs", "1")]
    [InlineData("truncation", "\"auto\"")]
    public void Translate_ExplicitlyRejectsUnsupportedFields(string field, string value)
    {
        var request = Deserialize(
            $$"""{"model":"m","input":"x","store":false,"{{field}}":{{value}}}""");

        var result = ResponsesEndpoints.Translate(request);

        result.Error.Should().NotBeNull();
        result.Error!.Parameter.Should().Be(field);
        result.Error.Message.Should().Contain("not supported");
    }

    [Theory]
    [InlineData("""[{"role":"tool","content":"x"}]""", "input[0].role")]
    [InlineData("""[{"role":"user","content":[{"type":"input_text","text":"x"}]}]""", "input[0].content")]
    [InlineData("""[{"role":"user","content":"x","type":"item_reference"}]""", "input[0].type")]
    [InlineData("""[{"role":"user","content":"x","id":"item_1"}]""", "input[0].id")]
    public void Translate_RejectsNonTextOrNonMessageItems(string input, string parameter)
    {
        var request = Deserialize(
            $$"""{"model":"m","input":{{input}},"store":false}""");

        var result = ResponsesEndpoints.Translate(request);

        result.Error!.Parameter.Should().Be(parameter);
    }

    [Fact]
    public async Task CreateResponse_ConvertsProviderResponseAndPublishesAccountingEvidence()
    {
        var executor = new Mock<IResponsesChatExecutor>();
        executor.Setup(service => service.CreateAsync(
                It.IsAny<ChatCompletionRequest>(),
                7,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse
            {
                Id = "chatcmpl_upstream",
                Object = "chat.completion",
                Created = 1_700_000_000,
                Model = "provider-model",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        FinishReason = "stop",
                        Message = new Message { Role = "assistant", Content = "Hello back" }
                    }
                ],
                Usage = new Usage
                {
                    PromptTokens = 3,
                    CompletionTokens = 2,
                    TotalTokens = 5,
                    CachedInputTokens = 1
                }
            });
        var (endpoint, context) = CreateEndpoint(executor.Object);

        var result = await endpoint.CreateResponse(Deserialize(
            """{"model":"model-a","input":"Hello","store":false}"""));
        await ExecuteResultAsync(result, context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.ContentType.Should().Contain("application/json");
        var json = JsonDocument.Parse(ReadBody(context));
        var root = json.RootElement;
        root.GetProperty("id").GetString().Should().StartWith("resp_");
        root.GetProperty("object").GetString().Should().Be("response");
        root.GetProperty("status").GetString().Should().Be("completed");
        root.GetProperty("output_text").GetString().Should().Be("Hello back");
        root.GetProperty("output")[0].GetProperty("content")[0].GetProperty("type")
            .GetString().Should().Be("output_text");
        root.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(3);
        root.GetProperty("usage").GetProperty("input_tokens_details")
            .GetProperty("cached_tokens").GetInt32().Should().Be(1);

        var accounting = context.GetRequestAccountingSnapshot();
        accounting!.Operation.Should().Be(RequestOperation.Responses);
        accounting.VirtualKeyId.Should().Be(7);
        accounting.ProviderUsage!.Usage.TotalTokens.Should().Be(5);
    }

    [Fact]
    public async Task CreateResponse_StreamsPinnedEventSequenceAndFinalUsage()
    {
        var executor = new Mock<IResponsesChatExecutor>();
        executor.Setup(service => service.StreamAsync(
                It.IsAny<ChatCompletionRequest>(),
                7,
                It.IsAny<CancellationToken>()))
            .Returns(StreamChunks());
        var (endpoint, context) = CreateEndpoint(executor.Object);

        var result = await endpoint.CreateResponse(Deserialize(
            """{"model":"model-a","input":"Hello","store":false,"stream":true}"""));

        result.Should().NotBeNull();
        context.Response.ContentType.Should().Be("text/event-stream");
        var body = ReadBody(context);
        var frames = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var eventNames = frames.Select(frame => frame.Split('\n')[0]["event: ".Length..]).ToList();
        eventNames.Should().Equal(
            "response.created",
            "response.in_progress",
            "response.output_item.added",
            "response.content_part.added",
            "response.output_text.delta",
            "response.output_text.delta",
            "response.output_text.done",
            "response.content_part.done",
            "response.output_item.done",
            "response.completed");

        var payloads = frames.Select(frame =>
            JsonDocument.Parse(frame.Split('\n')[1]["data: ".Length..]).RootElement.Clone()).ToList();
        payloads.Select(payload => payload.GetProperty("sequence_number").GetInt32())
            .Should().Equal(Enumerable.Range(1, payloads.Count));
        payloads[^1].GetProperty("response").GetProperty("output_text").GetString()
            .Should().Be("Hello world");
        payloads[^1].GetProperty("response").GetProperty("usage")
            .GetProperty("total_tokens").GetInt32().Should().Be(5);

        var accounting = context.GetRequestAccountingSnapshot();
        accounting!.ProviderUsage!.Source.Should().Be(UsageEvidenceSource.Provider);
        accounting.Transport!.Outcome.Should().Be(StreamTransportOutcome.Completed);
        accounting.Transport.ProviderChunksObserved.Should().Be(3);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CreateResponse_EmitsFailureEventsAndMarksUsageIndeterminate(
        bool includeProviderDetail, bool providerFailure)
    {
        var executor = new Mock<IResponsesChatExecutor>();
        executor.Setup(service => service.StreamAsync(
                It.IsAny<ChatCompletionRequest>(),
                7,
                It.IsAny<CancellationToken>()))
            .Returns(FailingStream(providerFailure
                ? new LLMCommunicationException("provider failed", System.Net.HttpStatusCode.ServiceUnavailable, null)
                : new InvalidOperationException("provider failed")));
        var translator = new Mock<IProviderErrorTranslator>();
        translator.Setup(value => value.Translate(It.IsAny<Exception>(), It.IsAny<string>()))
            .Returns(new CustomerFacingProviderError("provider unavailable", ProviderErrorType.Unknown,
                includeProviderDetail
                    ? new ProviderErrorDetail("test-provider", "unknown", 503, "upstream unavailable")
                    : null));
        var (endpoint, context) = CreateEndpoint(executor.Object, translator.Object);

        await endpoint.CreateResponse(Deserialize(
            """{"model":"model-a","input":"Hello","store":false,"stream":true}"""));

        var body = ReadBody(context);
        body.Should().Contain("event: error");
        body.Should().Contain("event: response.failed");
        var failedFrame = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Single(frame => frame.StartsWith("event: response.failed\n"));
        using var failed = JsonDocument.Parse(failedFrame.Split('\n')[1]["data: ".Length..]);
        var error = failed.RootElement.GetProperty("response").GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("provider_response_error");
        error.GetProperty("message").GetString().Should().Be(providerFailure
            ? "provider unavailable" : "The provider stream terminated before completion.");
        error.TryGetProperty("provider_error", out var detail).Should().Be(includeProviderDetail);
        error.EnumerateObject().Should().HaveCount(includeProviderDetail ? 3 : 2);
        if (includeProviderDetail)
        {
            detail.GetProperty("provider").GetString().Should().Be("test-provider");
        }
        var accounting = context.GetRequestAccountingSnapshot();
        accounting!.IsIndeterminate.Should().BeTrue();
        accounting.Transport!.Outcome.Should().Be(StreamTransportOutcome.ProviderFailed);
    }

    [Theory]
    [InlineData("length", "max_output_tokens", false)]
    [InlineData("content_filter", "content_filter", false)]
    [InlineData("length", "max_output_tokens", true)]
    [InlineData("content_filter", "content_filter", true)]
    public async Task CreateResponse_IncompleteResultsUseGeneratedDetails(
        string finishReason, string expectedReason, bool stream)
    {
        var executor = new Mock<IResponsesChatExecutor>();
        executor.Setup(service => service.CreateAsync(
                It.IsAny<ChatCompletionRequest>(), 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletionResponse
            {
                Id = "chatcmpl_partial", Model = "provider-model", Created = 1, Object = "chat.completion",
                Choices = [new Choice
                {
                    Index = 0, FinishReason = finishReason,
                    Message = new Message { Role = "assistant", Content = "partial" }
                }]
            });
        executor.Setup(service => service.StreamAsync(
                It.IsAny<ChatCompletionRequest>(), 7, It.IsAny<CancellationToken>()))
            .Returns(IncompleteStream(finishReason));
        var (endpoint, context) = CreateEndpoint(executor.Object);
        var request = Deserialize("""{"model":"model-a","input":"Hello","store":false}""");
        request.Stream = stream;

        var result = await endpoint.CreateResponse(request);
        if (!stream)
        {
            await ExecuteResultAsync(result, context);
        }
        var payload = ReadBody(context);
        if (stream)
        {
            var finalFrame = payload.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Single(frame => frame.StartsWith("event: response.incomplete\n"));
            payload = finalFrame.Split('\n')[1]["data: ".Length..];
        }
        using var document = JsonDocument.Parse(payload);
        var response = stream ? document.RootElement.GetProperty("response") : document.RootElement;
        response.GetProperty("status").GetString().Should().Be("incomplete");
        var details = response.GetProperty("incomplete_details");
        details.GetProperty("reason").GetString().Should().Be(expectedReason);
        details.EnumerateObject().Should().HaveCount(1);
    }

    private static (ResponsesEndpoints Endpoint, DefaultHttpContext Context) CreateEndpoint(
        IResponsesChatExecutor executor, IProviderErrorTranslator? providerErrorTranslator = null)
    {
        var mappingService = new Mock<IModelProviderMappingService>();
        mappingService.Setup(service => service.GetMappingByModelAliasAsync(It.IsAny<string>()))
            .ReturnsAsync((ModelProviderMapping?)null);
        mappingService.Setup(service => service.GetMappingByIdAsync(It.IsAny<int>()))
            .ReturnsAsync((ModelProviderMapping?)null);
        var usageEstimation = new Mock<IUsageEstimationService>();
        usageEstimation.Setup(service => service.EstimateUsageFromStreamingResponseAsync(
                It.IsAny<string>(),
                It.IsAny<List<Message>>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<Tool>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Usage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 });

        var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.TypeInfoResolverChain.Clear();
                GatewayJsonOptions.Configure(options.SerializerOptions);
            })
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("VirtualKeyId", "7"),
                    new Claim("VirtualKey", "test-key")
                ],
                "VirtualKey"))
        };
        context.Response.Body = new MemoryStream();
        var accessor = new HttpContextAccessor { HttpContext = context };
        var endpoint = new ResponsesEndpoints(
            executor,
            mappingService.Object,
            usageEstimation.Object,
            JsonOptions,
            accessor,
            NullLogger<ResponsesEndpoints>.Instance,
            billingAdmissionOptions: Options.Create(new BillingAdmissionOptions
            {
                Mode = BillingAdmissionMode.Off
            }), providerErrorTranslator: providerErrorTranslator);
        return (endpoint, context);
    }

    private static async Task ExecuteResultAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
    }

    private static string ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(
            context.Response.Body,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static CreateResponseRequest Deserialize(string json) =>
        JsonSerializer.Deserialize<CreateResponseRequest>(json, JsonOptions)!;

    private static async IAsyncEnumerable<ChatCompletionChunk> StreamChunks()
    {
        yield return Chunk("Hello ");
        await Task.Yield();
        yield return Chunk("world");
        yield return new ChatCompletionChunk
        {
            Model = "provider-model",
            Usage = new Usage { PromptTokens = 3, CompletionTokens = 2, TotalTokens = 5 }
        };
    }

    private static async IAsyncEnumerable<ChatCompletionChunk> FailingStream(Exception failure)
    {
        await Task.Yield();
        throw failure;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<ChatCompletionChunk> IncompleteStream(string finishReason)
    {
        yield return Chunk("partial");
        await Task.Yield();
        yield return new ChatCompletionChunk
        {
            Choices = [new StreamingChoice
            {
                Index = 0, Delta = new DeltaContent(), FinishReason = finishReason
            }]
        };
    }

    private static ChatCompletionChunk Chunk(string content) =>
        new()
        {
            Choices =
            [
                new StreamingChoice
                {
                    Index = 0,
                    Delta = new DeltaContent { Content = content }
                }
            ]
        };
}
