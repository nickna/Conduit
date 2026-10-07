using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// Minimal OpenAI-compatible upstream used by the native process gate. Keeping the
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

    public static async Task RunAsync(Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsLoopback || baseAddress.Scheme != Uri.UriSchemeHttp)
        {
            throw new InvalidOperationException(
                $"The native provider stub must use a loopback HTTP address, received '{baseAddress}'.");
        }

        var listener = new TcpListener(IPAddress.Loopback, baseAddress.Port);
        listener.Start();
        Console.WriteLine($"Native provider stub listening on {baseAddress.GetLeftPart(UriPartial.Authority)}");

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = HandleClientAsync(client).ContinueWith(
                static task => Console.Error.WriteLine(task.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }

    private static async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            var request = await ReadRequestAsync(stream);
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
                    {"non_stream_requests":{{Volatile.Read(ref _nonStreamingRequests)}},"stream_requests":{{Volatile.Read(ref _streamingRequests)}},"image_requests":{{Volatile.Read(ref _imageRequests)}},"error_requests":{{Volatile.Read(ref _errorRequests)}},"authorization_failures":{{Volatile.Read(ref _authorizationFailures)}},"active_cancellation_requests":{{Volatile.Read(ref _activeCancellationRequests)}},"cancellations_observed":{{Volatile.Read(ref _cancellationsObserved)}}}
                    """;
                await WriteJsonAsync(stream, HttpStatusCode.OK, state);
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

    private static async Task<ProviderHttpRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var headerBytes = new List<byte>(1024);
        var singleByte = new byte[1];
        while (headerBytes.Count < 64 * 1024)
        {
            var bytesRead = await stream.ReadAsync(singleByte);
            if (bytesRead == 0)
            {
                return null;
            }

            headerBytes.Add(singleByte[0]);
            var count = headerBytes.Count;
            if (count >= 4 &&
                headerBytes[count - 4] == '\r' &&
                headerBytes[count - 3] == '\n' &&
                headerBytes[count - 2] == '\r' &&
                headerBytes[count - 1] == '\n')
            {
                break;
            }
        }

        if (headerBytes.Count >= 64 * 1024)
        {
            throw new InvalidOperationException("Native provider request headers exceeded 64 KiB.");
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines[0].Split(' ', 3);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator > 0)
            {
                headers[line[..separator]] = line[(separator + 1)..].Trim();
            }
        }

        var contentLength = headers.TryGetValue("Content-Length", out var lengthValue)
            ? int.Parse(lengthValue, System.Globalization.CultureInfo.InvariantCulture)
            : 0;
        if (contentLength > 1024 * 1024)
        {
            throw new InvalidOperationException("Native provider request body exceeded 1 MiB.");
        }

        var body = new byte[contentLength];
        await stream.ReadExactlyAsync(body);
        return new ProviderHttpRequest(
            requestLine[0],
            requestLine[1].Split('?', 2)[0],
            headers,
            Encoding.UTF8.GetString(body));
    }

    private sealed record ProviderHttpRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        string Body);
}
