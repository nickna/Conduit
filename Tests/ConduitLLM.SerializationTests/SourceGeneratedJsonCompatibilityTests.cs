using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Serialization;
using ConduitLLM.Admin.Models.ModelAuthors;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Configuration.Serialization;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.DTOs;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Models;
using ConduitLLM.Gateway.Serialization;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Providers.Bedrock;
using ConduitLLM.Providers.Serialization;

namespace ConduitLLM.SerializationTests;

public sealed class SourceGeneratedJsonCompatibilityTests
{
    private static readonly DateTime FixtureTime =
        new(2026, 7, 27, 12, 34, 56, DateTimeKind.Utc);

    [Fact]
    public void Test_profile_disables_reflection_defaults()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(new UnregisteredContract()));
    }

    [Fact]
    public void Gateway_chat_contract_is_bidirectionally_compatible()
    {
        var value = new ChatCompletionResponse
        {
            Id = "chatcmpl-sourcegen",
            Object = "chat.completion",
            Created = 1_722_083_696,
            Model = "gpt-test",
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    FinishReason = "stop",
                    Message = new Message { Role = "assistant", Content = "Hello" }
                }
            ],
            Usage = new Usage
            {
                PromptTokens = 4,
                CompletionTokens = 2,
                TotalTokens = 6
            }
        };

        AssertCompatible(
            value,
            CoreHttpJsonContext.Default.ChatCompletionResponse,
            "gateway-chat-response.json",
            SnakeCaseWireOptions());

        var hostOptions = GatewayJsonOptions.Create();
        AssertJsonEqual(
            ReadFixture("gateway-chat-response.json"),
            JsonSerializer.Serialize(value, hostOptions));
    }

    [Fact]
    public void Wolverine_message_is_bidirectionally_compatible()
    {
        var value = new SpendUpdateRequested
        {
            KeyId = 42,
            Amount = 1.25m,
            RequestId = "req-sourcegen",
            EventId = "event-sourcegen",
            Timestamp = FixtureTime,
            CorrelationId = "corr-sourcegen"
        };

        AssertCompatible(
            value,
            CoreMessagingJsonContext.Default.SpendUpdateRequested,
            "wolverine-spend-update.json",
            new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() });

        var runtimeOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = CoreMessagingJsonContext.Default
        };
        AssertJsonEqual(
            ReadFixture("wolverine-spend-update.json"),
            JsonSerializer.Serialize(value, typeof(SpendUpdateRequested), runtimeOptions));
    }

    [Fact]
    public void SignalR_notification_preserves_live_camel_case_contract()
    {
        var value = new VirtualKeyCreatedNotification
        {
            KeyId = 42,
            KeyName = "serialization-test",
            IsEnabled = true,
            MaxBudget = 25.50m,
            AllowedModels = "gpt-test",
            RateLimitPerMinute = 60,
            CreatedAt = FixtureTime,
            ExpiresAt = FixtureTime.AddDays(30),
            Timestamp = FixtureTime
        };

        AssertCompatible(
            value,
            ConfigurationSignalRJsonContext.Default.VirtualKeyCreatedNotification,
            "signalr-virtual-key-created.json",
            CamelCaseOptions());
    }

    [Fact]
    public void Gateway_model_list_preserves_openai_envelope()
    {
        var value = new ModelListResponse(
            [new ModelListItemDto("gpt-test", "model", 1_722_083_696, "conduit")],
            "list");

        AssertCompatible(
            value,
            GatewayHttpJsonContext.Default.ModelListResponse,
            "gateway-model-list.json",
            SnakeCaseWireOptions());
    }

    [Fact]
    public void Service_http_options_resolve_advertised_contracts_without_reflection()
    {
        var admin = AdminJsonOptions.Create();
        var gateway = GatewayJsonOptions.Create();

        Assert.DoesNotContain(
            admin.TypeInfoResolverChain,
            static resolver => resolver is DefaultJsonTypeInfoResolver);
        Assert.DoesNotContain(
            gateway.TypeInfoResolverChain,
            static resolver => resolver is DefaultJsonTypeInfoResolver);

        Assert.NotNull(admin.GetTypeInfo(typeof(UpdateModelAuthorDto)));
        Assert.NotNull(admin.GetTypeInfo(typeof(IEnumerable<ModelAuthorDto>)));
        Assert.NotNull(admin.GetTypeInfo(typeof(ProviderType?)));
        Assert.NotNull(admin.GetTypeInfo(typeof(Microsoft.AspNetCore.Http.IFormFile)));
        Assert.NotNull(gateway.GetTypeInfo(typeof(EphemeralKeyResponse)));
        Assert.NotNull(gateway.GetTypeInfo(typeof(Microsoft.AspNetCore.Http.IFormFile)));
        Assert.NotNull(gateway.GetTypeInfo(typeof(MediaUsageMetadata)));
    }

    [Fact]
    public void Gateway_internal_metadata_uses_named_contracts_without_shape_expansion()
    {
        var value = new MediaTaskAccountingMetadata(
            "image",
            "task-sourcegen",
            "queued",
            ImageCount: 2,
            Quality: "hd",
            Size: "1024x1024",
            Style: "vivid");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(
            value,
            GatewayInternalJsonContext.Default.MediaTaskAccountingMetadata));
        var root = document.RootElement;

        Assert.Equal("image", root.GetProperty("type").GetString());
        Assert.Equal("task-sourcegen", root.GetProperty("taskId").GetString());
        Assert.Equal(2, root.GetProperty("imageCount").GetInt32());
        Assert.False(root.TryGetProperty("durationSeconds", out _));
        Assert.False(root.TryGetProperty("resolution", out _));
        Assert.False(root.TryGetProperty("fps", out _));
    }

    [Fact]
    public void Service_http_options_preserve_enum_wire_naming()
    {
        Assert.Equal(
            "\"openAICompatible\"",
            JsonSerializer.Serialize(ProviderType.OpenAICompatible, AdminJsonOptions.Create()));
        Assert.Equal(
            "\"timed_out\"",
            JsonSerializer.Serialize(
                ConduitLLM.Functions.Enums.ExecutionState.TimedOut,
                GatewayJsonOptions.Create()));
    }

    [Fact]
    public void Discovery_projection_uses_service_owned_wire_dialects()
    {
        var capabilities = new DiscoveryModelCapabilitiesDto(
            Chat: true,
            ChatStream: true,
            ImageInput: false,
            VideoInput: false,
            AudioInput: false,
            FileInput: false,
            Vision: false,
            VideoUnderstanding: false,
            ImageGeneration: false,
            VideoGeneration: false,
            Embeddings: false,
            FunctionCalling: true,
            SpeechToText: false,
            TextToSpeech: false,
            Rerank: false);
        var value = new DiscoveryModelsResponse(
            [
                new DiscoveredModelDto(
                    "gpt-test",
                    "openai",
                    "GPT Test",
                    "Test model",
                    "https://example.test/models/gpt-test",
                    12_000,
                    8_000,
                    4_000,
                    "cl100k",
                    ["text"],
                    ["text"],
                    "provider",
                    FixtureTime,
                    "{}",
                    capabilities)
            ],
            1);

        using var adminDocument = JsonDocument.Parse(JsonSerializer.Serialize(
            value,
            AdminHttpJsonContext.Default.DiscoveryModelsResponse));
        using var gatewayDocument = JsonDocument.Parse(JsonSerializer.Serialize(
            value,
            GatewayHttpJsonContext.Default.DiscoveryModelsResponse));

        var adminModel = adminDocument.RootElement.GetProperty("data")[0];
        var gatewayModel = gatewayDocument.RootElement.GetProperty("data")[0];

        Assert.True(adminModel.TryGetProperty("displayName", out _));
        Assert.True(adminModel.GetProperty("capabilities").TryGetProperty("chatStream", out _));
        Assert.False(adminModel.TryGetProperty("display_name", out _));

        Assert.True(gatewayModel.TryGetProperty("display_name", out _));
        Assert.True(gatewayModel.GetProperty("capabilities").TryGetProperty("chat_stream", out _));
        Assert.False(gatewayModel.TryGetProperty("displayName", out _));
    }

    [Fact]
    public void Admin_problem_details_preserves_error_contract()
    {
        var value = new AdminProblemDetails
        {
            Type = "https://errors.conduit.test/validation",
            Title = "Validation failed",
            Status = 400,
            Detail = "The request was invalid.",
            Instance = "/api/test",
            Code = "validation_error",
            TraceId = "trace-sourcegen",
            Errors = new Dictionary<string, string[]>
            {
                ["model"] = ["The model field is required."]
            }
        };

        AssertCompatible(
            value,
            AdminHttpJsonContext.Default.AdminProblemDetails,
            "admin-problem-details.json",
            CamelCaseOptions());
    }

    [Fact]
    public void Bedrock_response_preserves_provider_wire_contract()
    {
        var value = new BedrockConverseResponse
        {
            Output = new BedrockConverseOutput
            {
                Message = new BedrockMessage
                {
                    Role = "assistant",
                    Content = [new BedrockContentBlock { Text = "Hello from Bedrock" }]
                }
            },
            StopReason = "end_turn",
            Usage = new BedrockUsage { InputTokens = 4, OutputTokens = 3, TotalTokens = 7 }
        };

        AssertCompatible(
            value,
            ProvidersJsonContext.Default.BedrockConverseResponse,
            "bedrock-converse-response.json",
            CamelCaseWireOptions());
    }

    [Fact]
    public void Redis_reliability_payload_is_bidirectionally_compatible()
    {
        var value = new RedisWebhookCircuitBreaker.CircuitState
        {
            State = "Open",
            OpenedAt = FixtureTime,
            HalfOpenTestAt = null,
            FailureCount = 5,
            WebhookUrl = "https://hooks.example.test/events"
        };

        AssertCompatible(
            value,
            CoreRedisJsonContext.Default.CircuitState,
            "redis-webhook-circuit-state.json",
            CaseInsensitivePascalOptions());
    }

    [Fact]
    public void Redis_invalidation_payload_is_bidirectionally_compatible()
    {
        var value = new RedisVirtualKeyCache.VirtualKeyBatchInvalidation
        {
            KeyHashes = ["hash-one", "hash-two"],
            Timestamp = FixtureTime
        };

        AssertCompatible(
            value,
            GatewayRedisJsonContext.Default.VirtualKeyBatchInvalidation,
            "redis-virtual-key-invalidation.json",
            CaseInsensitivePascalOptions());
    }

    [Fact]
    public void Previous_release_model_cost_cache_entry_is_readable_and_new_entry_is_legacy_readable()
    {
        var legacyOptions = CaseInsensitivePascalOptions();
        var fixture = ReadFixture("redis-model-cost-legacy.json");

        var generatedRead = JsonSerializer.Deserialize(
            fixture,
            GatewayRedisJsonContext.Default.ModelCost);
        Assert.NotNull(generatedRead);
        Assert.Equal(7, generatedRead.Id);
        Assert.Equal("Legacy token pricing", generatedRead.CostName);

        var generatedJson = JsonSerializer.Serialize(
            generatedRead,
            GatewayRedisJsonContext.Default.ModelCost);
        var legacyRead = JsonSerializer.Deserialize<ModelCost>(generatedJson, legacyOptions);
        Assert.NotNull(legacyRead);
        Assert.Equal(generatedRead.Id, legacyRead.Id);
        Assert.Equal(generatedRead.PricingModel, legacyRead.PricingModel);
        Assert.Equal(generatedRead.InputCostPerMillionTokens, legacyRead.InputCostPerMillionTokens);
    }

    private static void AssertCompatible<T>(
        T value,
        JsonTypeInfo<T> generatedTypeInfo,
        string fixtureName,
        JsonSerializerOptions legacyOptions)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);

        var fixture = ReadFixture(fixtureName);
        var generatedJson = JsonSerializer.Serialize(value, generatedTypeInfo);
        var legacyJson = JsonSerializer.Serialize(value, legacyOptions);

        AssertJsonEqual(fixture, generatedJson);
        AssertJsonEqual(fixture, legacyJson);

        var generatedReadsLegacy = JsonSerializer.Deserialize(legacyJson, generatedTypeInfo);
        Assert.NotNull(generatedReadsLegacy);
        AssertJsonEqual(
            generatedJson,
            JsonSerializer.Serialize(generatedReadsLegacy, generatedTypeInfo));

        var legacyReadsGenerated = JsonSerializer.Deserialize<T>(generatedJson, legacyOptions);
        Assert.NotNull(legacyReadsGenerated);
        AssertJsonEqual(legacyJson, JsonSerializer.Serialize(legacyReadsGenerated, legacyOptions));
    }

    private static JsonSerializerOptions SnakeCaseWireOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static JsonSerializerOptions CamelCaseOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static JsonSerializerOptions CamelCaseWireOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static JsonSerializerOptions CaseInsensitivePascalOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static void AssertJsonEqual(string expected, string actual) =>
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            $"Expected: {expected}{Environment.NewLine}Actual:   {actual}");

    private sealed class UnregisteredContract;
}
