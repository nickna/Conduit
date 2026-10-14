using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

/// <summary>
/// Minimal OpenAI-compatible and Exa upstream used by the native process gate. Keeping the
/// provider in a separate process proves the real HTTP and SSE transport rather than
/// replacing the production client with an in-memory test double.
/// </summary>
internal static class NativeProviderStub
{
    public const string ApiKey = "native-provider-key";
    public const string ImageBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";
    public static byte[] ImageBytes => Convert.FromBase64String(ImageBase64);
    internal const string AgenticPromptPrefix = "native-agentic-proof:";
    internal const string AgenticQueryPrefix = "native-agentic-query:";
    internal const string AgenticFinalContent = "Native agentic result café 雪 🔎";
    internal const string FunctionSuccessResponse = """
        {"requestId":"native-function-search","resolvedSearchType":"neural","results":[{"title":"Native search 雪 🔎","url":"https://example.test/native-function","publishedDate":"2026-10-11T00:00:00Z","author":"Native fixture","score":0.875,"text":"Unicode text café 雪","highlights":["snow 雪","café"],"highlightScores":[0.875,0.75],"summary":"Native summary","subpages":[{"title":"Nested 雪","url":"https://example.test/native-function/child","text":"Nested content"}],"extras":{"links":["https://example.test/link"],"imageLinks":["https://example.test/image.png"]}}]}
        """;
    private const string AgenticContractError = """
        {"error":{"message":"Native agentic provider contract rejected.","type":"invalid_request_error","code":"native_agentic_contract_error"}}
        """;

    private static int _nonStreamingRequests;
    private static int _streamingRequests;
    private static int _imageRequests;
    private static int _errorRequests;
    private static int _authorizationFailures;
    private static int _activeCancellationRequests;
    private static int _cancellationsObserved;
    private static int _functionRequests;
    private static int _functionErrorRequests;
    private static int _functionAuthorizationFailures;
    private static int _functionChunkedRequests;
    private static int _agenticToolCallRequests;
    private static int _agenticFinalRequests;
    private static int _agenticContractFailures;

    public static async Task RunAsync(Uri baseAddress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsLoopback || baseAddress.Scheme != Uri.UriSchemeHttp)
        {
            throw new InvalidOperationException(
                $"The native provider stub must use a loopback HTTP address, received '{baseAddress}'.");
        }

