using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

/// <summary>
/// Small JSON-protocol SignalR client used by the published NativeAOT process gate.
/// It deliberately speaks negotiate, WebSocket, handshake, invocation, completion,
/// and close frames directly so the probe itself has no reflection-oriented client
/// dependency or protocol fallback.
/// </summary>
internal sealed class NativeSignalRClient : IAsyncDisposable
{
    private const byte RecordSeparator = 0x1e;

    private readonly ClientWebSocket _socket;
    private readonly List<byte> _receiveBuffer = [];
    private readonly Queue<string> _pendingRecords = [];
    private int _nextInvocationId;

    private NativeSignalRClient(ClientWebSocket socket)
    {
        _socket = socket;
    }

    public static async Task<NativeSignalRClient> ConnectAsync(
        Uri gateway,
        string hubPath,
        string? bearerToken,
        CancellationToken cancellationToken = default)
    {
        var connectionToken = await NegotiateAsync(
            gateway,
            hubPath,
            bearerToken,
            cancellationToken);
        var socket = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {bearerToken}");
        }

        try
        {
            await socket.ConnectAsync(
                BuildWebSocketUri(gateway, hubPath, connectionToken),
                cancellationToken);
            var client = new NativeSignalRClient(socket);
            await client.SendRecordAsync(
                """{"protocol":"json","version":1}""",
                cancellationToken);
            var handshake = await client.ReadRecordAsync(cancellationToken);
            using var document = JsonDocument.Parse(handshake);
            if (TryGetProperty(document.RootElement, "error", out var error))
            {
                throw new InvalidOperationException(
                    $"SignalR JSON handshake failed for {hubPath}: {error.GetString()}");
            }

            return client;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task InvokeAsync(
        string target,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsJson);
        var invocationId = Interlocked.Increment(ref _nextInvocationId)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        await SendRecordAsync(
            $$"""{"type":1,"invocationId":"{{invocationId}}","target":"{{target}}","arguments":{{argumentsJson}}}""",
            cancellationToken);

        while (true)
        {
            var record = await ReadRecordAsync(cancellationToken);
            using var document = JsonDocument.Parse(record);
            var root = document.RootElement;
            if (GetMessageType(root) == 7)
            {
                throw new InvalidOperationException(
                    $"SignalR server closed while invoking {target}: {record}");
            }

            if (GetMessageType(root) == 3 &&
                TryGetProperty(root, "invocationId", out var completedId) &&
                string.Equals(completedId.GetString(), invocationId, StringComparison.Ordinal))
            {
                if (TryGetProperty(root, "error", out var error))
                {
                    throw new InvalidOperationException(
                        $"SignalR invocation {target} failed: {error.GetString()}");
                }

                return;
            }

            _pendingRecords.Enqueue(record);
        }
    }

    public async Task<string> WaitForTargetAsync(
        string target,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var pendingCount = _pendingRecords.Count;
        for (var index = 0; index < pendingCount; index++)
        {
            var pending = _pendingRecords.Dequeue();
            if (HasTarget(pending, target))
            {
                return pending;
            }

            _pendingRecords.Enqueue(pending);
        }

        while (true)
        {
            var record = await ReadRecordAsync(cancellationToken);
            if (HasTarget(record, target))
            {
                return record;
            }

            using var document = JsonDocument.Parse(record);
            if (GetMessageType(document.RootElement) == 7)
            {
                throw new InvalidOperationException(
                    $"SignalR server closed before target {target} was received: {record}");
            }

            _pendingRecords.Enqueue(record);
        }
    }

    public async Task<string> WaitForServerCloseAsync(
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var record = await ReadRecordOrCloseAsync(cancellationToken);
            if (record is null)
            {
                return "websocket-close";
            }

            using var document = JsonDocument.Parse(record);
            if (GetMessageType(document.RootElement) == 7)
            {
                return record;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "native parity probe complete",
                    timeout.Token);
            }
        }
        catch (Exception exception) when (
            exception is WebSocketException or OperationCanceledException)
        {
            // Disposing the socket is sufficient when the peer already closed or the
            // process gate is intentionally testing a rejected connection.
        }
        finally
        {
            _socket.Dispose();
        }
    }

    private async Task SendRecordAsync(string json, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(json + (char)RecordSeparator);
        await _socket.SendAsync(
            payload,
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }

    private async Task<string> ReadRecordAsync(CancellationToken cancellationToken) =>
        await ReadRecordOrCloseAsync(cancellationToken)
        ?? throw new InvalidOperationException("SignalR WebSocket closed before the expected record arrived.");

    private async Task<string?> ReadRecordOrCloseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var separator = _receiveBuffer.IndexOf(RecordSeparator);
            if (separator >= 0)
            {
                var record = Encoding.UTF8.GetString(
                    _receiveBuffer.GetRange(0, separator).ToArray());
                _receiveBuffer.RemoveRange(0, separator + 1);
                return record;
            }

            var buffer = new byte[4096];
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidOperationException(
                    $"SignalR JSON client received unexpected {result.MessageType} data.");
            }

            _receiveBuffer.AddRange(buffer.AsSpan(0, result.Count).ToArray());
        }
    }

    private static async Task<string> NegotiateAsync(
        Uri gateway,
        string hubPath,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = gateway };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{hubPath.TrimStart('/')}/negotiate?negotiateVersion=1");
        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"SignalR negotiate for {hubPath} returned {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        return TryGetProperty(document.RootElement, "connectionToken", out var token)
            ? token.GetString()
              ?? throw new InvalidOperationException(
                  $"SignalR negotiate for {hubPath} returned a null connection token.")
            : throw new InvalidOperationException(
                $"SignalR negotiate for {hubPath} omitted connectionToken: {body}");
    }

    private static Uri BuildWebSocketUri(Uri gateway, string hubPath, string connectionToken)
    {
        var builder = new UriBuilder(new Uri(gateway, hubPath.TrimStart('/')))
        {
            Scheme = gateway.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Query = $"id={Uri.EscapeDataString(connectionToken)}"
        };
        return builder.Uri;
    }

    private static bool HasTarget(string record, string target)
    {
        using var document = JsonDocument.Parse(record);
        return GetMessageType(document.RootElement) == 1 &&
               TryGetProperty(document.RootElement, "target", out var candidate) &&
               string.Equals(candidate.GetString(), target, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetMessageType(JsonElement element) =>
        TryGetProperty(element, "type", out var type) && type.TryGetInt32(out var value)
            ? value
            : 0;

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
