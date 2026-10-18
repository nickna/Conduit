using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Configuration.Serialization;
using ConduitLLM.Contracts.SignalR;
using ConduitLLM.Core.Caching;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Helpers;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Messaging;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Serialization;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Functions.Serialization;

using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Wolverine;
using Wolverine.Runtime.Serialization;

namespace ConduitLLM.SerializationTests;

public sealed class WireBoundaryContractTests
{
    public static IEnumerable<object[]> WolverineMessages() =>
        ConduitMessagingTopology.WebhookDeliveryEvents
            .Concat(ConduitMessagingTopology.SpendUpdateEvents)
            .Concat(ConduitMessagingTopology.VideoGenerationEvents)
            .Concat(ConduitMessagingTopology.ImageGenerationEvents)
            .Concat(ConduitMessagingTopology.GatewayEvents)
            .Concat(ConduitMessagingTopology.SharedEvents)
            .Concat(ConduitMessagingTopology.AdminHeartbeatEvents)
            .Distinct()
            .Select(type => new object[] { type });

    public static IEnumerable<object[]> SignalRNotifications() =>
        typeof(VirtualKeyCreatedNotification).Assembly.GetTypes()
            .Where(type => type.Namespace == typeof(VirtualKeyCreatedNotification).Namespace)
            .Concat(typeof(ImageGenerationStartedNotification).Assembly.GetTypes()
                .Where(type => type.Namespace == typeof(ImageGenerationStartedNotification).Namespace))
            .Where(type => type.IsClass && !type.IsAbstract)
            .Select(type => new object[] { type });

    [Theory]
    [MemberData(nameof(SignalRNotifications))]
    public void Every_live_notification_resolves_generated_metadata(Type notificationType)
    {
        Assert.NotNull(ConfigurationSignalRJsonContext.Default.GetTypeInfo(notificationType));
    }

    [Theory]
    [MemberData(nameof(WolverineMessages))]
    public void Every_routed_message_round_trips_in_both_release_formats(Type messageType)
    {
        var generated = SystemTextJsonSerializer.DefaultOptions();
        // Simulate serializer defaults installed by a transport before service configuration.
        generated.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
        CoreMessagingJsonContext.Configure(generated);
        Assert.Same(CoreMessagingJsonContext.Default, Assert.Single(generated.TypeInfoResolverChain));

        var value = Activator.CreateInstance(messageType)!;
        var legacyOptions = SystemTextJsonSerializer.DefaultOptions();
        legacyOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        AssertCompatible(value, generated.GetTypeInfo(messageType), legacyOptions);

        // Exercise the same transport configuration as both service hosts. A
        // plain context's Pascal-case default is a separate persistence policy.
        var transport = new WolverineOptions();
        transport.UseSystemTextJsonForSerialization(CoreMessagingJsonContext.Configure);
        var bytes = transport.DefaultSerializer.WriteMessage(value);
        var json = Encoding.UTF8.GetString(bytes);
        var legacy = JsonSerializer.Serialize(value, messageType, legacyOptions);
        AssertJsonEqual(legacy, json);
        var transportReadsLegacy = transport.DefaultSerializer.ReadFromData(messageType,
            new Envelope { Data = Encoding.UTF8.GetBytes(legacy) });
        AssertJsonEqual(json, Encoding.UTF8.GetString(transport.DefaultSerializer.WriteMessage(transportReadsLegacy)));
        var legacyReadsTransport = JsonSerializer.Deserialize(json, messageType, legacyOptions)!;
        AssertJsonEqual(legacy, JsonSerializer.Serialize(legacyReadsTransport, messageType, legacyOptions));
    }

    [Fact]
    public void Wolverine_metadata_preserves_nested_runtime_json_types()
    {
        var value = new MediaGenerationCompleted
        {
            VirtualKeyId = 42,
            Metadata = new()
            {
                ["attempt"] = 2,
                ["size"] = 12L,
                ["recovered"] = true,
                ["price"] = 0.25m,
                ["details"] = new Dictionary<string, object>
                {
                    ["tags"] = new object[] { "image", 3, false },
                    ["provider"] = JsonDocument.Parse("""{"id":"operation-1"}""").RootElement.Clone()
                }
            }
        };
        var options = SystemTextJsonSerializer.DefaultOptions();
        CoreMessagingJsonContext.Configure(options);
        var legacy = SystemTextJsonSerializer.DefaultOptions();
        legacy.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        AssertCompatible(value, options.GetTypeInfo(typeof(MediaGenerationCompleted)), legacy);
    }

