using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Models;
using ConduitLLM.Providers.Bedrock;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Moq;
using Moq.Protected;

using Xunit;

namespace ConduitLLM.Tests.Providers.Bedrock;

/// <summary>
/// Tests for <see cref="BedrockClient"/>: credential-shape validation, per-request SigV4/Bearer
/// authentication, Converse request/response mapping, streaming decode, and model discovery.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Component", "Providers")]
public class BedrockClientTests
{
    private const string Region = "us-east-1";
    private const string ModelId = "anthropic.claude-sonnet-4-20250514-v1:0";

    [Fact]
    public void Constructor_Without_Region_Setting_Names_The_Missing_Field()
    {
        var act = () => CreateClient(CreateHandler(HttpStatusCode.OK, "{}"), settings: new());

        act.Should().Throw<ConfigurationException>()
            .WithMessage("*AWS Region*");
    }

    [Fact]
    public void Constructor_With_Access_Key_Id_But_No_Secret_Names_The_Missing_Secret()
    {
        var act = () => CreateClient(
            CreateHandler(HttpStatusCode.OK, "{}"),
            apiKey: "AKIAIOSFODNN7EXAMPLE",
            secretSettings: new());

        act.Should().Throw<ConfigurationException>()
            .WithMessage("*Secret Access Key*");
    }

