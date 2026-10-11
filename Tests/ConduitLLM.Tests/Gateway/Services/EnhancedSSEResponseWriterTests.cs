using System.Text;
using System.Text.Json;

using ConduitLLM.Core.Models;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Options;
using ConduitLLM.Gateway.Services;

using Microsoft.AspNetCore.Http;

namespace ConduitLLM.Tests.Http.Services;

public class EnhancedSSEResponseWriterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChatStream_ReservesNamedProgressEventsForEnhancedMode(bool enhancedEvents)
    {
        var context = new DefaultHttpContext();
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        var writer = new EnhancedSSEResponseWriter(
            context.Response, GatewayJsonOptions.Create(), enhancedEvents);

        await writer.WriteMetricsEventAsync(new StreamingMetrics { TokensGenerated = 1 });
        await writer.WriteFinalMetricsEventAsync(new PerformanceMetrics { TotalTokens = 2 });
        await writer.WriteReasoningEventAsync("thinking");
        await writer.WriteToolExecutingEventAsync(new ToolExecutionEvent { Status = "started" });
        await writer.WriteToolResultEventAsync(new ToolExecutionEvent { Status = "completed" });
        await writer.WriteContentEventAsync(new ChatCompletionChunk
        {
            Id = "chatcmpl_test", Created = 1, Model = "test-model", Choices = []
        });
        await writer.WriteDoneEventAsync();

        var stream = Encoding.UTF8.GetString(responseBody.ToArray());
        Assert.Contains("\"object\":\"chat.completion.chunk\"", stream);
        Assert.EndsWith("data: [DONE]\n\n", stream);
        foreach (var eventName in new[] { "metrics", "metrics-final", "reasoning", "tool-executing", "tool-result" })
        {
            if (enhancedEvents)
                Assert.Contains($"event: {eventName}", stream);
            else
                Assert.DoesNotContain($"event: {eventName}", stream);
        }
        Assert.Equal(enhancedEvents ? 7 : 2, writer.EventsWritten);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamFailure_UsesTheSelectedDialectErrorShape(bool enhancedEvents)
    {
        var context = new DefaultHttpContext();
        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        var writer = new EnhancedSSEResponseWriter(
            context.Response, GatewayJsonOptions.Create(), enhancedEvents);

        await writer.WriteErrorEventAsync("Provider stream failed.");

        var stream = Encoding.UTF8.GetString(responseBody.ToArray());
        var data = stream.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..];
        using var json = JsonDocument.Parse(data);
        var error = json.RootElement.GetProperty("error");
        if (enhancedEvents)
        {
            Assert.Contains("event: error", stream);
            Assert.Equal("Provider stream failed.", error.GetString());
        }
        else
        {
            Assert.DoesNotContain("event:", stream);
            Assert.Equal("Provider stream failed.", error.GetProperty("message").GetString());
            Assert.Equal("server_error", error.GetProperty("type").GetString());
            Assert.Equal("provider_response_error", error.GetProperty("code").GetString());
        }
        Assert.Equal(1, writer.EventsWritten);
    }

    [Fact]
    public async Task WriteDone_IsIdempotentAndUsesStreamingSafeHeaders()
    {
        var context = new DefaultHttpContext();
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        context.Request.Headers.Origin = "https://example.test";
        var writer = new EnhancedSSEResponseWriter(
            context.Response,
            GatewayJsonOptions.Create());

        await writer.WriteDoneEventAsync();
        await writer.WriteDoneEventAsync();

        Assert.True(writer.HasStarted);
        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal("no-cache, no-transform", context.Response.Headers.CacheControl);
        Assert.Equal("no", context.Response.Headers["X-Accel-Buffering"]);
        Assert.False(context.Response.Headers.ContainsKey("Connection"));
        Assert.False(context.Response.Headers.ContainsKey("Access-Control-Allow-Origin"));
        Assert.Equal("data: [DONE]\n\n", Encoding.UTF8.GetString(responseBody.ToArray()));
        Assert.Equal(1, writer.EventsWritten);
        Assert.Equal(responseBody.Length, writer.BytesWritten);
        Assert.NotNull(writer.FirstClientFlushAt);
    }
}
