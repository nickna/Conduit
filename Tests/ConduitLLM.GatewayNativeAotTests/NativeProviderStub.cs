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
                    {"non_stream_requests":{{Volatile.Read(ref _nonStreamingRequests)}},"stream_requests":{{Volatile.Read(ref _streamingRequests)}},"image_requests":{{Volatile.Read(ref _imageRequests)}},"error_requests":{{Volatile.Read(ref _errorRequests)}},"authorization_failures":{{Volatile.Read(ref _authorizationFailures)}},"active_cancellation_requests":{{Volatile.Read(ref _activeCancellationRequests)}},"cancellations_observed":{{Volatile.Read(ref _cancellationsObserved)}},"function_requests":{{Volatile.Read(ref _functionRequests)}},"function_error_requests":{{Volatile.Read(ref _functionErrorRequests)}},"function_authorization_failures":{{Volatile.Read(ref _functionAuthorizationFailures)}},"function_chunked_requests":{{Volatile.Read(ref _functionChunkedRequests)}}}
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
        catch (JsonException) { }
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
        await WriteJsonAsync(stream, HttpStatusCode.OK, """
            {"requestId":"native-function-search","resolvedSearchType":"neural","results":[{"title":"Native search 雪 🔎","url":"https://example.test/native-function","publishedDate":"2026-10-11T00:00:00Z","author":"Native fixture","score":0.875,"text":"Unicode text café 雪","highlights":["snow 雪","café"],"highlightScores":[0.875,0.75],"summary":"Native summary","subpages":[{"title":"Nested 雪","url":"https://example.test/native-function/child","text":"Nested content"}],"extras":{"links":["https://example.test/link"],"imageLinks":["https://example.test/image.png"]}}]}
            """);
    }

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
                {"id":"chatcmpl-native-cancel","object":"chat.completion.chunk","created":1787800002,"model":"native-aot-provider-model","choices":[{"index":0,"delta":{"role":"assistant","content":"partial"},"finish_reason":null}]}
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
