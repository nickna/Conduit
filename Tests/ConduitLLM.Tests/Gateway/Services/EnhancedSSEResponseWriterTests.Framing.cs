using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using ConduitLLM.Core.Models;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Services;

using Microsoft.AspNetCore.Http;

namespace ConduitLLM.Tests.Http.Services;

public partial class EnhancedSSEResponseWriterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frames_PreserveSerializerPolicyUnicodeAndPlatformNewlines(bool named)
    {
        var options = GatewayJsonOptions.Create();
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.WriteIndented = true;
        var chunk = new ChatCompletionChunk
        {
            Id = "café 雪 <tag>", Created = 1, Model = "test", Choices = []
        };
        var json = JsonSerializer.Serialize(chunk, (JsonTypeInfo<ChatCompletionChunk>)options.GetTypeInfo(typeof(ChatCompletionChunk)));
        var (writer, body) = CreateFramingWriter(options);
        if (named) await writer.WriteEventAsync("résultat", chunk);
        else await writer.WriteContentEventAsync(chunk);

        var newline = named ? Environment.NewLine : "\n";
        var expected = (named ? "event: résultat" + newline : "") + "data: " + json + newline + newline;
        Assert.Equal(Encoding.UTF8.GetBytes(expected), body.ToArray());
        Assert.Equal(body.Length, writer.BytesWritten);
        Assert.Equal(1, writer.EventsWritten);
        Assert.Equal(2, body.Flushes);
    }

    [Fact]
    public async Task EmptyEventType_PreservesDataOnlyNamedDialect()
    {
        var options = GatewayJsonOptions.Create();
        var (writer, body) = CreateFramingWriter(options);
        await writer.WriteEventAsync("", new SseReasoningEvent("雪"));
        var json = JsonSerializer.Serialize(new SseReasoningEvent("雪"), (JsonTypeInfo<SseReasoningEvent>)options.GetTypeInfo(typeof(SseReasoningEvent)));
        Assert.Equal($"data: {json}{Environment.NewLine}{Environment.NewLine}", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SerializationFailure_DoesNotWritePartialFrame(bool named)
    {
        var options = GatewayJsonOptions.Create();
        options.Converters.Insert(0, new FailingChunkConverter());
        var (writer, body) = CreateFramingWriter(options);
        var chunk = new ChatCompletionChunk { Id = "test", Choices = [] };
        await Assert.ThrowsAsync<JsonException>(() => named
            ? writer.WriteEventAsync("test", chunk)
            : writer.WriteContentEventAsync(chunk));
        Assert.Empty(body.ToArray());
        Assert.Equal(1, body.Flushes); // Existing header flush still occurs before serialization.
        Assert.Equal(0, writer.EventsWritten);
        Assert.Equal(0, writer.BytesWritten);
        Assert.Null(writer.FirstClientFlushAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedWriteOrEventFlush_DoesNotRecordSuccessfulEvent(bool failFlush)
    {
        var (writer, body) = CreateFramingWriter(GatewayJsonOptions.Create());
        body.FailWrite = !failFlush;
        body.FailEventFlush = failFlush;
        await Assert.ThrowsAsync<IOException>(() => writer.WriteContentEventAsync(new SseReasoningEvent("test")));
        Assert.Equal(0, writer.EventsWritten);
        Assert.Equal(0, writer.BytesWritten);
        Assert.Null(writer.FirstClientFlushAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCanceledWrite_DoesNotStartResponse(bool named)
    {
        var (writer, body) = CreateFramingWriter(GatewayJsonOptions.Create());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => named
            ? writer.WriteEventAsync("test", new SseReasoningEvent("test"), cancellation.Token)
            : writer.WriteContentEventAsync(new SseReasoningEvent("test"), cancellation.Token));
        Assert.False(writer.HasStarted);
        Assert.Empty(body.ToArray());
        Assert.Equal(0, body.Flushes);
    }

    [Fact]
    public async Task WriteCompletion_WaitsForEventFlush()
    {
        var (writer, body) = CreateFramingWriter(GatewayJsonOptions.Create());
        body.BlockEventFlush = true;
        var pending = writer.WriteContentEventAsync(new SseReasoningEvent("test"));
        await body.EventFlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, writer.EventsWritten);
        body.ReleaseFlush.SetResult();
        await pending;
        Assert.Equal(1, writer.EventsWritten);
    }

    [Fact]
    public async Task OverlappingWrites_KeepFrameMemoryIndependentUntilWriteCompletes()
    {
        var (writer, body) = CreateFramingWriter(GatewayJsonOptions.Create());
        body.BlockFirstWrite = true;
        var first = writer.WriteContentEventAsync(new SseReasoningEvent("first"));
        await body.FirstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await writer.WriteContentEventAsync(new SseReasoningEvent("second"));
        body.ReleaseWrite.SetResult();
        await first;
        Assert.Equal("data: {\"content\":\"second\"}\n\ndata: {\"content\":\"first\"}\n\n", Encoding.UTF8.GetString(body.ToArray()));
        Assert.Equal(2, writer.EventsWritten);
        // This checks memory ownership; concurrent calls have no promised wire ordering.
    }

    private static (EnhancedSSEResponseWriter Writer, FramingStream Body) CreateFramingWriter(JsonSerializerOptions options)
    {
        var context = new DefaultHttpContext();
        var body = new FramingStream();
        context.Response.Body = body;
        return (new EnhancedSSEResponseWriter(context.Response, options), body);
    }

    private sealed class FailingChunkConverter : JsonConverter<ChatCompletionChunk>
    {
        public override ChatCompletionChunk Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, ChatCompletionChunk value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("partial", "must stay private");
            throw new JsonException("Intentional conversion failure.");
        }
    }

    private sealed class FramingStream : MemoryStream
    {
        public int Flushes { get; private set; }
        public bool FailWrite { get; set; }
        public bool FailEventFlush { get; set; }
        public bool BlockEventFlush { get; set; }
        public bool BlockFirstWrite { get; set; }
        public TaskCompletionSource EventFlushEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFlush { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Flushes++;
            if (Flushes == 1) return;
            if (FailEventFlush) throw new IOException("Intentional flush failure.");
            if (BlockEventFlush)
            {
                EventFlushEntered.TrySetResult();
                await ReleaseFlush.Task.WaitAsync(cancellationToken);
            }
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailWrite) throw new IOException("Intentional write failure.");
            if (BlockFirstWrite)
            {
                BlockFirstWrite = false;
                FirstWriteEntered.TrySetResult();
                await ReleaseWrite.Task.WaitAsync(cancellationToken);
            }
            base.Write(buffer.Span);
        }
    }
}