        using var listener = new TcpListener(IPAddress.Loopback, baseAddress.Port);
        listener.Start();
        Console.WriteLine($"Native provider stub listening on {baseAddress.GetLeftPart(UriPartial.Authority)}");

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync(cancellationToken);
            _ = HandleClientAsync(client, cancellationToken).ContinueWith(
                static task => Console.Error.WriteLine(task.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }

    private static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            NativeProviderHttpRequest? request;
            using (var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                requestDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                request = await NativeProviderHttpRequestReader.ReadAsync(stream, requestDeadline.Token);
            }
            if (request is null)
            {
                return;
            }

            if (request.Method == "GET" && request.Path == "/health")
            {
                await WriteJsonAsync(stream, HttpStatusCode.OK, "{\"status\":\"ok\"}");
                return;
            }

            if (request.Method == "GET" && request.Path == "/probe/state")
            {
                var state = $$"""
                    {"non_stream_requests":{{Volatile.Read(ref _nonStreamingRequests)}},"stream_requests":{{Volatile.Read(ref _streamingRequests)}},"image_requests":{{Volatile.Read(ref _imageRequests)}},"error_requests":{{Volatile.Read(ref _errorRequests)}},"authorization_failures":{{Volatile.Read(ref _authorizationFailures)}},"active_cancellation_requests":{{Volatile.Read(ref _activeCancellationRequests)}},"cancellations_observed":{{Volatile.Read(ref _cancellationsObserved)}},"function_requests":{{Volatile.Read(ref _functionRequests)}},"function_error_requests":{{Volatile.Read(ref _functionErrorRequests)}},"function_authorization_failures":{{Volatile.Read(ref _functionAuthorizationFailures)}},"function_chunked_requests":{{Volatile.Read(ref _functionChunkedRequests)}},"agentic_tool_call_requests":{{Volatile.Read(ref _agenticToolCallRequests)}},"agentic_final_requests":{{Volatile.Read(ref _agenticFinalRequests)}},"agentic_contract_failures":{{Volatile.Read(ref _agenticContractFailures)}}}
                    """;
                await WriteJsonAsync(stream, HttpStatusCode.OK, state);
                return;
            }

            if (request.Method == "POST" && request.Path == "/search")
            {
                await WriteFunctionSearchAsync(stream, request);
                return;
            }

            var isChatCompletion = request.Method == "POST" && request.Path == "/chat/completions";
            var isImageGeneration = request.Method == "POST" && request.Path == "/images/generations";
            if (!isChatCompletion && !isImageGeneration)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.NotFound,
                    "{\"error\":{\"message\":\"native provider route not found\",\"type\":\"not_found\"}}");
                return;
            }

            if (!request.Headers.TryGetValue("Authorization", out var authorization) ||
                authorization != $"Bearer {ApiKey}")
            {
                Interlocked.Increment(ref _authorizationFailures);
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.Unauthorized,
                    "{\"error\":{\"message\":\"native provider credential rejected\",\"type\":\"authentication_error\"}}");
                return;
            }

            if (isImageGeneration)
            {
                Interlocked.Increment(ref _imageRequests);
                await WriteJsonAsync(stream, HttpStatusCode.OK, $$"""
                    {"created":1787800003,"data":[{"b64_json":"{{ImageBase64}}","revised_prompt":"native media persistence"}]}
                    """);
                return;
            }

            if (TryCreateAgenticResponse(request.Body, out var agentic))
            {
                if (agentic.Phase == AgenticPhase.ToolCall)
                    Interlocked.Increment(ref _agenticToolCallRequests);
                else if (agentic.Phase == AgenticPhase.Final)
                    Interlocked.Increment(ref _agenticFinalRequests);
                else
                    Interlocked.Increment(ref _agenticContractFailures);
                await WriteJsonAsync(stream, agentic.Status, agentic.Body);
                return;
            }

            if (request.Body.Contains("native-error", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _errorRequests);
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    "{\"error\":{\"message\":\"native upstream private diagnostic\",\"type\":\"invalid_request_error\",\"code\":\"native_bad_prompt\"}}");
                return;
            }

            if (request.Body.Contains("native-cancel", StringComparison.Ordinal))
            {
                await WriteCancellationStreamAsync(stream);
                return;
            }

            if (request.Body.Contains("\"stream\":true", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _streamingRequests);
                await WriteStreamingResponseAsync(stream);
                return;
            }

            Interlocked.Increment(ref _nonStreamingRequests);
            await WriteJsonAsync(stream, HttpStatusCode.OK, """
                {"id":"chatcmpl-native-nonstream","object":"chat.completion","created":1787800000,"model":"native-aot-provider-model","choices":[{"index":0,"message":{"role":"assistant","content":"native provider response"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
                """);
        }
    }

    private static async Task WriteFunctionSearchAsync(NetworkStream stream, NativeProviderHttpRequest request)
    {
        if (request.Headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding == "chunked")
            Interlocked.Increment(ref _functionChunkedRequests);
        if (!request.Headers.TryGetValue("x-api-key", out var apiKey) || apiKey != NativeFunctionFixture.ApiKey)
        {
            Interlocked.Increment(ref _functionAuthorizationFailures);
            await WriteJsonAsync(stream, HttpStatusCode.Unauthorized,
                """{"error":{"message":"native function credential rejected","type":"authentication_error"}}""");
            return;
        }

        string? query = null;
        try
        {
            using var body = JsonDocument.Parse(request.Body);
            if (body.RootElement.ValueKind == JsonValueKind.Object &&
                body.RootElement.TryGetProperty("query", out var value) && value.ValueKind == JsonValueKind.String)
                query = value.GetString();
        }
        catch (JsonException)
        {
            // Malformed JSON is a counted, controlled rejection. Its private
            // body is never included in diagnostics.
            Interlocked.Increment(ref _functionErrorRequests);
            await WriteJsonAsync(stream, HttpStatusCode.BadRequest,
                """{"error":{"message":"native function query is required","type":"invalid_request_error"}}""");
            return;
        }
        if (string.IsNullOrWhiteSpace(query))
        {
            Interlocked.Increment(ref _functionErrorRequests);
            await WriteJsonAsync(stream, HttpStatusCode.BadRequest,
                """{"error":{"message":"native function query is required","type":"invalid_request_error"}}""");
            return;
        }

        if (query.Contains("native-function-error", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _functionErrorRequests);
            await WriteJsonAsync(stream, HttpStatusCode.BadRequest,
                """{"error":{"message":"native function upstream private diagnostic","type":"invalid_request_error","code":"native_bad_query"}}""");
            return;
        }

        Interlocked.Increment(ref _functionRequests);
        // Deliberately omit costDollars so execution must use configured pricing.
        await WriteJsonAsync(stream, HttpStatusCode.OK, FunctionSuccessResponse);
    }

    private static bool TryCreateAgenticResponse(string body, out AgenticResponse response)
    {
        response = new AgenticResponse(AgenticPhase.Rejected, HttpStatusCode.BadRequest, AgenticContractError);
        JsonDocument document;
        try { document = JsonDocument.Parse(body); }
        catch (JsonException) { return body.Contains(AgenticPromptPrefix, StringComparison.Ordinal); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
                return body.Contains(AgenticPromptPrefix, StringComparison.Ordinal);
            var list = messages.EnumerateArray().ToArray();
            var markerIndex = -1;
            string? marker = null;
            for (var index = 0; index < list.Length; index++)
            {
                if (!StringEquals(list[index], "role", "user") ||
                    !TryString(list[index], "content", out var content) ||
                    !content.StartsWith(AgenticPromptPrefix, StringComparison.Ordinal))
                    continue;
                if (markerIndex >= 0) return true;
                markerIndex = index;
                marker = content[AgenticPromptPrefix.Length..];
            }
            if (markerIndex < 0) return body.Contains(AgenticPromptPrefix, StringComparison.Ordinal);
            if (!Guid.TryParseExact(marker, "N", out var markerId) || markerId == Guid.Empty ||
                marker != markerId.ToString("N") || !TryString(root, "model", out var model) ||
                string.IsNullOrWhiteSpace(model) ||
                root.TryGetProperty("stream", out var stream) && stream.ValueKind != JsonValueKind.False ||
                root.TryGetProperty("x_groq", out _) || root.TryGetProperty("provider_tool_usage", out _) ||
                !TryAdvertisedFunction(root, out var functionName))
                return true;
            for (var index = 0; index < markerIndex; index++)
                if (!StringEquals(list[index], "role", "system") ||
                    !TryString(list[index], "content", out _))
                    return true;

            var callId = "call_native_" + marker;
            var query = AgenticQueryPrefix + marker + " café 雪 🔎";
            var arguments = WriteJson(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("query", query);
                writer.WriteEndObject();
            });
            if (list.Length == markerIndex + 1)
            {
                response = new AgenticResponse(AgenticPhase.ToolCall, HttpStatusCode.OK,
                    WriteAgenticResponse(model, marker!, functionName, callId, arguments, final: false));
                return true;
            }
            if (list.Length != markerIndex + 3 ||
                !ValidAssistantCall(list[markerIndex + 1], functionName, callId, arguments) ||
                !ValidToolResult(list[markerIndex + 2], callId))
                return true;
            response = new AgenticResponse(AgenticPhase.Final, HttpStatusCode.OK,
                WriteAgenticResponse(model, marker!, functionName, callId, arguments, final: true));
            return true;
        }
    }

    private static bool TryAdvertisedFunction(JsonElement root, out string name)
    {
        name = string.Empty;
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array ||
            tools.GetArrayLength() != 1)
            return false;
        var tool = tools[0];
        if (!PropertiesAre(tool, "type", "function") || !StringEquals(tool, "type", "function") ||
            !tool.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object ||
            !TryString(function, "name", out name) || name.Length is < 1 or > 64 ||
            !char.IsAsciiLetter(name[0]) || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_') ||
            !function.TryGetProperty("parameters", out var parameters) ||
            !PropertiesAre(parameters, "type", "properties", "required") ||
            !StringEquals(parameters, "type", "object") ||
            !parameters.TryGetProperty("properties", out var properties) || !PropertiesAre(properties, "query") ||
            !properties.TryGetProperty("query", out var query) || !PropertiesAre(query, "type") ||
            !StringEquals(query, "type", "string") ||
            !parameters.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array ||
            required.GetArrayLength() != 1 || required[0].ValueKind != JsonValueKind.String || required[0].GetString() != "query")
            return false;
        return function.EnumerateObject().All(property => property.Name is "name" or "parameters" ||
            property.Name == "description" &&
            (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null));
    }

    private static bool ValidAssistantCall(JsonElement assistant, string name, string id, string arguments)
    {
        if (!StringEquals(assistant, "role", "assistant") ||
            assistant.TryGetProperty("content", out var content) &&
            content.ValueKind != JsonValueKind.Null &&
            (content.ValueKind != JsonValueKind.String || content.GetString() != string.Empty) ||
            !assistant.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array ||
            calls.GetArrayLength() != 1)
            return false;
        var call = calls[0];
        return PropertiesAre(call, "id", "type", "function") && StringEquals(call, "id", id) &&
            StringEquals(call, "type", "function") && call.TryGetProperty("function", out var function) &&
            PropertiesAre(function, "name", "arguments") && StringEquals(function, "name", name) &&
            TryString(function, "arguments", out var actualArguments) && JsonEquals(actualArguments, arguments);
    }

    private static bool ValidToolResult(JsonElement tool, string id) =>
        StringEquals(tool, "role", "tool") && StringEquals(tool, "tool_call_id", id) &&
        TryString(tool, "content", out var content) && JsonEquals(content, FunctionSuccessResponse);

    private static bool JsonEquals(string actual, string expected)
    {
        try
        {
            using var left = JsonDocument.Parse(actual);
            using var right = JsonDocument.Parse(expected);
            return JsonElement.DeepEquals(left.RootElement, right.RootElement);
        }
        catch (JsonException) { return false; }
    }

    private static bool TryString(JsonElement value, string propertyName, out string text)
    {
        text = string.Empty;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
            return false;
        text = property.GetString()!;
        return true;
    }

    private static bool StringEquals(JsonElement value, string name, string expected) =>
        TryString(value, name, out var actual) && actual == expected;

    private static bool PropertiesAre(JsonElement value, params string[] names) =>
        value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == names.Length &&
        names.All(name => value.TryGetProperty(name, out _));

    private static string WriteAgenticResponse(string model, string marker, string functionName,
        string callId, string arguments, bool final) => WriteJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("id", "chatcmpl-native-agentic-" + marker + (final ? "-final" : "-tool"));
        writer.WriteString("object", "chat.completion");
        writer.WriteNumber("created", 1787800010);
        writer.WriteString("model", model);
        writer.WriteStartArray("choices");
        writer.WriteStartObject();
        writer.WriteNumber("index", 0);
        writer.WriteStartObject("message");
        writer.WriteString("role", "assistant");
        if (final)
            writer.WriteString("content", AgenticFinalContent);
        else
        {
            writer.WriteNull("content");
            writer.WriteStartArray("tool_calls");
            writer.WriteStartObject();
            writer.WriteString("id", callId);
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", functionName);
            writer.WriteString("arguments", arguments);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        writer.WriteString("finish_reason", final ? "stop" : "tool_calls");
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteStartObject("usage");
        writer.WriteNumber("prompt_tokens", 10);
        writer.WriteNumber("completion_tokens", 5);
        writer.WriteNumber("total_tokens", 15);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private enum AgenticPhase { Rejected, ToolCall, Final }
    private sealed record AgenticResponse(AgenticPhase Phase, HttpStatusCode Status, string Body);

    private static async Task WriteStreamingResponseAsync(NetworkStream stream)
    {
        await WriteSseHeadersAsync(stream);
        await WriteSseDataAsync(stream, """
            {"id":"chatcmpl-native-stream","object":"chat.completion.chunk","created":1787800001,"model":"native-aot-provider-model","choices":[{"index":0,"delta":{"role":"assistant","content":"native "},"finish_reason":null}]}
            """);
        await Task.Delay(25);
        await WriteSseDataAsync(stream, """
            {"id":"chatcmpl-native-stream","object":"chat.completion.chunk","created":1787800001,"model":"native-aot-provider-model","choices":[{"index":0,"delta":{"content":"stream response"},"finish_reason":"stop"}]}
            """);
        await WriteSseDataAsync(stream, """
            {"id":"chatcmpl-native-stream","object":"chat.completion.chunk","created":1787800001,"model":"native-aot-provider-model","choices":[],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
            """);
        await WriteSseAsync(stream, "data: [DONE]\n\n");
    }

    private static async Task WriteCancellationStreamAsync(NetworkStream stream)
    {
        Interlocked.Increment(ref _activeCancellationRequests);
        try
        {
            await WriteSseHeadersAsync(stream);
            await WriteSseDataAsync(stream, """
                {"id":"chatcmpl-native-cancel","object":"chat.completion.chunk","created":1787800002,"model":"native-aot-provider-model","choices":[{"index":0,"delta":{"role":"assistant","content":"partial"},"finish_reason":null}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
                """);

            while (true)
            {
                await Task.Delay(100);
                await WriteSseAsync(stream, ": native-provider-keepalive\n\n");
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            Interlocked.Increment(ref _cancellationsObserved);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCancellationRequests);
        }
    }

    private static async Task WriteSseHeadersAsync(NetworkStream stream)
    {
        var headers = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(headers);
        await stream.FlushAsync();
    }

    private static async Task WriteSseAsync(NetworkStream stream, string value)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(value));
        await stream.FlushAsync();
    }

    private static Task WriteSseDataAsync(NetworkStream stream, string json) =>
        WriteSseAsync(stream, $"data: {json}\n\n");

    private static async Task WriteJsonAsync(
        NetworkStream stream,
        HttpStatusCode statusCode,
        string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var reason = statusCode switch
        {
            HttpStatusCode.OK => "OK",
            HttpStatusCode.BadRequest => "Bad Request",
            HttpStatusCode.Unauthorized => "Unauthorized",
            HttpStatusCode.NotFound => "Not Found",
            _ => statusCode.ToString()
        };
        var headers = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)statusCode} {reason}\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(headers);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

}