    [Fact]
    public async Task Enhanced_SSE_writer_preserves_live_metrics_reasoning_tool_and_error_contracts()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var context = new DefaultHttpContext();
        using var stream = new MemoryStream();
        context.Response.Body = stream;
        var writer = new EnhancedSSEResponseWriter(context.Response, GatewayJsonOptions.Create());
        var collector = new StreamingMetricsCollector("request-1", "model-1", "provider-1");
        collector.RecordFirstToken();
        collector.RecordToken();
        var metrics = collector.GetMetrics();
        var finalMetrics = collector.GetFinalMetrics(new Usage
        {
            PromptTokens = 3, CompletionTokens = 2, TotalTokens = 5
        });
        var tool = new ToolExecutionEvent { ToolCallId = "call-1", FunctionName = "lookup", Status = "completed", Cost = 0.25m };
        var providerError = new ProviderErrorDetail("provider-1", "rate_limit_exceeded", 429, "retry later");

        await writer.WriteMetricsEventAsync(metrics);
        await writer.WriteFinalMetricsEventAsync(finalMetrics);
        await writer.WriteReasoningEventAsync("Planning the lookup");
        await writer.WriteToolExecutingEventAsync(tool);
        await writer.WriteErrorEventAsync("Request failed");
        await writer.WriteErrorEventAsync("Request failed", providerError);

        var events = ReadSseEvents(stream);
        Assert.Equal(new[] { "metrics", "metrics-final", "reasoning", "tool-executing", "error", "error" },
            events.Select(item => item.Name));
        var legacy = LegacyOptions(JsonNamingPolicy.SnakeCaseLower, ignoreNulls: true);
        AssertJsonEqual(JsonSerializer.Serialize(metrics, legacy), events[0].Json);
        AssertJsonEqual(JsonSerializer.Serialize(finalMetrics, legacy), events[1].Json);
        AssertJsonEqual("""{"content":"Planning the lookup"}""", events[2].Json);
        AssertJsonEqual(JsonSerializer.Serialize(tool, legacy), events[3].Json);
        AssertJsonEqual("""{"error":"Request failed"}""", events[4].Json);
        AssertJsonEqual("""{"error":"Request failed","provider_error":{"provider":"provider-1","error_type":"rate_limit_exceeded","upstream_status":429,"raw_message":"retry later"}}""", events[5].Json);
        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal(6, writer.EventsWritten);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Standard_SSE_errors_preserve_OpenAI_error_envelopes(bool includeProviderDetail)
    {
        var context = new DefaultHttpContext();
        using var stream = new MemoryStream();
        context.Response.Body = stream;
        var writer = new EnhancedSSEResponseWriter(context.Response, GatewayJsonOptions.Create(), enhancedEvents: false);
        var detail = includeProviderDetail
            ? new ProviderErrorDetail("provider-1", "rate_limit_exceeded", 429, "retry later")
            : null;
        await writer.WriteErrorEventAsync("Request failed", detail);
        var frame = Assert.Single(ReadSseEvents(stream));
        Assert.Null(frame.Name);
        using var document = JsonDocument.Parse(frame.Json);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("Request failed", error.GetProperty("message").GetString());
        Assert.Equal("server_error", error.GetProperty("type").GetString());
        Assert.Equal("provider_response_error", error.GetProperty("code").GetString());
        Assert.Equal(includeProviderDetail, error.TryGetProperty("metadata", out var metadata));
        if (includeProviderDetail)
            Assert.Equal(429, metadata.GetProperty("provider_error").GetProperty("upstream_status").GetInt32());
    }

    private static List<(string? Name, string Json)> ReadSseEvents(MemoryStream stream) =>
        Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(frame =>
            {
                var lines = frame.Split('\n');
                var name = lines.FirstOrDefault(line => line.StartsWith("event: ", StringComparison.Ordinal))?[7..];
                var json = Assert.Single(lines.Where(line => line.StartsWith("data: ", StringComparison.Ordinal)))[6..];
                return (name, json);
            }).ToList();