    [Fact]
    public void Constructor_With_Bedrock_Api_Key_And_No_Secrets_Is_Valid()
    {
        var act = () => CreateClient(
            CreateHandler(HttpStatusCode.OK, "{}"),
            apiKey: "bedrock-api-key-value",
            secretSettings: new());

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Chat_Posts_To_The_Converse_Endpoint_With_The_Model_Id_Escaped()
    {
        HttpRequestMessage? captured = null;
        var handler = CreateHandler(HttpStatusCode.OK, ConverseResponseJson("Hi"), req => captured = req);
        var client = CreateClient(handler);

        await client.CreateChatCompletionAsync(ChatRequest());

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsoluteUri.Should().Be(
            $"https://bedrock-runtime.{Region}.amazonaws.com/model/anthropic.claude-sonnet-4-20250514-v1%3A0/converse");
    }

    [Fact]
    public async Task Chat_In_SigV4_Mode_Signs_The_Request()
    {
        HttpRequestMessage? captured = null;
        var handler = CreateHandler(HttpStatusCode.OK, ConverseResponseJson("Hi"), req => captured = req);
        var client = CreateClient(
            handler,
            apiKey: "AKIAIOSFODNN7EXAMPLE",
            secretSettings: new Dictionary<string, string>
            {
                ["secret_access_key"] = "secret",
                ["session_token"] = "token"
            });

        await client.CreateChatCompletionAsync(ChatRequest());

        var authorization = captured!.Headers.GetValues("Authorization").Single();
        authorization.Should().StartWith("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/");
        authorization.Should().Contain($"/{Region}/bedrock-runtime/aws4_request");
        captured.Headers.Contains("x-amz-date").Should().BeTrue();
        captured.Headers.GetValues("x-amz-security-token").Single().Should().Be("token");
    }

    [Fact]
    public async Task Chat_In_Bearer_Mode_Sends_The_Api_Key_As_Bearer_Token()
    {
        HttpRequestMessage? captured = null;
        var handler = CreateHandler(HttpStatusCode.OK, ConverseResponseJson("Hi"), req => captured = req);
        var client = CreateClient(handler, apiKey: "bedrock-api-key", secretSettings: null);

        await client.CreateChatCompletionAsync(ChatRequest());

        captured!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization.Parameter.Should().Be("bedrock-api-key");
    }

    [Fact]
    public async Task Chat_Maps_Text_Usage_And_Stop_Reason()
    {
        var handler = CreateHandler(HttpStatusCode.OK, ConverseResponseJson("Hello there"));
        var client = CreateClient(handler);

        var response = await client.CreateChatCompletionAsync(ChatRequest());

        response.Choices.Should().ContainSingle();
        response.Choices[0].Message.Content.Should().Be("Hello there");
        response.Choices[0].FinishReason.Should().Be("stop");
        response.Usage!.PromptTokens.Should().Be(10);
        response.Usage.CompletionTokens.Should().Be(20);
        response.Usage.TotalTokens.Should().Be(30);
    }

    [Fact]
    public async Task Chat_Maps_Tool_Use_Blocks_To_Tool_Calls()
    {
        const string body = """
        {
          "output": { "message": { "role": "assistant", "content": [
            { "toolUse": { "toolUseId": "tool-1", "name": "get_weather", "input": { "city": "Berlin" } } }
          ] } },
          "stopReason": "tool_use",
          "usage": { "inputTokens": 5, "outputTokens": 7, "totalTokens": 12 }
        }
        """;
        var client = CreateClient(CreateHandler(HttpStatusCode.OK, body));

        var response = await client.CreateChatCompletionAsync(ChatRequest());

        response.Choices[0].FinishReason.Should().Be("tool_calls");
        var toolCall = response.Choices[0].Message.ToolCalls.Should().ContainSingle().Subject;
        toolCall.Id.Should().Be("tool-1");
        toolCall.Function.Name.Should().Be("get_weather");
        toolCall.Function.Arguments.Should().Contain("Berlin");
    }

    [Fact]
    public async Task Chat_Error_Surfaces_Bedrocks_Message_And_Status()
    {
        var handler = CreateHandler(
            HttpStatusCode.Forbidden,
            """{ "message": "The security token included in the request is invalid." }""");
        var client = CreateClient(handler);

        var act = () => client.CreateChatCompletionAsync(ChatRequest());

        (await act.Should().ThrowAsync<LLMCommunicationException>())
            .WithMessage("*security token*")
            .WithMessage("*403*");
    }

    [Fact]
    public void MapToConverseRequest_Extracts_System_Merges_Roles_And_Maps_Tool_Results()
    {
        var client = CreateClient(CreateHandler(HttpStatusCode.OK, "{}"));
        var request = new ChatCompletionRequest
        {
            Model = ModelId,
            Messages = new List<Message>
            {
                new() { Role = "system", Content = "Be terse." },
                new() { Role = "user", Content = "What's the weather?" },
                new()
                {
                    Role = "assistant",
                    Content = null,
                    ToolCalls = new List<ToolCall>
                    {
                        new()
                        {
                            Id = "tool-1",
                            Function = new FunctionCall { Name = "get_weather", Arguments = """{"city":"Berlin"}""" }
                        }
                    }
                },
                new() { Role = "tool", ToolCallId = "tool-1", Content = """{"temp_c": 21}""" },
                new() { Role = "tool", ToolCallId = "tool-2", Content = "plain text result" }
            }
        };

        var converse = client.MapToConverseRequest(request);

        converse.System.Should().ContainSingle().Which.Text.Should().Be("Be terse.");
        converse.Messages.Should().HaveCount(3, "the two consecutive tool results merge into one user message");
        converse.Messages[0].Role.Should().Be("user");
        converse.Messages[1].Role.Should().Be("assistant");
        converse.Messages[1].Content.Should().ContainSingle(block => block.ToolUse != null);
        converse.Messages[2].Role.Should().Be("user");
        converse.Messages[2].Content.Should().HaveCount(2);
        converse.Messages[2].Content[0].ToolResult!.ToolUseId.Should().Be("tool-1");
        converse.Messages[2].Content[0].ToolResult!.Content[0].Json.Should().NotBeNull();
        converse.Messages[2].Content[1].ToolResult!.Content[0].Text.Should().Be("plain text result");
    }

    [Fact]
    public void MapToConverseRequest_Rejects_Unsupported_Content_Instead_Of_Dropping_It()
    {
        var client = CreateClient(CreateHandler(HttpStatusCode.OK, "{}"));
        var request = ChatRequest();
        request.Messages =
        [
            new Message
            {
                Role = "user",
                Content = JsonSerializer.Deserialize<JsonElement>(
                    """[{"type":"text","text":"Listen"},{"type":"input_audio","input_audio":{"data":"AAAA","format":"wav"}}]""")
            }
        ];

        var act = () => client.MapToConverseRequest(request);

        act.Should().Throw<ValidationException>()
            .WithMessage("*input_audio*");
    }

    [Theory]
    [InlineData("data:image/png,not-base64", "*base64 data URL*")]
    [InlineData("data:image/bmp;base64,AQID", "*does not support*image/bmp*")]
    public void MapToConverseRequest_Rejects_Invalid_ImageDataUrls(
        string url,
        string expectedMessage)
    {
        var client = CreateClient(CreateHandler(HttpStatusCode.OK, "{}"));
        var request = ChatRequest();
        request.Messages =
        [
            new Message
            {
                Role = "user",
                Content = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        type = "image_url",
                        image_url = new { url }
                    }
                })
            }
        ];

        var act = () => client.MapToConverseRequest(request);

        act.Should().Throw<ValidationException>()
            .WithMessage(expectedMessage);
    }

    [Fact]
    public void MapToConverseRequest_AcceptsCaseInsensitiveImageDataUrlMarkers()
    {
        var client = CreateClient(CreateHandler(HttpStatusCode.OK, "{}"));
        var request = ChatRequest();
        request.Messages =
        [
            new Message
            {
                Role = "user",
                Content = JsonSerializer.SerializeToElement(new[]
                {
                    new
                    {
                        type = "image_url",
                        image_url = new { url = "DATA:image/png;BASE64,AQID" }
                    }
                })
            }
        ];

        var mapped = client.MapToConverseRequest(request);

        mapped.Messages.Single().Content.Single().Image!.Format.Should().Be("png");
        mapped.Messages.Single().Content.Single().Image!.Source.Bytes.Should().Be("AQID");
    }

    [Fact]
    public void ConversePayload_PreservesGeneratedJsonBoundaryShapes()
    {
        var client = CreateClient(CreateHandler(HttpStatusCode.OK, "{}"));
        var request = ChatRequest();
        request.TopK = 42;
        request.ToolChoice = ToolChoice.Function("get_weather");
        request.Tools =
        [
            new Tool
            {
                Function = new FunctionDefinition
                {
                    Name = "get_weather",
                    Parameters = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["city"] = new JsonObject { ["type"] = "string" }
                        }
                    }
                }
            }
        ];
        request.Messages =
        [
            new Message
            {
                Role = "user",
                Content = new List<object> { new TextContentPart { Text = "Weather?" } }
            },
            new Message
            {
                Role = "assistant",
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "tool-1",
                        Function = new FunctionCall
                        {
                            Name = "get_weather",
                            Arguments = "not-json"
                        }
                    }
                ]
            }
        ];

        var payload = BedrockClient.SerializePayload(client.MapToConverseRequest(request));
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;

        root.GetProperty("additionalModelRequestFields").GetProperty("top_k").GetInt32()
            .Should().Be(42);
        root.GetProperty("toolConfig").GetProperty("toolChoice").GetProperty("tool")
            .GetProperty("name").GetString().Should().Be("get_weather");
        root.GetProperty("toolConfig").GetProperty("tools")[0].GetProperty("toolSpec")
            .GetProperty("inputSchema").GetProperty("json").GetProperty("properties")
            .GetProperty("city").GetProperty("type").GetString().Should().Be("string");
        root.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("toolUse")
            .GetProperty("input").GetProperty("raw").GetString().Should().Be("not-json");
    }

    [Fact]
    public async Task GetModels_Queries_The_Control_Plane_Foundation_Models_Endpoint()
    {
        HttpRequestMessage? captured = null;
        const string body = """
        {
          "modelSummaries": [
            { "modelId": "anthropic.claude-sonnet-4-20250514-v1:0", "modelName": "Claude Sonnet 4", "providerName": "Anthropic" },
            { "modelId": "amazon.nova-pro-v1:0", "modelName": "Nova Pro", "providerName": "Amazon" }
          ]
        }
        """;
        var client = CreateClient(CreateHandler(HttpStatusCode.OK, body, req => captured = req));

        var models = await client.GetModelsAsync();

        captured!.Method.Should().Be(HttpMethod.Get);
        captured.RequestUri!.AbsoluteUri.Should().Be(
            $"https://bedrock.{Region}.amazonaws.com/foundation-models");
        models.Select(m => m.Id).Should().Contain(new[]
        {
            "anthropic.claude-sonnet-4-20250514-v1:0",
            "amazon.nova-pro-v1:0"
        });
    }

    [Fact]
    public async Task VerifyAuthentication_Fails_With_The_Providers_Error_Message()
    {
        var handler = CreateHandler(
            HttpStatusCode.Unauthorized,
            """{ "message": "UnrecognizedClientException" }""");
        var client = CreateClient(handler);

        var result = await client.VerifyAuthenticationAsync();

        result.IsSuccess.Should().BeFalse();
        result.ErrorDetails.Should().Contain("UnrecognizedClientException");
    }

    [Fact]
    public async Task Streaming_Maps_Event_Stream_Frames_To_Chunks()
    {
        var frames = new[]
        {
            Frame("messageStart", """{"role":"assistant"}"""),
            Frame("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"Hel"}}"""),
            Frame("contentBlockDelta", """{"contentBlockIndex":0,"delta":{"text":"lo"}}"""),
            Frame("messageStop", """{"stopReason":"end_turn"}"""),
            Frame("metadata", """{"usage":{"inputTokens":3,"outputTokens":9,"totalTokens":12}}""")
        };
        var client = CreateClient(CreateStreamingHandler(frames));

        var chunks = new List<ChatCompletionChunk>();
        await foreach (var chunk in client.StreamChatCompletionAsync(ChatRequest()))
        {
            chunks.Add(chunk);
        }

        chunks.Should().HaveCount(5);
        chunks[0].Choices[0].Delta.Role.Should().Be("assistant");
        string.Concat(chunks.Select(c => c.Choices[0].Delta.Content)).Should().Be("Hello");
        chunks[3].Choices[0].FinishReason.Should().Be("stop");
        chunks[4].Usage!.TotalTokens.Should().Be(12);
    }

    [Fact]
    public async Task Streaming_Assigns_Tool_Call_Indices_In_Order_Of_Appearance()
    {
        var frames = new[]
        {
            Frame("contentBlockStart", """{"contentBlockIndex":1,"start":{"toolUse":{"toolUseId":"t-1","name":"get_weather"}}}"""),
            Frame("contentBlockDelta", """{"contentBlockIndex":1,"delta":{"toolUse":{"input":"{\"city\":"}}}"""),
            Frame("contentBlockDelta", """{"contentBlockIndex":1,"delta":{"toolUse":{"input":"\"Berlin\"}"}}}"""),
            Frame("messageStop", """{"stopReason":"tool_use"}""")
        };
        var client = CreateClient(CreateStreamingHandler(frames));

        var chunks = new List<ChatCompletionChunk>();
        await foreach (var chunk in client.StreamChatCompletionAsync(ChatRequest()))
        {
            chunks.Add(chunk);
        }

        chunks[0].Choices[0].Delta.ToolCalls![0].Id.Should().Be("t-1");
        chunks[0].Choices[0].Delta.ToolCalls![0].Index.Should().Be(0);
        chunks[0].Choices[0].Delta.ToolCalls![0].Function!.Name.Should().Be("get_weather");
        var argumentFragments = chunks
            .Where(c => c.Choices[0].Delta.ToolCalls?[0].Function?.Arguments is { Length: > 0 })
            .Select(c => c.Choices[0].Delta.ToolCalls![0].Function!.Arguments);
        string.Concat(argumentFragments).Should().Be("""{"city":"Berlin"}""");
        chunks[^1].Choices[0].FinishReason.Should().Be("tool_calls");
    }

    [Fact]
    public async Task Streaming_Exception_Event_Raises_A_Communication_Error()
    {
        var frame = FrameWithHeaders(
            new Dictionary<string, string>
            {
                [":message-type"] = "exception",
                [":exception-type"] = "throttlingException"
            },
            """{ "message": "Too many requests, please wait." }""");
        var client = CreateClient(CreateStreamingHandler(new[] { frame }));

        var act = async () =>
        {
            await foreach (var _ in client.StreamChatCompletionAsync(ChatRequest()))
            {
            }
        };

        (await act.Should().ThrowAsync<LLMCommunicationException>())
            .WithMessage("*throttlingException*")
            .WithMessage("*Too many requests*");
    }

    // ---- helpers ----

    private static ChatCompletionRequest ChatRequest() => new()
    {
        Model = ModelId,
        Messages = new List<Message> { new() { Role = "user", Content = "Hello" } }
    };

    private static string ConverseResponseJson(string text) => $$"""
    {
      "output": { "message": { "role": "assistant", "content": [ { "text": "{{text}}" } ] } },
      "stopReason": "end_turn",
      "usage": { "inputTokens": 10, "outputTokens": 20, "totalTokens": 30 }
    }
    """;

    private static byte[] Frame(string eventType, string payload) =>
        FrameWithHeaders(
            new Dictionary<string, string>
            {
                [":message-type"] = "event",
                [":event-type"] = eventType
            },
            payload);

    private static byte[] FrameWithHeaders(Dictionary<string, string> headers, string payload) =>
        AwsEventStreamReaderTests.BuildFrame(headers, payload);

    private static Mock<HttpMessageHandler> CreateHandler(
        HttpStatusCode statusCode,
        string body,
        Action<HttpRequestMessage>? onRequest = null)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => onRequest?.Invoke(req))
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        return handler;
    }

    private static Mock<HttpMessageHandler> CreateStreamingHandler(IEnumerable<byte[]> frames)
    {
        var bytes = frames.SelectMany(frame => frame).ToArray();
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new ByteArrayContent(bytes)
                {
                    Headers = { { "Content-Type", "application/vnd.amazon.eventstream" } }
                }
            });
        return handler;
    }

    private static BedrockClient CreateClient(
        Mock<HttpMessageHandler> handler,
        string apiKey = "AKIAIOSFODNN7EXAMPLE",
        Dictionary<string, string>? settings = null,
        Dictionary<string, string>? secretSettings = null)
    {
        var httpClient = new HttpClient(handler.Object);
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        var provider = new Provider
        {
            Id = 1,
            ProviderType = ProviderType.Bedrock,
            ProviderName = "bedrock-test",
            Settings = settings ?? new Dictionary<string, string> { ["region"] = Region }
        };

        var key = new ProviderKeyCredential
        {
            Id = 1,
            ProviderId = 1,
            ApiKey = apiKey,
            SecretSettings = secretSettings ?? (apiKey.StartsWith("AKIA") || apiKey.StartsWith("ASIA")
                ? new Dictionary<string, string> { ["secret_access_key"] = "test-secret" }
                : null),
            IsPrimary = true,
            IsEnabled = true
        };

        return new BedrockClient(
            provider,
            key,
            ModelId,
            NullLogger<BedrockClient>.Instance,
            factory.Object);
    }
}
