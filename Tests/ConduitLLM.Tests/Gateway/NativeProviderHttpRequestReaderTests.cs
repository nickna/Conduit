using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using ConduitLLM.Core.Serialization;

namespace ConduitLLM.Tests.Gateway;

public sealed class NativeProviderHttpRequestReaderTests
{
    [Fact]
    public async Task RealHttpClientChunkedJsonContentPreservesTheCompleteGeneratedJsonPayload()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var payload = JsonDocument.Parse(
            """{"model":"native-fixture","messages":[{"role":"user","content":"雪 😀 café"}],"stream":true,"nested":{"enabled":false,"values":[1,null,{"name":"雪"}]},"optional":null}""");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/chat/completions")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = JsonContent.Create(payload.RootElement, CoreHttpJsonContext.Default.JsonElement)
        };
        request.Headers.TransferEncodingChunked = true;
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture-secret");
        Assert.Null(request.Content.Headers.ContentLength);

        async Task<NativeProviderHttpRequest> ReceiveAsync()
        {
            using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = accepted.GetStream();
            var parsed = await NativeProviderHttpRequestReader.ReadAsync(stream, deadline.Token);
            Assert.NotNull(parsed);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), deadline.Token);
            return parsed;
        }

        var receive = ReceiveAsync();
        var send = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        try
        {
            await Task.WhenAll(receive, send);
            using var response = await send;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var parsed = await receive;
            Assert.Equal("POST", parsed.Method);
            Assert.Equal("/chat/completions", parsed.Path);
            Assert.Equal("chunked", parsed.Headers["transfer-encoding"]);
            Assert.False(parsed.Headers.ContainsKey("Content-Length"));
            Assert.Equal("Bearer fixture-secret", parsed.Headers["authorization"]);
            using var actual = JsonDocument.Parse(parsed.Body);
            Assert.True(JsonElement.DeepEquals(payload.RootElement, actual.RootElement));
            Assert.True(actual.RootElement.GetProperty("stream").GetBoolean());
            Assert.Equal("雪 😀 café", actual.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.False(deadline.IsCancellationRequested);
        }
        finally
        {
            listener.Stop();
            http.Dispose();
            await DrainAsync(receive);
            await DrainAsync(send);
        }
    }

    [Theory]
    [InlineData("length")]
    [InlineData("zero")]
    [InlineData("none")]
    public async Task ContentLengthAndBodylessRequestsPreserveExistingMethodPathAndHeaders(string framing)
    {
        const string body = "雪 😀 content-length";
        var expected = framing == "length" ? body : "";
        var headers = framing switch
        {
            "length" => $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n",
            "zero" => "Content-Length: 0\r\n",
            _ => ""
        };
        var parsed = await ReadTextAsync(
            "GET /probe?fixture=1 HTTP/1.1\r\nHost: fixture.test\r\nX-Probe: first\r\nx-probe: last\r\n" + headers + "\r\n" + expected);
        Assert.NotNull(parsed);
        Assert.Equal("GET", parsed.Method);
        Assert.Equal("/probe", parsed.Path);
        Assert.Equal("fixture.test", parsed.Headers["host"]);
        Assert.Equal("last", parsed.Headers["X-PROBE"]);
        Assert.Equal(expected, parsed.Body);
    }

    [Fact]
    public async Task CleanInitialEndOfStreamReturnsNoRequest()
    {
        using var stream = new MemoryStream();
        Assert.Null(await NativeProviderHttpRequestReader.ReadAsync(stream));
    }

    [Fact]
    public async Task ChunkBoundariesMaySplitUtf8AndExtensionsAndTrailersDoNotChangeTheBodyOrHeaders()
    {
        const string body = "{\"message\":\"雪 😀 café\",\"nested\":[true,null,{\"enabled\":false}]}";
        using var stream = new MemoryStream();
        WriteAscii(stream, "POST /chat/completions HTTP/1.1\r\nHost: fixture.test\r\nAuthorization: original\r\nTransfer-Encoding: ChUnKeD\r\n\r\n");
        // One-byte chunks split every multibyte UTF-8 character. Decode only after
        // reassembling the body, rather than treating each chunk as a JSON/string unit.
        foreach (var value in Encoding.UTF8.GetBytes(body))
        {
            WriteAscii(stream, "1; probe=token; note=\"split\\\"quote\"\r\n");
            stream.WriteByte(value);
            WriteAscii(stream, "\r\n");
        }
        WriteAscii(stream, "0; finished=yes\r\nAuthorization: trailer-value\r\nX-Trailer: discarded\r\n\r\n");
        stream.Position = 0;
        var parsed = await NativeProviderHttpRequestReader.ReadAsync(stream);
        Assert.NotNull(parsed);
        Assert.Equal(body, parsed.Body);
        Assert.Equal("original", parsed.Headers["authorization"]);
        Assert.False(parsed.Headers.ContainsKey("X-Trailer"));
        using var actual = JsonDocument.Parse(parsed.Body);
        Assert.Equal("雪 😀 café", actual.RootElement.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, actual.RootElement.GetProperty("nested")[1].ValueKind);
    }

    [Theory]
    [InlineData("duplicate-length-same")]
    [InlineData("duplicate-length-conflict")]
    [InlineData("duplicate-transfer")]
    [InlineData("transfer-and-length")]
    [InlineData("unsupported-coding")]
    [InlineData("coding-list")]
    [InlineData("non-numeric-length")]
    [InlineData("negative-length")]
    [InlineData("comma-length")]
    [InlineData("bad-header")]
    [InlineData("bad-name")]
    [InlineData("bad-chunk-size")]
    [InlineData("bad-data-delimiter")]
    [InlineData("bad-extension")]
    [InlineData("bad-trailer")]
    public async Task InvalidOrConflictingFramingIsRejected(string invalid)
    {
        var text = invalid switch
        {
            "duplicate-length-same" => Request("Content-Length: 1\r\ncontent-length: 1\r\n", "a"),
            "duplicate-length-conflict" => Request("Content-Length: 1\r\ncontent-length: 2\r\n", "ab"),
            "duplicate-transfer" => Request("Transfer-Encoding: chunked\r\ntransfer-encoding: chunked\r\n", "0\r\n\r\n"),
            "transfer-and-length" => Request("Transfer-Encoding: chunked\r\nContent-Length: 0\r\n", "0\r\n\r\n"),
            "unsupported-coding" => Request("Transfer-Encoding: gzip\r\n", ""),
            "coding-list" => Request("Transfer-Encoding: gzip, chunked\r\n", "0\r\n\r\n"),
            "non-numeric-length" => Request("Content-Length: not-a-length\r\n", ""),
            "negative-length" => Request("Content-Length: -1\r\n", ""),
            "comma-length" => Request("Content-Length: 1, 1\r\n", "a"),
            "bad-header" => Request("not-a-header\r\n", ""),
            "bad-name" => Request("Bad Name: value\r\n", ""),
            "bad-chunk-size" => Chunked("not-hex\r\n"),
            "bad-data-delimiter" => Chunked("1\r\naXX0\r\n\r\n"),
            "bad-extension" => Chunked("1; probe=\"unterminated\r\na\r\n0\r\n\r\n"),
            "bad-trailer" => Chunked("0\r\nnot-a-header\r\n\r\n"),
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadTextAsync(text));
    }

    [Theory]
    [InlineData("P")]
    [InlineData("POST /chat/completions HTTP/1.1\r\nHost: fixture.test\r\n")]
    [InlineData("POST /chat/completions HTTP/1.1\r\nContent-Length: 3\r\n\r\nab")]
    [InlineData("POST /chat/completions HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n1")]
    [InlineData("POST /chat/completions HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n2\r\na")]
    [InlineData("POST /chat/completions HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n1\r\na\r")]
    [InlineData("POST /chat/completions HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\nX-Trailer: value\r\n")]
    public async Task IncompleteRequestsNeverReturnAPartialBody(string text)
    {
        await Assert.ThrowsAsync<EndOfStreamException>(() => ReadTextAsync(text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactlyOneMebibyteOfBodyIsAcceptedForBothFramingModes(bool chunked)
    {
        var body = new string('x', NativeProviderHttpRequestReader.MaxBodyBytes);
        var text = chunked
            ? Chunked($"{NativeProviderHttpRequestReader.MaxBodyBytes / 2:X}\r\n" + body[..(body.Length / 2)] +
                $"\r\n{NativeProviderHttpRequestReader.MaxBodyBytes / 2:X}\r\n" + body[(body.Length / 2)..] + "\r\n0\r\n\r\n")
            : Request($"Content-Length: {NativeProviderHttpRequestReader.MaxBodyBytes}\r\n", body);
        var parsed = await ReadTextAsync(text);
        Assert.NotNull(parsed);
        Assert.Equal(NativeProviderHttpRequestReader.MaxBodyBytes, Encoding.UTF8.GetByteCount(parsed.Body));
        Assert.Equal(body, parsed.Body);
    }

    [Theory]
    [InlineData("declared-length")]
    [InlineData("declared-chunk")]
    [InlineData("aggregate-body")]
    [InlineData("line")]
    [InlineData("headers")]
    [InlineData("chunk-framing")]
    [InlineData("trailer-framing")]
    public async Task BodyHeaderAndFramingBudgetsRejectOversizedRequests(string limit)
    {
        var text = limit switch
        {
            "declared-length" => Request($"Content-Length: {NativeProviderHttpRequestReader.MaxBodyBytes + 1}\r\n", ""),
            "declared-chunk" => Chunked($"{NativeProviderHttpRequestReader.MaxBodyBytes + 1:X}\r\n"),
            "aggregate-body" => Chunked($"{NativeProviderHttpRequestReader.MaxBodyBytes:X}\r\n" +
                new string('x', NativeProviderHttpRequestReader.MaxBodyBytes) + "\r\n1\r\ny\r\n0\r\n\r\n"),
            "line" => Request("X-Probe: " + new string('x', NativeProviderHttpRequestReader.MaxLineBytes) + "\r\n", ""),
            "headers" => Request(BudgetFields(NativeProviderHttpRequestReader.MaxHeaderBytes), ""),
            "chunk-framing" => Chunked(string.Concat(Enumerable.Repeat("1\r\nx\r\n",
                NativeProviderHttpRequestReader.MaxFramingBytes / 5 + 1)) + "0\r\n\r\n"),
            "trailer-framing" => Chunked("0\r\n" + BudgetFields(NativeProviderHttpRequestReader.MaxFramingBytes) + "\r\n"),
            _ => throw new ArgumentOutOfRangeException(nameof(limit))
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadTextAsync(text));
    }

    [Theory]
    [InlineData("header")]
    [InlineData("body")]
    [InlineData("trailer")]
    public async Task LivePartialRequestCancellationDrainsTheOwnedTcpReader(string stage)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancelled = new CancellationTokenSource();
        using var pair = await TcpPair.ConnectAsync(deadline.Token);
        var prefix = stage switch
        {
            "header" => "POST /chat/completions HTTP/1.1\r\nHost: fixture.test\r\nX-Partial: ",
            "body" => Request("Content-Length: 3\r\n", "a"),
            "trailer" => Chunked("0\r\nX-Trailer: partial"),
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
        var bytes = Encoding.ASCII.GetBytes(prefix);
        await using var observed = new ObservedReadStream(pair.Server.GetStream(), bytes.Length);
        var read = NativeProviderHttpRequestReader.ReadAsync(observed, cancelled.Token);
        try
        {
            await pair.Client.GetStream().WriteAsync(bytes, deadline.Token);
            await observed.PrefixConsumed.Task.WaitAsync(deadline.Token);
            Assert.False(read.IsCompleted);
            cancelled.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => read.WaitAsync(deadline.Token));
            Assert.Equal(cancelled.Token, error.CancellationToken);
            Assert.False(deadline.IsCancellationRequested);
        }
        finally
        {
            cancelled.Cancel();
            pair.Dispose();
            await DrainAsync(read);
        }
    }

    private static string Request(string headers, string body) =>
        "POST /chat/completions HTTP/1.1\r\nHost: fixture.test\r\n" + headers + "\r\n" + body;

    private static string Chunked(string body) => Request("Transfer-Encoding: chunked\r\n", body);

    private static string BudgetFields(int budget)
    {
        const int valueLength = 4096;
        return string.Concat(Enumerable.Range(0, budget / valueLength + 1)
            .Select(index => $"X-Budget-{index}: {new string('x', valueLength)}\r\n"));
    }

    private static async Task<NativeProviderHttpRequest?> ReadTextAsync(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await NativeProviderHttpRequestReader.ReadAsync(stream);
    }

    private static void WriteAscii(Stream stream, string value) => stream.Write(Encoding.ASCII.GetBytes(value));

    private static async Task DrainAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception error) when (error is not TimeoutException && task.IsCompleted)
        {
            // The primary assertion observes failures; cleanup only drains a task
            // after its listener/client has been disposed. A timeout still fails.
        }
    }

    private sealed class TcpPair(TcpClient client, TcpClient server) : IDisposable
    {
        public TcpClient Client { get; } = client;
        public TcpClient Server { get; } = server;

        public static async Task<TcpPair> ConnectAsync(CancellationToken cancellationToken)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
                var server = await listener.AcceptTcpClientAsync(cancellationToken);
                return new TcpPair(client, server);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public void Dispose() { Client.Dispose(); Server.Dispose(); }
    }

    private sealed class ObservedReadStream(Stream inner, int expectedPrefixBytes) : Stream
    {
        private int _consumed;
        public TaskCompletionSource PrefixConsumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            _consumed += count;
            if (_consumed >= expectedPrefixBytes) PrefixConsumed.TrySetResult();
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