    [Fact]
    public void SignalR_uses_generated_metadata_for_dynamic_task_arguments()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddConduitSignalR(builder.Environment, null, "test", 0, "test",
            additionalPayloadResolver: GatewaySignalRJsonContext.Default);
        using var services = builder.Services.BuildServiceProvider();
        var protocol = services.GetServices<IHubProtocol>().Single(value => value.Name == "json");
        var payload = new Dictionary<string, object>
        {
            ["taskId"] = "task-1",
            ["state"] = "completed",
            ["result"] = new MediaGenerationTaskResult
            {
                Created = 123,
                Model = "image-model",
                Data = [new MediaGenerationTaskResultItem { Url = "https://example.test/image.png" }]
            },
            ["metadata"] = new TaskMetadata(42)
            {
                ExtensionData = new() { ["recovered"] = true }
            }
        };
        var buffer = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(new InvocationMessage("TaskUpdated", [payload]), buffer);
        var json = Encoding.UTF8.GetString(buffer.WrittenSpan).TrimEnd('\u001e');
        using var document = JsonDocument.Parse(json);
        var argument = document.RootElement.GetProperty("arguments")[0];
        Assert.Equal("image-model", argument.GetProperty("result").GetProperty("model").GetString());
        Assert.Equal(42, argument.GetProperty("metadata").GetProperty("virtualKeyId").GetInt32());
        Assert.True(argument.GetProperty("metadata").GetProperty("recovered").GetBoolean());

