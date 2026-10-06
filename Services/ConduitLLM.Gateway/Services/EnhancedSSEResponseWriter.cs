using System.Text;
using System.Text.Json;
using ConduitLLM.Gateway.Serialization;

namespace ConduitLLM.Gateway.Services
{
    public sealed record SseErrorEvent(
        string Error,
        ConduitLLM.Core.Interfaces.ProviderErrorDetail? ProviderError = null);

    public sealed record SseReasoningEvent(string Content);

    /// <summary>
    /// Enhanced Server-Sent Events writer that supports multiple event types for streaming responses.
    /// </summary>
    public class EnhancedSSEResponseWriter
    {
        private readonly HttpResponse _response;
        private readonly JsonSerializerOptions _jsonOptions;
        private bool _headersWritten;
        private int _doneWritten;
        private long _eventsWritten;
        private long _bytesWritten;
        private DateTimeOffset? _firstClientFlushAt;

        public bool HasStarted => _headersWritten || _response.HasStarted;

        public long EventsWritten => Interlocked.Read(ref _eventsWritten);

        public long BytesWritten => Interlocked.Read(ref _bytesWritten);

        public DateTimeOffset? FirstClientFlushAt => _firstClientFlushAt;

        public EnhancedSSEResponseWriter(HttpResponse response, JsonSerializerOptions jsonOptions)
        {
            _response = response ?? throw new ArgumentNullException(nameof(response));
            _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));
        }

        /// <summary>
        /// Writes SSE headers if not already written.
        /// </summary>
        private async Task EnsureHeadersWrittenAsync(CancellationToken cancellationToken)
        {
            if (!_headersWritten)
            {
                _response.ContentType = "text/event-stream";
                _response.Headers["Cache-Control"] = "no-cache, no-transform";
                _response.Headers.Append("X-Accel-Buffering", "no"); // Disable Nginx buffering

                _headersWritten = true;
                await _response.Body.FlushAsync(cancellationToken);
            }
        }

        /// <summary>
        /// Writes a content event containing a chat completion chunk.
        /// For OpenAI compatibility, this writes just "data:" without event type.
        /// </summary>
        public async Task WriteContentEventAsync<T>(T data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            await EnsureHeadersWrittenAsync(cancellationToken);
            
            // OpenAI format uses just "data:" without event type
            var json = JsonSerializer.Serialize(data, GatewayJsonTypeInfo.Require<T>(_jsonOptions));
            var eventData = $"data: {json}\n\n";
            var bytes = Encoding.UTF8.GetBytes(eventData);
            await _response.Body.WriteAsync(bytes, cancellationToken);
            await _response.Body.FlushAsync(cancellationToken);
            RecordSuccessfulEvent(bytes.Length);
        }

        /// <summary>
        /// Writes a metrics event containing streaming metrics.
        /// </summary>
        public async Task WriteMetricsEventAsync<T>(T metrics, CancellationToken cancellationToken = default)
        {
            await WriteEventAsync("metrics", metrics, cancellationToken);
        }

        /// <summary>
        /// Writes a final metrics event containing complete performance metrics.
        /// </summary>
        public async Task WriteFinalMetricsEventAsync<T>(T metrics, CancellationToken cancellationToken = default)
        {
            await WriteEventAsync("metrics-final", metrics, cancellationToken);
        }

        /// <summary>
        /// Writes an error event.
        /// </summary>
        public async Task WriteErrorEventAsync(string error, CancellationToken cancellationToken = default)
        {
            await WriteEventAsync("error", new SseErrorEvent(error), cancellationToken);
        }

        /// <summary>
        /// Writes an error event with optional structured provider detail (Internal
        /// customer mode). The <c>error</c> key keeps its string shape so existing
        /// consumers are unaffected; <c>provider_error</c> appears only when detail
        /// is present.
        /// </summary>
        public async Task WriteErrorEventAsync(
            string error,
            ConduitLLM.Core.Interfaces.ProviderErrorDetail? providerError,
            CancellationToken cancellationToken = default)
        {
            if (providerError is null)
            {
                await WriteErrorEventAsync(error, cancellationToken);
                return;
            }

            await WriteEventAsync(
                "error",
                new SseErrorEvent(error, providerError),
                cancellationToken);
        }

        /// <summary>
        /// Writes a reasoning event containing model thinking/reasoning content.
        /// Sent as "event: reasoning" for separate display from main content.
        /// </summary>
        public async Task WriteReasoningEventAsync(string reasoning, CancellationToken cancellationToken = default)
        {
            await WriteEventAsync("reasoning", new SseReasoningEvent(reasoning), cancellationToken);
        }

        /// <summary>
        /// Writes a tool execution status event to provide real-time feedback during function calling.
        /// Sent as "event: tool-executing" with status and progress information.
        /// </summary>
        public async Task WriteToolExecutingEventAsync<T>(T statusData, CancellationToken cancellationToken = default)
        {
            await WriteEventAsync("tool-executing", statusData, cancellationToken);
        }

        /// <summary>
        /// Writes a tool result event for individual tool execution outcomes (optional, for detailed logging).
        /// Sent as "event: tool-result" with tool call ID and result data.
        /// </summary>
        public async Task WriteToolResultEventAsync<T>(T resultData, CancellationToken cancellationToken = default)
        {
            await WriteEventAsync("tool-result", resultData, cancellationToken);
        }

        /// <summary>
        /// Writes a generic SSE event with specified event type and data.
        /// </summary>
        public async Task WriteEventAsync<T>(string eventType, T data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            await EnsureHeadersWrittenAsync(cancellationToken);
            
            var json = JsonSerializer.Serialize(data, GatewayJsonTypeInfo.Require<T>(_jsonOptions));
            var eventData = new StringBuilder();
            
            // Add event type if specified
            if (!string.IsNullOrEmpty(eventType))
            {
                eventData.AppendLine($"event: {eventType}");
            }
            
            // Add data field
            eventData.AppendLine($"data: {json}");
            
            // Add empty line to signal end of event
            eventData.AppendLine();
            
            var bytes = Encoding.UTF8.GetBytes(eventData.ToString());
            await _response.Body.WriteAsync(bytes, cancellationToken);
            await _response.Body.FlushAsync(cancellationToken);
            RecordSuccessfulEvent(bytes.Length);
        }

        /// <summary>
        /// Writes the done event to signal stream completion.
        /// </summary>
        public async Task WriteDoneEventAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Interlocked.Exchange(ref _doneWritten, 1) != 0)
            {
                return;
            }

            await EnsureHeadersWrittenAsync(cancellationToken);
            
            // OpenAI format requires just "data: [DONE]" without event type
            var doneData = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
            await _response.Body.WriteAsync(doneData, cancellationToken);
            await _response.Body.FlushAsync(cancellationToken);
            RecordSuccessfulEvent(doneData.Length);
        }

        /// <summary>
        /// Writes a keep-alive comment to maintain the connection.
        /// </summary>
        public async Task WriteKeepAliveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            await EnsureHeadersWrittenAsync(cancellationToken);
            
            var keepAlive = Encoding.UTF8.GetBytes(": keep-alive\n\n");
            await _response.Body.WriteAsync(keepAlive, cancellationToken);
            await _response.Body.FlushAsync(cancellationToken);
            RecordSuccessfulEvent(keepAlive.Length);
        }

        private void RecordSuccessfulEvent(int byteCount)
        {
            Interlocked.Increment(ref _eventsWritten);
            Interlocked.Add(ref _bytesWritten, byteCount);
            _firstClientFlushAt ??= DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Extension methods for enhanced SSE support.
    /// </summary>
    public static class EnhancedSSEExtensions
    {
        /// <summary>
        /// Creates an enhanced SSE response writer for the HTTP response.
        /// </summary>
        public static EnhancedSSEResponseWriter CreateEnhancedSSEWriter(
            this HttpResponse response, 
            JsonSerializerOptions jsonOptions)
        {
            return new EnhancedSSEResponseWriter(response, jsonOptions);
        }
    }
}
