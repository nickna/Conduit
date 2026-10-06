using System.Net;
using System.Text;
using System.Text.Json;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Audio;
using ConduitLLM.Core.Models.Rerank;
using ConduitLLM.Providers.OpenRouter;

using AwesomeAssertions;

using Moq;
using Moq.Protected;

using Xunit.Abstractions;

namespace ConduitLLM.Tests.Providers
{
    /// <summary>
    /// Unit tests for OpenRouterClient: attribution headers and rich /models metadata mapping.
    /// </summary>
    [Trait("Category", "Unit")]
    [Trait("Component", "Providers")]
    public class OpenRouterClientTests : TestBase
    {
        private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
        private readonly Mock<HttpMessageHandler> _handlerMock;
        private readonly HttpClient _httpClient;
        private readonly List<Dictionary<string, string>> _capturedHeaders = new();
        private readonly List<(string Method, string Path, string Body)> _capturedRequests = new();
        private string _modelsJson = "{\"data\":[]}";
        private string _imagesJson = "{\"created\":0,\"data\":[]}";
        private string _videoSubmitJson = "{\"id\":\"vid_1\"}";
        private string _videoStatusJson = "{\"status\":\"completed\",\"unsigned_urls\":[\"https://openrouter.ai/videos/vid_1.mp4\"]}";
        private string _transcriptionJson = "{\"text\":\"hello\"}";
        private string _rerankJson = "{\"results\":[]}";
        private string _chatJson = "{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"openai/gpt-4o\"," +
            "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":\"stop\"}]," +
            "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";

        public OpenRouterClientTests(ITestOutputHelper output) : base(output)
        {
            _httpClientFactoryMock = new Mock<IHttpClientFactory>();
            _handlerMock = new Mock<HttpMessageHandler>();
            _httpClient = new HttpClient(_handlerMock.Object)
            {
                BaseAddress = new Uri("https://openrouter.ai/api/v1/")
            };
            _httpClientFactoryMock.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(_httpClient);

            _handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Returns<HttpRequestMessage, CancellationToken>((req, _) =>
                {
                    _capturedHeaders.Add(req.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)));
                    var reqBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                    var path = req.RequestUri!.AbsolutePath;
                    _capturedRequests.Add((req.Method.Method, path, reqBody));

                    // TTS returns raw audio bytes, not JSON.
                    if (path.EndsWith("/audio/speech"))
                    {
                        var audio = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
                        audio.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = audio });
                    }

