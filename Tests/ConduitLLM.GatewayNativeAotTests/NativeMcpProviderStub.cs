using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

/// <summary>Instance-owned, stateless MCP HTTP upstream for real SDK/native transport proofs.</summary>
internal sealed class NativeMcpProviderStub : IAsyncDisposable
{
    internal const string ApiKey = "native-mcp-fixture-key";
    internal const string ProtocolVersion = "2026-07-28";
    internal const string ToolName = "native_search";
    internal const string OtherToolName = "native_echo";
    internal const string ToolSchema = """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""";
    internal const string ToolText = "Native MCP result café 雪 🔎";
    internal const string StructuredResult = """{"unicode":"café 雪 🔎","items":[null,true,2,{"name":"nested 雪"}]}""";
    private const string VersionMeta = "io.modelcontextprotocol/protocolVersion";
    private const string ClientInfoMeta = "io.modelcontextprotocol/clientInfo";
    private const string ClientCapabilitiesMeta = "io.modelcontextprotocol/clientCapabilities";
    private const string ServerInfoMeta = "io.modelcontextprotocol/serverInfo";
    private const string Image = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=";

    private readonly object _gate = new();
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<long, TcpClient> _clients = new();
    private readonly HashSet<Task> _handlers = new();
    private readonly List<Exception> _faults = new();
    private readonly TaskCompletionSource _listReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _callReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _holdRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly NativeMcpCallMode _mode;
    private readonly string _authHeader;
    private readonly string _authValue;
    private readonly Task _acceptLoop;
    private Task? _disposeTask;
    private bool _disposing;
    private long _nextClient;
    private JsonElement? _lastCallArguments;
    private JsonElement? _lastRequestMetadata;
    private int _discoverRequests;
    private int _listRequests;
    private int _callRequests;
    private int _authorizationFailures;
    private int _protocolFailures;
    private int _chunkedRequests;
    private int _cancellationNotifications;
    private int _heldRequests;