        var legacy = LegacyOptions(JsonNamingPolicy.CamelCase);
        var generatedOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = JsonTypeInfoResolver.Combine(
                ConfigurationSignalRJsonContext.Default, GatewaySignalRJsonContext.Default)
        };
        AssertCompatible(payload, generatedOptions.GetTypeInfo(payload.GetType()), legacy);
    }

    [Fact]
    public void Discovery_cache_reads_previous_release_fixture_and_writes_legacy_readable_entries()
    {
        const string fixture = """
            {"Data":[{"id":"image-model","capabilities":{"image_generation":true}}],
             "Count":1,"CachedAt":"2026-07-27T12:34:56Z","CapabilityFilter":"image_generation"}
            """;
        var serializer = new ApplicationCacheSerializer();
        var value = serializer.Deserialize<DiscoveryModelsResult>(Encoding.UTF8.GetBytes(fixture))!;
        Assert.Equal("image-model", value.Data[0].GetProperty("id").GetString());
        var json = Encoding.UTF8.GetString(serializer.Serialize(value));
        AssertJsonEqual(fixture, json);
        var previousReleaseRead = JsonSerializer.Deserialize<DiscoveryModelsResult>(json, LegacyOptions())!;
        Assert.Equal(value.Count, previousReleaseRead.Count);
        Assert.Equal(value.CachedAt, previousReleaseRead.CachedAt);
        AssertJsonEqual(json, JsonSerializer.Serialize(previousReleaseRead, LegacyOptions(ignoreNulls: true)));
        Assert.Throws<NotSupportedException>(() => serializer.Serialize(new UnregisteredContract()));
    }

    [Theory]
    [InlineData(typeof(ImageCompletionWebhookPayload))]
    [InlineData(typeof(ImageProgressWebhookPayload))]
    [InlineData(typeof(VideoCompletionWebhookPayload))]
    [InlineData(typeof(VideoProgressWebhookPayload))]
    public void Named_webhook_contracts_preserve_previous_release_wire_format(Type payloadType)
    {
        var payload = (WebhookPayloadBase)Activator.CreateInstance(payloadType)!;
        payload.TaskId = "task-1";
        payload.Status = "completed";
        payload.Timestamp = new DateTime(2026, 7, 27, 12, 34, 56, DateTimeKind.Utc);
        var typeInfo = CoreWebhookJsonContext.Default.GetTypeInfo(payloadType)!;
        AssertCompatible(payload, typeInfo, LegacyOptions(JsonNamingPolicy.CamelCase));
        AssertJsonEqual(JsonSerializer.Serialize(payload, typeInfo),
            WebhookPayloadHelper.SerializePayload(payload, CoreWebhookJsonContext.Default));

        // Receiver POSTs have always omitted nulls; durable event bodies retain them.
        var receiverContext = new CoreWebhookJsonContext(new JsonSerializerOptions(ConduitJsonOptions.Wire));
        AssertCompatible(payload, receiverContext.GetTypeInfo(payloadType)!,
            LegacyOptions(JsonNamingPolicy.CamelCase, ignoreNulls: true));
    }

    [Fact]
    public void Custom_webhook_json_preserves_registered_runtime_value_shapes()
    {
        var payload = new Dictionary<string, object>
        {
            ["text"] = "completed",
            ["tags"] = new List<object> { 1, true, 3.5m, 9L },
            ["headers"] = new Dictionary<string, string> { ["trace"] = "trace-1" },
            ["provider"] = JsonNode.Parse("""{"id":"operation-1"}""")!.AsObject()
        };
        AssertCompatible(payload, CoreWebhookJsonContext.Default.DictionaryStringObject,
            LegacyOptions(JsonNamingPolicy.CamelCase));
    }

    [Fact]
    public void Function_request_persistence_preserves_nested_programmatic_parameter_values()
    {
        var request = new FunctionExecutionRequestData
        {
            Parameters = new()
            {
                ["urls"] = new[] { "https://example.test/first", "https://example.test/second" },
                ["options"] = new List<object>
                {
                    new Dictionary<string, object> { ["limit"] = 2 },
                    new object[] { "include", true },
                    JsonNode.Parse("""{"provider":"search"}""")!.AsObject(),
                    JsonNode.Parse("""["text","summary"]""")!.AsArray()
                }
            },
            Metadata = new() { ["traceId"] = "trace-1" },
            IdempotencyKey = "search-1"
        };
        AssertCompatible(request, FunctionsJsonContext.Default.FunctionExecutionRequestData,
            LegacyOptions(JsonNamingPolicy.CamelCase, ignoreNulls: true));
    }

    [Fact]
    public void Webhook_helper_requires_registered_metadata_and_preserves_size_limit_envelope()
    {
        var payload = new ImageProgressWebhookPayload
        {
            TaskId = "task-1", Status = "processing", ImagesCompleted = 1, TotalImages = 2
        };
        var original = WebhookPayloadHelper.SerializePayload(payload, CoreWebhookJsonContext.Default);
        var truncated = WebhookPayloadHelper.SerializePayload(payload, CoreWebhookJsonContext.Default, 1);
        using var document = JsonDocument.Parse(truncated);
        var root = document.RootElement;
        Assert.Equal("Payload too large", root.GetProperty("error").GetString());
        Assert.Equal(Encoding.UTF8.GetByteCount(original), root.GetProperty("originalSizeBytes").GetInt32());
        Assert.Equal(1, root.GetProperty("maxSizeBytes").GetInt32());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Throws<NotSupportedException>(() =>
            WebhookPayloadHelper.SerializePayload(new UnregisteredContract(), CoreWebhookJsonContext.Default));
    }

    private static void AssertCompatible(object value, JsonTypeInfo typeInfo, JsonSerializerOptions legacyOptions)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var generated = JsonSerializer.Serialize(value, typeInfo);
        var legacy = JsonSerializer.Serialize(value, value.GetType(), legacyOptions);
        AssertJsonEqual(legacy, generated);
        var generatedReadsLegacy = JsonSerializer.Deserialize(legacy, typeInfo)!;
        AssertJsonEqual(generated, JsonSerializer.Serialize(generatedReadsLegacy, typeInfo));
        var legacyReadsGenerated = JsonSerializer.Deserialize(generated, value.GetType(), legacyOptions)!;
        AssertJsonEqual(legacy, JsonSerializer.Serialize(legacyReadsGenerated, value.GetType(), legacyOptions));
    }

    private static JsonSerializerOptions LegacyOptions(JsonNamingPolicy? naming = null, bool ignoreNulls = false) => new()
    {
        PropertyNamingPolicy = naming,
        DefaultIgnoreCondition = ignoreNulls ? JsonIgnoreCondition.WhenWritingNull : JsonIgnoreCondition.Never,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static void AssertJsonEqual(string expected, string actual) => Assert.True(
        JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
        $"Expected: {expected}{Environment.NewLine}Actual: {actual}");

    private sealed class UnregisteredContract;
}