                    string responseBody;
                    if (path.EndsWith("/chat/completions")) responseBody = _chatJson;
                    else if (path.EndsWith("/key")) responseBody = "{\"data\":{}}";
                    else if (path.EndsWith("/images")) responseBody = _imagesJson;
                    else if (path.EndsWith("/audio/transcriptions")) responseBody = _transcriptionJson;
                    else if (path.EndsWith("/rerank")) responseBody = _rerankJson;
                    else if (path.Contains("/videos/")) responseBody = _videoStatusJson;  // GET status
                    else if (path.EndsWith("/videos")) responseBody = _videoSubmitJson;    // POST submit
                    else responseBody = _modelsJson;

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
                    });
                });
        }

        private OpenRouterClient CreateClient(string? providerOptionsJson = null, string model = "openai/gpt-4o")
        {
            var provider = new Provider { Id = 1, ProviderType = ProviderType.OpenRouter };
            var keyCredential = new ProviderKeyCredential { Id = 1, ProviderId = 1, ApiKey = "test-api-key" };
            var logger = CreateLogger<OpenRouterClient>();
            return new OpenRouterClient(
                provider,
                keyCredential,
                model,
                logger.Object,
                _httpClientFactoryMock.Object,
                providerOptionsJson: providerOptionsJson);
        }

        [Fact]
        public async Task Chat_MixedMultimodalContent_PreservesOrderPayloadsAndPlugins()
        {
            var content = JsonSerializer.Deserialize<JsonElement>(
                """
                [
                  { "type": "text", "text": "first", "vendor_hint": "keep" },
                  { "type": "image_url", "image_url": { "url": "https://example.com/image.png?sig=secret" } },
                  { "type": "file", "file": { "filename": "sample.pdf", "file_data": "data:application/pdf;base64,JVBERg==" } },
                  { "type": "input_audio", "input_audio": { "data": "AQID", "format": "wav" } },
                  { "type": "video_url", "video_url": { "url": "data:video/mp4;base64,AAAA" } }
                ]
                """);
            var request = new ChatCompletionRequest
            {
                Model = "alias",
                Messages = [new Message { Role = "user", Content = content }],
                ExtensionData = new Dictionary<string, JsonElement>
                {
                    ["plugins"] = JsonSerializer.SerializeToElement(new[]
                    {
                        new { id = "file-parser", pdf = new { engine = "cloudflare-ai" } }
                    })
                }
            };

            await CreateClient().CreateChatCompletionAsync(request);

            using var json = JsonDocument.Parse(
                _capturedRequests.Single(r => r.Path.EndsWith("/chat/completions")).Body);
            var parts = json.RootElement.GetProperty("messages")[0].GetProperty("content");
            parts.EnumerateArray().Select(part => part.GetProperty("type").GetString())
                .Should().Equal("text", "image_url", "file", "input_audio", "video_url");
            parts[0].GetProperty("vendor_hint").GetString().Should().Be("keep");
            parts[1].GetProperty("image_url").GetProperty("url").GetString()
                .Should().Be("https://example.com/image.png?sig=secret");
            parts[2].GetProperty("file").GetProperty("file_data").GetString()
                .Should().Be("data:application/pdf;base64,JVBERg==");
            json.RootElement.GetProperty("plugins")[0].GetProperty("pdf")
                .GetProperty("engine").GetString().Should().Be("cloudflare-ai");
        }

        [Fact]
        public async Task Chat_FileWithFileDataAndFileId_IsRejectedBeforeProviderCall()
        {
            var request = new ChatCompletionRequest
            {
                Model = "alias",
                Messages =
                [
                    new Message
                    {
                        Role = "user",
                        Content = JsonSerializer.Deserialize<JsonElement>(
                            """[{"type":"file","file":{"file_data":"https://example.com/a.pdf","file_id":"file-1"}}]""")
                    }
                ]
            };

            var action = () => CreateClient().CreateChatCompletionAsync(request);

            await action.Should().ThrowAsync<ValidationException>()
                .WithMessage("*exactly one of file_data or file_id*");
            _capturedRequests.Should().BeEmpty();
        }

        [Theory]
        [InlineData("""[{"type":"input_audio","input_audio":{"data":"https://example.com/a.wav","format":"wav"}}]""")]
        [InlineData("""[{"type":"image_url","image_url":{"url":"http://example.com/a.png"}}]""")]
        [InlineData("""[{"type":"future_media","future_media":{"value":1}}]""")]
        public async Task Chat_InvalidOrUnsupportedContent_IsRejected(string contentJson)
        {
            var request = new ChatCompletionRequest
            {
                Model = "alias",
                Messages =
                [
                    new Message
                    {
                        Role = "user",
                        Content = JsonSerializer.Deserialize<JsonElement>(contentJson)
                    }
                ]
            };

            var action = () => CreateClient().CreateChatCompletionAsync(request);

            await action.Should().ThrowAsync<ValidationException>();
            _capturedRequests.Should().BeEmpty();
        }

        [Fact]
        public async Task Chat_ResponsePreservesAnnotationsAndAssistantExtensions()
        {
            _chatJson = """
            {
              "id":"c",
              "object":"chat.completion",
              "created":1,
              "model":"openai/gpt-4o",
              "provider":"ExampleProvider",
              "choices":[{
                "index":0,
                "message":{
                  "role":"assistant",
                  "content":"done",
                  "annotations":[{
                    "type":"file",
                    "file":{"hash":"hash-1","name":"sample.pdf","content":[{"type":"text","text":"parsed"}]}
                  }],
                  "audio":{"id":"audio-1"},
                  "images":[{"image_url":{"url":"https://example.com/generated.png"}}],
                  "reasoning_details":[{"type":"summary","text":"reasoned"}],
                  "provider_message_field":{"kept":true}
                },
                "finish_reason":"stop"
              }],
              "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}
            }
            """;

            var response = await CreateClient().CreateChatCompletionAsync(new ChatCompletionRequest
            {
                Model = "alias",
                Messages = [new Message { Role = "user", Content = "hello" }]
            });

            var message = response.Choices.Single().Message;
            message.Annotations.Should().ContainSingle();
            message.Annotations![0].GetProperty("file").GetProperty("hash").GetString().Should().Be("hash-1");
            message.Audio.Should().NotBeNull();
            message.Images.Should().NotBeNull();
            message.ReasoningDetails.Should().NotBeNull();
            message.ExtensionData.Should().ContainKey("provider_message_field");
            response.ExtensionData.Should().ContainKey("provider");
        }

        [Fact]
        public async Task Chat_PreservesAnnotationsAndToolCallsAcrossProviderBoundary()
        {
            _chatJson = """
            {
              "id":"c",
              "object":"chat.completion",
              "created":1,
              "model":"openai/gpt-4o",
              "choices":[{
                "index":0,
                "message":{
                  "role":"assistant",
                  "tool_calls":[{
                    "id":"response-tool",
                    "type":"function",
                    "function":{"name":"lookup","arguments":"{\"id\":7}"}
                  }]
                },
                "finish_reason":"tool_calls"
              }],
              "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}
            }
            """;
            using var annotationDocument = JsonDocument.Parse(
                """{"type":"file","file":{"hash":"request-hash"}}""");
            var request = new ChatCompletionRequest
            {
                Model = "alias",
                Messages =
                [
                    new Message
                    {
                        Role = "assistant",
                        Annotations = [annotationDocument.RootElement.Clone()],
                        ToolCalls =
                        [
                            new ToolCall
                            {
                                Id = "request-tool",
                                Function = new FunctionCall
                                {
                                    Name = "lookup",
                                    Arguments = "{\"id\":3}"
                                }
                            }
                        ]
                    }
                ]
            };

            var response = await CreateClient().CreateChatCompletionAsync(request);

            using var sent = JsonDocument.Parse(
                _capturedRequests.Single(item => item.Path.EndsWith("/chat/completions")).Body);
            var sentMessage = sent.RootElement.GetProperty("messages")[0];
            sentMessage.GetProperty("annotations")[0].GetProperty("file")
                .GetProperty("hash").GetString().Should().Be("request-hash");
            sentMessage.GetProperty("tool_calls")[0].GetProperty("id").GetString()
                .Should().Be("request-tool");

            var responseTool = response.Choices.Single().Message.ToolCalls.Should().ContainSingle().Subject;
            responseTool.Id.Should().Be("response-tool");
            responseTool.Function.Name.Should().Be("lookup");
            responseTool.Function.Arguments.Should().Be("{\"id\":7}");
        }

        [Fact]
        public async Task Chat_ClaudeAutomaticCaching_AddsTopLevelDirectiveWithoutMutatingRequest()
        {
            var client = CreateClient(model: "anthropic/claude-sonnet-4");
            var request = new ChatCompletionRequest
            {
                Model = "alias",
                Messages = [new Message { Role = "system", Content = "Original" }],
                PromptCachingIntent = new PromptCachingIntent
                {
                    Strategy = PromptCachingStrategy.Automatic,
                    Ttl = "1h"
                }
            };

            await client.CreateChatCompletionAsync(request);

            using var json = JsonDocument.Parse(_capturedRequests.Single(r => r.Path.EndsWith("/chat/completions")).Body);
            json.RootElement.GetProperty("cache_control").GetProperty("ttl").GetString().Should().Be("1h");
            request.Messages[0].Content.Should().Be("Original");
        }

        [Fact]
        public async Task Chat_QwenExplicitCaching_AddsContentBreakpointAndDeduplicatesTargets()
        {
            var client = CreateClient(model: "qwen/qwen3-coder-plus");
            var request = new ChatCompletionRequest
            {
                Model = "alias",
                Messages = [new Message { Role = "system", Content = "Stable prefix" }],
                PromptCachingIntent = new PromptCachingIntent
                {
                    Strategy = PromptCachingStrategy.Explicit,
                    Ttl = "5m",
                    InjectionPoints =
                    [
                        new CacheInjectionPoint { Role = "system", Index = 0 },
                        new CacheInjectionPoint { Role = "system", Index = 0 }
                    ]
                }
            };

            await client.CreateChatCompletionAsync(request);

            using var json = JsonDocument.Parse(_capturedRequests.Single(r => r.Path.EndsWith("/chat/completions")).Body);
            var content = json.RootElement.GetProperty("messages")[0].GetProperty("content");
            content.GetArrayLength().Should().Be(1);
            content[0].GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");
            request.Messages[0].Content.Should().Be("Stable prefix");
        }

        [Fact]
        public async Task GetModelsAsync_SendsAttributionHeaders_OnEveryRequest()
        {
            // Arrange
            var client = CreateClient();

            // Act — this makes both the GET /key and GET /models calls
            await client.GetModelsAsync();

            // Assert — HTTP-Referer + X-Title present on every outgoing request
            _capturedHeaders.Should().NotBeEmpty();
            _capturedHeaders.Should().OnlyContain(h =>
                h.ContainsKey("HTTP-Referer") && h.ContainsKey("X-Title"));
        }

        [Fact]
        public async Task GetModelsAsync_FullMetadata_PopulatesCapabilitiesAndTokenLimits()
        {
            // Arrange — a rich model entry + a bare minimal one
            _modelsJson = """
            {
                "data": [
                    {
                        "id": "anthropic/claude-3.5-sonnet",
                        "name": "Anthropic: Claude 3.5 Sonnet",
                        "context_length": 200000,
                        "architecture": {
                            "input_modalities": ["text", "image"],
                            "output_modalities": ["text"],
                            "tokenizer": "Claude"
                        },
                        "pricing": { "prompt": "0.000003", "completion": "0.000015" },
                        "top_provider": { "max_completion_tokens": 8192, "context_length": 200000 },
                        "supported_parameters": ["tools", "response_format", "reasoning"]
                    },
                    { "id": "minimal/model" }
                ]
            }
            """;
            var client = CreateClient();

            // Act
            var models = await client.GetModelsAsync();

            // Assert
            models.Should().HaveCount(2);

            var claude = models.First(m => m.Id == "anthropic/claude-3.5-sonnet");
            claude.Name.Should().Be("Anthropic: Claude 3.5 Sonnet");
            claude.Capabilities.Should().NotBeNull();
            claude.Capabilities!.SupportsVision.Should().BeTrue();          // image input modality
            claude.Capabilities.SupportsFunctionCalling.Should().BeTrue();  // supported_parameters contains "tools"
            claude.Capabilities.SupportsImageGeneration.Should().BeFalse(); // no image output modality
            claude.TokenLimits.Should().NotBeNull();
            claude.TokenLimits!.Context.Should().Be(200000);
            claude.TokenLimits.Output.Should().Be(8192);
        }

        [Fact]
        public void CatalogContract_ParsesTheUnionOfRuntimeAndAdminFields()
        {
            var catalog = JsonSerializer.Deserialize<OpenRouterCatalogResponse>(
                """
                {
                    "data": [{
                        "id": "provider/model",
                        "canonical_slug": "provider/model-v1",
                        "description": "A model",
                        "expiration_date": "2027-01-01",
                        "architecture": { "tokenizer": "Tokenizer" },
                        "pricing": {
                            "request": "0.01",
                            "image": "0.02",
                            "web_search": "0.03",
                            "internal_reasoning": "0.04"
                        }
                    }]
                }
                """);

            var model = catalog!.Data.Should().ContainSingle().Subject;
            model.CanonicalSlug.Should().Be("provider/model-v1");
            model.Description.Should().Be("A model");
            model.ExpirationDate.Should().Be("2027-01-01");
            model.Architecture!.Tokenizer.Should().Be("Tokenizer");
            model.Pricing!.Request.Should().Be("0.01");
            model.Pricing.Image.Should().Be("0.02");
            model.Pricing.WebSearch.Should().Be("0.03");
            model.Pricing.InternalReasoning.Should().Be("0.04");
        }

        [Fact]
        public async Task GetModelsAsync_MinimalModel_StillParses()
        {
            // Arrange — only the required id field present (schema-drift tolerance)
            _modelsJson = "{\"data\":[{\"id\":\"minimal/model\"}]}";
            var client = CreateClient();

            // Act
            var models = await client.GetModelsAsync();

            // Assert
            models.Should().ContainSingle();
            var model = models[0];
            model.Id.Should().Be("minimal/model");
            model.Capabilities.Should().NotBeNull();      // chat defaults still applied
            model.Capabilities!.SupportsChat.Should().BeTrue();
            model.TokenLimits.Should().BeNull();          // no context/limits reported
        }

        [Fact]
        public async Task CreateImageAsync_PostsToNativeImagesEndpoint_NotImagesGenerations()
        {
            // Arrange
            _imagesJson = "{\"created\":1,\"data\":[{\"b64_json\":\"aGVsbG8=\"}]}";
            var client = CreateClient();
            var request = new ImageGenerationRequest { Prompt = "a cat", Model = "black-forest-labs/flux-1.1-pro", N = 1 };

            // Act
            await client.CreateImageAsync(request);

            // Assert — hits /api/v1/images, not the OpenAI /images/generations path
            var imageCall = _capturedRequests.Single(r => r.Method == "POST");
            imageCall.Path.Should().EndWith("/images");
            imageCall.Path.Should().NotContain("/images/generations");
        }

        [Fact]
        public async Task CreateImageAsync_MapsQualityHdToHigh_AndDefaultsOutputFormatPng()
        {
            // Arrange
            _imagesJson = "{\"created\":1,\"data\":[{\"b64_json\":\"aGVsbG8=\"}]}";
            var client = CreateClient();
            var request = new ImageGenerationRequest
            {
                Prompt = "a cat", Model = "openai/gpt-image-1", N = 1, Quality = "hd", Size = "1024x1024"
            };

            // Act
            await client.CreateImageAsync(request);

            // Assert
            var body = _capturedRequests.Single(r => r.Method == "POST").Body;
            body.Should().Contain("\"quality\":\"high\"");
            body.Should().Contain("\"output_format\":\"png\"");
            body.Should().NotContain("response_format");
        }

        [Fact]
        public async Task CreateImageAsync_CapturesProviderCost_AndImageCount()
        {
            // Arrange — response carries b64 image + usage with cost
            _imagesJson = "{\"created\":1,\"data\":[{\"b64_json\":\"aGVsbG8=\"}],\"usage\":{\"prompt_tokens\":10,\"cost\":0.003}}";
            var client = CreateClient();
            var request = new ImageGenerationRequest { Prompt = "a cat", Model = "openai/gpt-image-1", N = 1, Quality = "standard", Size = "1024x1024" };

            // Act
            var result = await client.CreateImageAsync(request);

            // Assert
            result.Data.Should().ContainSingle();
            result.Data[0].B64Json.Should().Be("aGVsbG8=");
            result.Usage.Should().NotBeNull();
            result.Usage!.ProviderReportedCostUsd.Should().Be(0.003m);
            result.Usage.ImageCount.Should().Be(1);
            result.Usage.ImageResolution.Should().Be("1024x1024");
        }

        [Fact]
        public async Task CreateVideoAsync_SubmitsThenPolls_ReturnsUnsignedUrlAndCost()
        {
            // Arrange — job completes on first poll with an unsigned URL + cost
            _videoStatusJson = "{\"status\":\"completed\",\"unsigned_urls\":[\"https://openrouter.ai/videos/out.mp4\"],\"usage\":{\"cost\":0.5,\"is_byok\":false}}";
            var client = CreateClient();
            var request = new VideoGenerationRequest { Prompt = "a dog running", Model = "some/video-model", Duration = 5 };

            // Act
            var result = await client.CreateVideoAsync(request);

            // Assert
            result.Data.Should().ContainSingle();
            result.Data[0].Url.Should().Be("https://openrouter.ai/videos/out.mp4");
            result.Data[0].Metadata!.Format.Should().Be("mp4");
            result.Usage.Should().NotBeNull();
            result.Usage!.EstimatedCost.Should().Be(0.5m);
            result.Usage.VideosGenerated.Should().Be(1);
            result.Usage.TotalDurationSeconds.Should().Be(5);
        }

        [Fact]
        public async Task CreateVideoAsync_FailedJob_ThrowsLLMCommunicationException()
        {
            // Arrange
            _videoStatusJson = "{\"status\":\"failed\"}";
            var client = CreateClient();
            var request = new VideoGenerationRequest { Prompt = "a dog running", Model = "some/video-model", Duration = 5 };

            // Act
            var act = () => client.CreateVideoAsync(request);

            // Assert
            await act.Should().ThrowAsync<ConduitLLM.Core.Exceptions.LLMCommunicationException>();
        }

        [Fact]
        public void CreateVideoAsync_SignatureMatchesOrchestratorReflectionContract()
        {
            // The video orchestrator discovers video support by looking for a 3-parameter
            // method named "CreateVideoAsync"; guard that contract.
            var hasContract = typeof(OpenRouterClient).GetMethods()
                .Any(m => m.Name == "CreateVideoAsync" && m.GetParameters().Length == 3);

            hasContract.Should().BeTrue();
        }

        [Fact]
        public async Task TranscribeAudioAsync_PostsToTranscriptionsEndpoint_MapsTextAndCost()
        {
            // Arrange
            _transcriptionJson = "{\"text\":\"hello world\",\"duration\":12.5,\"usage\":{\"cost\":0.02}}";
            var client = CreateClient();
            var request = new AudioTranscriptionRequest
            {
                Model = "openai/whisper-1",
                AudioData = new byte[] { 1, 2, 3 },
                FileName = "audio.mp3",
                ContentType = "audio/mpeg"
            };

            // Act
            var result = await client.TranscribeAudioAsync(request);

            // Assert
            _capturedRequests.Single(r => r.Method == "POST").Path.Should().EndWith("/audio/transcriptions");
            result.Text.Should().Be("hello world");
            result.DurationSeconds.Should().Be(12.5);
            result.Usage!.ProviderReportedCostUsd.Should().Be(0.02m);
        }

        [Fact]
        public async Task TranscribeAudioAsync_ForwardsModernMultipartFields()
        {
            var client = CreateClient();
            var request = new AudioTranscriptionRequest
            {
                Model = "openai/whisper-1",
                AudioData = [1, 2, 3],
                FileName = "audio.mp3",
                ChunkingStrategy = JsonSerializer.SerializeToElement("auto"),
                Include = ["logprobs"],
                KnownSpeakerNames = ["Alice"],
                KnownSpeakerReferences =
                [
                    new AudioTranscriptionReference
                    {
                        AudioData = [4, 5],
                        FileName = "alice.wav",
                        ContentType = "audio/wav"
                    }
                ],
                Stream = true,
                TimestampGranularities = ["word"]
            };

            await client.TranscribeAudioAsync(request);

            var body = _capturedRequests.Single(r => r.Path.EndsWith("/audio/transcriptions")).Body;
            body.Should().Contain("name=chunking_strategy");
            body.Should().Contain("\"auto\"");
            body.Should().Contain("name=\"include[]\"");
            body.Should().Contain("logprobs");
            body.Should().Contain("name=\"known_speaker_names[]\"");
            body.Should().Contain("Alice");
            body.Should().Contain("name=\"known_speaker_references[]\"");
            body.Should().Contain("filename=alice.wav");
            body.Should().Contain("name=stream");
            body.Should().Contain("true");
            body.Should().Contain("name=\"timestamp_granularities[]\"");
            body.Should().Contain("word");
        }

        [Fact]
        public async Task CreateSpeechAsync_ReturnsAudioBytes_AndCharacterUsage()
        {
            // Arrange
            var client = CreateClient();
            var request = new TextToSpeechRequest { Model = "openai/tts-1", Input = "hello", Voice = "alloy" };

            // Act
            var result = await client.CreateSpeechAsync(request);

            // Assert
            _capturedRequests.Single(r => r.Method == "POST").Path.Should().EndWith("/audio/speech");
            result.AudioData.Should().NotBeEmpty();
            result.ContentType.Should().Contain("audio");
            result.Usage!.TtsCharacters.Should().Be(5); // "hello".Length
        }

        [Fact]
        public async Task CreateSpeechAsync_ForwardsInstructionsAndStreamFormat()
        {
            var client = CreateClient();
            var request = new TextToSpeechRequest
            {
                Model = "openai/tts-1",
                Input = "hello",
                Voice = "alloy",
                ResponseFormat = "wav",
                Instructions = "Speak softly",
                StreamFormat = "sse"
            };

            await client.CreateSpeechAsync(request);

            using var body = JsonDocument.Parse(
                _capturedRequests.Single(r => r.Path.EndsWith("/audio/speech")).Body);
            body.RootElement.GetProperty("response_format").GetString().Should().Be("wav");
            body.RootElement.GetProperty("instructions").GetString().Should().Be("Speak softly");
            body.RootElement.GetProperty("stream_format").GetString().Should().Be("sse");
        }

        [Fact]
        public async Task CreateRerankAsync_PostsToRerankEndpoint_MapsResultsAndSynthesizesSearchUnits()
        {
            // Arrange — provider returns ranked results but no usage; the client synthesizes search units
            _rerankJson = "{\"model\":\"cohere/rerank-v3.5\",\"results\":[{\"index\":1,\"relevance_score\":0.9},{\"index\":0,\"relevance_score\":0.4}]}";
            var client = CreateClient();
            var request = new RerankRequest
            {
                Model = "cohere/rerank-v3.5",
                Query = "best language model",
                Documents = new List<string> { "doc a", "doc b" }
            };

            // Act
            var result = await client.CreateRerankAsync(request);

            // Assert
            _capturedRequests.Single(r => r.Method == "POST").Path.Should().EndWith("/rerank");
            result.Results.Should().HaveCount(2);
            result.Results[0].Index.Should().Be(1);
            result.Results[0].RelevanceScore.Should().Be(0.9);
            result.Usage!.SearchUnits.Should().Be(1); // ceil(2 / 100)
        }

        [Fact]
        public async Task CreateChatCompletionAsync_WithMappingOptions_MergesIntoRequest()
        {
            // Arrange — per-mapping routing options configured on the client
            var client = CreateClient(providerOptionsJson: "{\"provider\":{\"order\":[\"anthropic\"]},\"transforms\":[\"middle-out\"]}");
            var request = new ChatCompletionRequest
            {
                Model = "openai/gpt-4o",
                Messages = new List<Message> { new() { Role = "user", Content = "hi" } }
            };

            // Act
            await client.CreateChatCompletionAsync(request);

            // Assert — the mapping options were merged into the outgoing request body
            var body = _capturedRequests.Single(r => r.Path.EndsWith("/chat/completions")).Body;
            body.Should().Contain("\"provider\"");
            body.Should().Contain("\"order\"");
            body.Should().Contain("\"transforms\"");
        }

        [Fact]
        public async Task CreateChatCompletionAsync_CallerExtensionData_WinsOverMappingOptions()
        {
            // Arrange — mapping sets provider.sort=price; caller sends its own provider object
            var client = CreateClient(providerOptionsJson: "{\"provider\":{\"sort\":\"price\"}}");
            var request = new ChatCompletionRequest
            {
                Model = "openai/gpt-4o",
                Messages = new List<Message> { new() { Role = "user", Content = "hi" } },
                ExtensionData = new Dictionary<string, JsonElement>
                {
                    ["provider"] = JsonDocument.Parse("{\"sort\":\"throughput\"}").RootElement
                }
            };

            // Act
            await client.CreateChatCompletionAsync(request);

            // Assert — the caller's provider object wins (whole-key precedence)
            var body = _capturedRequests.Single(r => r.Path.EndsWith("/chat/completions")).Body;
            body.Should().Contain("throughput");
            body.Should().NotContain("price");
        }

        [Fact]
        public async Task CreateChatCompletionAsync_MalformedMappingOptions_IgnoredAndRequestSucceeds()
        {
            // Arrange — invalid JSON must not break the request
            var client = CreateClient(providerOptionsJson: "not valid json");
            var request = new ChatCompletionRequest
            {
                Model = "openai/gpt-4o",
                Messages = new List<Message> { new() { Role = "user", Content = "hi" } }
            };

            // Act
            var response = await client.CreateChatCompletionAsync(request);

            // Assert
            response.Should().NotBeNull();
        }
    }
}