    internal NativeMcpProviderStub(NativeMcpCallMode mode = NativeMcpCallMode.Success,
        string authHeader = "Authorization", string? authValue = null)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (string.IsNullOrWhiteSpace(authHeader) || authHeader.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && !"!#$%&'*+-.^_`|~".Contains(character)))
            throw new ArgumentException("A valid fixture authentication header is required.", nameof(authHeader));
        if (authValue is not null && (string.IsNullOrWhiteSpace(authValue) || authValue.Contains('\r') || authValue.Contains('\n')))
            throw new ArgumentException("A valid fixture authentication value is required.", nameof(authValue));
        _mode = mode;
        _authHeader = authHeader;
        _authValue = authValue ?? "Bearer " + ApiKey;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/mcp");
        _acceptLoop = AcceptAsync(_stop.Token);
    }

    internal Uri Endpoint { get; }
    internal Task ListReceived => _listReceived.Task;
    internal Task CallReceived => _callReceived.Task;
    internal JsonElement? LastCallArguments { get { lock (_gate) return _lastCallArguments?.Clone(); } }
    internal JsonElement? LastRequestMetadata { get { lock (_gate) return _lastRequestMetadata?.Clone(); } }
    internal NativeMcpCounters Counters
    {
        get
        {
            lock (_gate)
            {
                ThrowIfFaultedLocked();
                return new(Volatile.Read(ref _discoverRequests), Volatile.Read(ref _listRequests), Volatile.Read(ref _callRequests),
                    Volatile.Read(ref _authorizationFailures), Volatile.Read(ref _protocolFailures), Volatile.Read(ref _chunkedRequests),
                    Volatile.Read(ref _cancellationNotifications), _clients.Count, Volatile.Read(ref _heldRequests), _nextClient);
            }
        }
    }

    internal void ReleaseHeldRequests() => _holdRelease.TrySetResult();

    internal async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            lock (_gate)
            {
                ThrowIfFaultedLocked();
                if (_clients.Count == 0) return;
            }
            await Task.Delay(10, deadline.Token);
        }
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                lock (_gate)
                {
                    if (_disposing)
                    {
                        client.Dispose();
                        return;
                    }
                    // Completed successful handlers have already drained their owned sockets.
                    _handlers.RemoveWhere(task => task.IsCompletedSuccessfully);
                    var identity = ++_nextClient;
                    _clients.Add(identity, client);
                    _handlers.Add(HandleAsync(identity, client, cancellationToken));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            RecordFault(exception);
            _stop.Cancel();
        }
    }

    private async Task HandleAsync(long identity, TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                NativeProviderHttpRequest? request;
                using (var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    readDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                    request = await NativeProviderHttpRequestReader.ReadAsync(stream, readDeadline.Token);
                }
                if (request is null) return;
                if (request.Headers.TryGetValue("Transfer-Encoding", out var encoding) &&
                    encoding.Equals("chunked", StringComparison.OrdinalIgnoreCase)) Interlocked.Increment(ref _chunkedRequests);
                using var responseDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                responseDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                var token = responseDeadline.Token;
                if (request.Path != "/mcp")
                {
                    Interlocked.Increment(ref _protocolFailures);
                    await WriteAsync(stream, HttpStatusCode.NotFound, "{}", token);
                    return;
                }
                if (!request.Headers.TryGetValue(_authHeader, out var credential) || credential != _authValue)
                {
                    Interlocked.Increment(ref _authorizationFailures);
                    await WriteAsync(stream, HttpStatusCode.Unauthorized, """{"error":"Native MCP credential rejected."}""", token);
                    return;
                }
                if (request.Method != "POST")
                {
                    Interlocked.Increment(ref _protocolFailures);
                    await WriteAsync(stream, HttpStatusCode.MethodNotAllowed, "{}", token);
                    return;
                }
                JsonDocument document;
                try { document = JsonDocument.Parse(request.Body); }
                catch (JsonException)
                {
                    Interlocked.Increment(ref _protocolFailures);
                    await WriteAsync(stream, HttpStatusCode.BadRequest, RpcError(default, -32700, "Invalid fixture JSON."), token);
                    return;
                }
                using (document)
                {
                    var root = document.RootElement;
                    if (!StringEquals(root, "jsonrpc", "2.0") || !TryString(root, "method", out var method))
                    {
                        Interlocked.Increment(ref _protocolFailures);
                        await WriteAsync(stream, HttpStatusCode.BadRequest, RpcError(default, -32600, "Invalid fixture RPC."), token);
                        return;
                    }
                    if (method == "notifications/cancelled" && !root.TryGetProperty("id", out _))
                    {
                        if (!root.TryGetProperty("params", out var cancellation) || cancellation.ValueKind != JsonValueKind.Object ||
                            !cancellation.TryGetProperty("requestId", out var cancelledId) || !ValidId(cancelledId))
                        {
                            Interlocked.Increment(ref _protocolFailures);
                            await WriteAsync(stream, HttpStatusCode.BadRequest, RpcError(default, -32600, "Invalid fixture cancellation."), token);
                            return;
                        }
                        // The SDK sends notifications without the RPC request's per-request metadata.
                        Interlocked.Increment(ref _cancellationNotifications);
                        await WriteAsync(stream, HttpStatusCode.Accepted, string.Empty, token);
                        return;
                    }
                    if (!root.TryGetProperty("id", out var id) || !ValidId(id) ||
                        !root.TryGetProperty("params", out var parameters) || !ValidMetadata(request, parameters, method, out var metadata))
                    {
                        Interlocked.Increment(ref _protocolFailures);
                        await WriteAsync(stream, HttpStatusCode.BadRequest, RpcError(default, -32600, "Invalid fixture RPC metadata."), token);
                        return;
                    }
                    lock (_gate) _lastRequestMetadata = metadata.Clone();
                    switch (method)
                    {
                        case "server/discover":
                            Interlocked.Increment(ref _discoverRequests);
                            await WriteAsync(stream, HttpStatusCode.OK, RpcResult(id, WriteDiscover), token);
                            break;
                        case "tools/list":
                            var listNumber = Interlocked.Increment(ref _listRequests);
                            if (_mode == NativeMcpCallMode.HoldFirstList && listNumber == 1) await HoldAsync(_listReceived, token);
                            else _listReceived.TrySetResult();
                            await WriteAsync(stream, HttpStatusCode.OK, RpcResult(id, WriteTools), token);
                            break;
                        case "tools/call":
                            if (!TryString(parameters, "name", out var name) || name is not (ToolName or OtherToolName) ||
                                !request.Headers.TryGetValue("Mcp-Name", out var headerName) || headerName != name ||
                                !parameters.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object ||
                                !TryString(arguments, "query", out var query) || string.IsNullOrWhiteSpace(query) ||
                                arguments.TryGetProperty("__conduit_mcp_tool", out _))
                            {
                                Interlocked.Increment(ref _protocolFailures);
                                await WriteAsync(stream, HttpStatusCode.OK, RpcError(id, -32602, "Invalid fixture tool arguments."), token);
                                break;
                            }
                            lock (_gate) _lastCallArguments = arguments.Clone();
                            var callNumber = Interlocked.Increment(ref _callRequests);
                            if (_mode == NativeMcpCallMode.HoldFirstCall && callNumber == 1) await HoldAsync(_callReceived, token);
                            else _callReceived.TrySetResult();
                            await WriteAsync(stream, HttpStatusCode.OK, _mode == NativeMcpCallMode.RpcError
                                ? RpcError(id, -32000, "Native MCP RPC failed.") : RpcResult(id, WriteCallResult), token);
                            break;
                        default:
                            Interlocked.Increment(ref _protocolFailures);
                            await WriteAsync(stream, HttpStatusCode.OK, RpcError(id, -32601, "Fixture RPC method not supported."), token);
                            break;
                    }
                }
            }
        }
        catch (InvalidDataException) { Interlocked.Increment(ref _protocolFailures); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { RecordFault(exception); }
        finally
        {
            client.Dispose();
            lock (_gate) _clients.Remove(identity);
        }
    }

    private async Task HoldAsync(TaskCompletionSource received, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _heldRequests);
        received.TrySetResult();
        try { await _holdRelease.Task.WaitAsync(cancellationToken); }
        finally { Interlocked.Decrement(ref _heldRequests); }
    }

    private static bool ValidMetadata(NativeProviderHttpRequest request, JsonElement parameters, string method, out JsonElement metadata)
    {
        metadata = default;
        return request.Headers.TryGetValue("MCP-Protocol-Version", out var version) && version == ProtocolVersion &&
            !request.Headers.ContainsKey("Mcp-Session-Id") &&
            request.Headers.TryGetValue("Mcp-Method", out var headerMethod) && headerMethod == method &&
            parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("_meta", out metadata) &&
            metadata.ValueKind == JsonValueKind.Object && StringEquals(metadata, VersionMeta, ProtocolVersion) &&
            metadata.TryGetProperty(ClientInfoMeta, out var clientInfo) && clientInfo.ValueKind == JsonValueKind.Object &&
            TryString(clientInfo, "name", out var clientName) && !string.IsNullOrWhiteSpace(clientName) &&
            TryString(clientInfo, "version", out var clientVersion) && !string.IsNullOrWhiteSpace(clientVersion) &&
            metadata.TryGetProperty(ClientCapabilitiesMeta, out var capabilities) && capabilities.ValueKind == JsonValueKind.Object;
    }

    private static bool ValidId(JsonElement id) => id.ValueKind == JsonValueKind.String ||
        id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out _);

    private static bool TryString(JsonElement value, string name, out string text)
    {
        text = string.Empty;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        text = property.GetString()!;
        return true;
    }

    private static bool StringEquals(JsonElement value, string name, string expected) => TryString(value, name, out var actual) && actual == expected;

    private static string RpcResult(JsonElement id, Action<Utf8JsonWriter> writeResult) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id"); id.WriteTo(writer);
        writer.WritePropertyName("result"); writeResult(writer);
        writer.WriteEndObject();
    });

    private static string RpcError(JsonElement id, int code, string message) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        if (ValidId(id)) id.WriteTo(writer); else writer.WriteNullValue();
        writer.WriteStartObject("error");
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    private static void WriteDiscover(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("supportedVersions"); writer.WriteStringValue(ProtocolVersion); writer.WriteEndArray();
        writer.WriteStartObject("capabilities"); writer.WriteStartObject("tools"); writer.WriteEndObject(); writer.WriteEndObject();
        WriteCacheHints(writer);
        WriteServerInfo(writer);
        writer.WriteEndObject();
    }

    private static void WriteTools(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("tools");
        WriteTool(writer, ToolName, "Search Unicode content café 雪 🔎");
        WriteTool(writer, OtherToolName, "Echo Unicode content café 雪");
        writer.WriteEndArray();
        WriteCacheHints(writer);
        WriteServerInfo(writer);
        writer.WriteEndObject();
    }

    private static void WriteTool(Utf8JsonWriter writer, string name, string description)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("description", description);
        writer.WritePropertyName("inputSchema");
        using var schema = JsonDocument.Parse(ToolSchema);
        schema.RootElement.WriteTo(writer);
        writer.WriteEndObject();
    }

    private static void WriteCacheHints(Utf8JsonWriter writer)
    {
        writer.WriteNumber("ttlMs", 0);
        writer.WriteString("cacheScope", "private");
    }

    private static void WriteServerInfo(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("_meta");
        writer.WriteStartObject(ServerInfoMeta);
        writer.WriteString("name", "native-mcp-fixture");
        writer.WriteString("version", "1.0.0");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private void WriteCallResult(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("isError", _mode == NativeMcpCallMode.ToolError);
        writer.WriteStartArray("content");
        writer.WriteStartObject();
        writer.WriteString("type", "text");
        writer.WriteString("text", _mode == NativeMcpCallMode.LargeText ? string.Concat(Enumerable.Repeat("😀", 75000)) : ToolText);
        writer.WriteEndObject();
        if (_mode != NativeMcpCallMode.LargeText)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "image");
            writer.WriteString("data", Image);
            writer.WriteString("mimeType", "image/png");
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (_mode != NativeMcpCallMode.LargeText)
        {
            writer.WritePropertyName("structuredContent");
            if (_mode == NativeMcpCallMode.LargeStructured)
            {
                writer.WriteStartObject();
                writer.WriteString("value", new string('x', 150000));
                writer.WriteEndObject();
            }
            else
            {
                using var structured = JsonDocument.Parse(StructuredResult);
                structured.RootElement.WriteTo(writer);
            }
        }
        WriteServerInfo(writer);
        writer.WriteEndObject();
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static async Task WriteAsync(NetworkStream stream, HttpStatusCode status, string body, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(int)status} Fixture\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private void RecordFault(Exception exception) { lock (_gate) _faults.Add(exception); }
    private void ThrowIfFaultedLocked()
    {
        if (_faults.Count > 0) throw new AggregateException("Native MCP fixture failed unexpectedly.", _faults);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_disposeTask ??= DisposeOwnedAsync());
    }

    private async Task DisposeOwnedAsync()
    {
        TcpClient[] clients;
        lock (_gate)
        {
            _disposing = true;
            clients = _clients.Values.ToArray();
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _stop.Cancel();
        _listener.Stop();
        ReleaseHeldRequests();
        foreach (var client in clients) client.Dispose();
        try
        {
            await _acceptLoop.WaitAsync(deadline.Token);
            Task[] handlers;
            lock (_gate) handlers = _handlers.ToArray();
            await Task.WhenAll(handlers).WaitAsync(deadline.Token);
            lock (_gate)
            {
                if (_clients.Count != 0) throw new InvalidOperationException("Native MCP fixture did not drain its owned clients.");
                ThrowIfFaultedLocked();
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Native MCP fixture cleanup exceeded its ten-second deadline.");
        }
        finally { _stop.Dispose(); }
    }
}

internal enum NativeMcpCallMode { Success, ToolError, HoldFirstList, HoldFirstCall, LargeText, LargeStructured, RpcError }
internal sealed record NativeMcpCounters(int DiscoverRequests, int ListRequests, int CallRequests, int AuthorizationFailures,
    int ProtocolFailures, int ChunkedRequests, int CancellationNotifications, int ActiveHandlers, int HeldRequests,
    long AcceptedConnections);
