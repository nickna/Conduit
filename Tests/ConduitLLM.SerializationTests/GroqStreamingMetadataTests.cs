#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Providers.Groq;
using ConduitLLM.Providers.Streaming;

using Microsoft.Extensions.Logging.Abstractions;

namespace ConduitLLM.SerializationTests;

public sealed class GroqStreamingMetadataTests
{
    public static TheoryData<string> ProviderFrames => new()
    {
        """{"id":"role","choices":[{"index":0,"delta":{"role":"assistant"}}]}""",
        """{"id":"text","model":"llama","choices":[{"index":0,"delta":{"content":"café 雪 \"quoted\""}}]}""",
        """{"id":"tools","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call-1","type":"function","function":{"name":"lookup","arguments":"{\"key\":"}}]}}]}""",
        """{"id":"usage","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""",
        """{"id":"vendor-usage","choices":[],"x_groq":{"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,"code_interpreter":2,"code_interpreter_duration_seconds":1.25,"browser_search":1,"python":3}}}""",
        """{"id":"both-usage","choices":[],"usage":{"prompt_tokens":1,"completion_tokens":2,"total_tokens":3},"x_groq":{"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}}""",
        """{"id":"vendor-info","choices":[],"x_groq":{"id":"vendor-id"}}"""
    };

    [Theory]
    [MemberData(nameof(ProviderFrames))]
    public void StableClientMetadataPreservesTheOptionsPath(string json)
    {
        using var document = JsonDocument.Parse(json);
        var client = new ExposedGroqClient();
        var expected = Assert.IsType<ChatCompletionChunk>(client.MapUsingCallerOptions(document.RootElement));
        var actual = Assert.IsType<ChatCompletionChunk>(client.Map(document.RootElement));

        Assert.Equal(Encode(expected), Encode(actual));
        Assert.Equal(Tools(expected), Tools(actual));
        Assert.DoesNotContain("x_groq", Encode(actual));
        Assert.DoesNotContain("provider_tool_usage", Encode(actual));
        if (actual.Id == "both-usage")
            Assert.Equal(15, actual.Usage!.TotalTokens);
    }

    [Fact]
    public void PublicOptionsOverloadRetainsCallerConvertersAndMutableOptions()
    {
        using var document = JsonDocument.Parse("""{"id":"original","choices":[]}""");
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ChunkConverter("first:"));

        Assert.Equal("first:original", GroqChunkConverter.Convert(document.RootElement, options)!.Id);
        Assert.False(options.IsReadOnly);
        options.Converters.Clear();
        options.Converters.Add(new ChunkConverter("second:"));

        Assert.Equal("second:original", GroqChunkConverter.Convert(document.RootElement, options)!.Id);
        var unrelated = new JsonSerializerOptions();
        unrelated.Converters.Add(new ChunkConverter("other:"));
        Assert.Equal("other:original", GroqChunkConverter.Convert(document.RootElement, unrelated)!.Id);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "uppercase")]
    public void PublicOptionsOverloadRetainsCaseSensitivity(bool insensitive, string? expectedId)
    {
        using var document = JsonDocument.Parse("""{"ID":"uppercase","CHOICES":[]}""");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = insensitive };

        Assert.Equal(expectedId, GroqChunkConverter.Convert(document.RootElement, options)!.Id);
    }

    [Fact]
    public void SharedMetadataCreatesIndependentChunksDuringConcurrentUse()
    {
        using var document = JsonDocument.Parse("""
            {"id":"parallel","choices":[{"index":0,"delta":{"content":"original"}}],
             "x_groq":{"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,"code_interpreter":2}}}
            """);
        var client = new ExposedGroqClient();
        var chunks = new ChatCompletionChunk[64];
        Parallel.For(0, chunks.Length, index =>
            chunks[index] = client.Map(document.RootElement)!);

        foreach (var chunk in chunks)
        {
            Assert.Equal("original", Assert.Single(chunk.Choices).Delta.Content);
            Assert.Equal(15, chunk.Usage!.TotalTokens);
            Assert.Equal(2, Assert.Single(chunk.ProviderToolUsage!.Tools).Count);
        }
        for (var index = 1; index < chunks.Length; index++)
        {
            Assert.NotSame(chunks[0], chunks[index]);
            Assert.NotSame(chunks[0].Usage, chunks[index].Usage);
            Assert.NotSame(chunks[0].ProviderToolUsage, chunks[index].ProviderToolUsage);
        }

        chunks[0].Choices[0].Delta.Content = "changed";
        chunks[0].Usage!.TotalTokens = 999;
        chunks[0].ProviderToolUsage!.Tools.Clear();
        Assert.Equal("original", chunks[1].Choices[0].Delta.Content);
        Assert.Equal(15, chunks[1].Usage!.TotalTokens);
        Assert.Equal(2, chunks[1].ProviderToolUsage!.Tools[0].Count);
    }

    [Fact]
    public void MalformedVendorUsageRetainsTheExistingFallback()
    {
        using var document = JsonDocument.Parse("""
            {"id":"fallback","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15},
             "x_groq":{"usage":"unsupported-shape"}}
            """);
        var actual = Assert.IsType<ChatCompletionChunk>(new ExposedGroqClient().Map(document.RootElement));

        Assert.Equal("fallback", actual.Id);
        Assert.Equal(15, actual.Usage!.TotalTokens);
        Assert.Null(actual.ProviderToolUsage);
        Assert.DoesNotContain("x_groq", Encode(actual));
    }

    private static string Encode(ChatCompletionChunk chunk) =>
        JsonSerializer.Serialize(chunk, CoreHttpJsonContext.Default.ChatCompletionChunk);

    private static (string Name, int Count, decimal? Duration)[] Tools(ChatCompletionChunk chunk) =>
        chunk.ProviderToolUsage?.Tools.Select(tool =>
            (tool.ToolName, tool.Count, tool.DurationSeconds)).ToArray() ?? [];

    private sealed class ExposedGroqClient() : GroqClient(
        new Provider { Id = 1, ProviderType = ProviderType.Groq, ProviderName = "fixture" },
        new ProviderKeyCredential { Id = 1, ProviderId = 1, ApiKey = "fixture-key" },
        "fixture-model", NullLogger<GroqClient>.Instance)
    {
        public ChatCompletionChunk? Map(JsonElement frame) => MapStreamingChunk(frame);

        public ChatCompletionChunk? MapUsingCallerOptions(JsonElement frame) =>
            GroqChunkConverter.Convert(frame, DefaultJsonOptions);
    }

    private sealed class ChunkConverter(string prefix) : JsonConverter<ChatCompletionChunk>
    {
        public override ChatCompletionChunk Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return new ChatCompletionChunk { Id = prefix + document.RootElement.GetProperty("id").GetString() };
        }

        public override void Write(Utf8JsonWriter writer, ChatCompletionChunk value, JsonSerializerOptions options) =>
            throw new NotSupportedException("This fixture only deserializes provider frames.");
    }
}
