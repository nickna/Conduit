using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConduitLLM.IntegrationTests.Tests;

[CollectionDefinition("NativeBenchmarkWorkload", DisableParallelization = true)]
public sealed class NativeBenchmarkWorkloadCollection;

[Collection("NativeBenchmarkWorkload")]
public sealed class NativeBenchmarkWorkloadProbeTests
{
    private const string SecretVariable = "CONDUIT_NATIVE_BENCHMARK_API_KEY";
    private const string Secret = "native-benchmark-private-fixture-key";
    private const string SettingKey = "native_benchmark_café";
    private const string Content = "reply café 雪 🔎";

    [Theory]
    [InlineData("admin-setting-read")]
    [InlineData("gateway-chat-nonstream")]
    [InlineData("gateway-chat-sse")]
    public async Task AuthenticatedWorkloadsValidateCompleteUnicodeResponsesAndSeparateWarmupFromMeasuredCounts(string workload)
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(ValidResponse(workload));
        var configuration = Configuration(server, workload, requests: 3, concurrency: 2, warmup: 1);

        var (exitCode, report) = await server.RunAsync(configuration);

        Assert.Equal(0, exitCode);
        var row = AssertReport(report, workload, requested: 3, completed: 3, failures: 0, warmup: 1, concurrency: 2);
        Assert.Equal(4, server.Requests.Count);
        foreach (var request in server.Requests)
        {
            Assert.Equal("Bearer " + Secret, request.Headers["Authorization"]);
            if (workload == "admin-setting-read")
            {
                Assert.Equal("GET", request.Method);
                Assert.Equal("/v1/admin/global-settings/by-key/" + Uri.EscapeDataString(SettingKey), request.Path);
                Assert.Empty(request.Body);
            }
            else
            {
                Assert.Equal("POST", request.Method);
                Assert.Equal("/v1/chat/completions", request.Path);
                using var body = JsonDocument.Parse(request.Body);
                Assert.Equal("native-model", body.RootElement.GetProperty("model").GetString());
                var message = Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray());
                Assert.Equal("user", message.GetProperty("role").GetString());
                Assert.Equal("prompt café 雪 🔎", message.GetProperty("content").GetString());
                Assert.Equal(workload == "gateway-chat-sse", body.RootElement.GetProperty("stream").GetBoolean());
                if (workload == "gateway-chat-sse")
                    Assert.True(body.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
            }
        }
        Assert.Equal(workload == "admin-setting-read" ? "GET" : "POST", row.GetProperty("method").GetString());
        Assert.False(report.GetRawText().Contains(Secret, StringComparison.Ordinal));
        Assert.False(report.GetRawText().Contains(Content, StringComparison.Ordinal));
        Assert.DoesNotContain(SettingKey, report.GetRawText());
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("missing-fields")]
    [InlineData("wrong-content")]
    [InlineData("wrong-usage")]
    [InlineData("invalid-admin-time")]
    [InlineData("sse-premature-eof")]
    [InlineData("sse-no-usage")]
    [InlineData("sse-no-done")]
    [InlineData("sse-after-done")]
    [InlineData("unauthorized")]
    public async Task HttpSuccessRequiresValidCompleteWorkloadAndHttpFailureProducesOnlyAggregateFailureEvidence(string scenario)
    {
        using var secret = new EnvironmentSecret(Secret);
        var workload = scenario.StartsWith("sse-", StringComparison.Ordinal) ? "gateway-chat-sse"
            : scenario == "invalid-admin-time" ? "admin-setting-read" : "gateway-chat-nonstream";
        var response = scenario switch
        {
            "invalid-json" => new Response("application/json", Secret + " invalid private diagnostics"),
            "missing-fields" => new Response("application/json", "{}"),
            "wrong-content" => new Response("application/json", ChatResponse("private unexpected " + Secret)),
            "wrong-usage" => new Response("application/json", ChatResponse(completionTokens: 6)),
            "invalid-admin-time" => new Response("application/json", AdminResponse(updatedAt: "2024-01-01T00:00:00Z")),
            "sse-premature-eof" => new Response("text/event-stream", SseResponse().TrimEnd('\n')),
            "sse-no-usage" => new Response("text/event-stream", SseResponse(includeUsage: false)),
            "sse-no-done" => new Response("text/event-stream", SseResponse(includeDone: false)),
            "sse-after-done" => new Response("text/event-stream", SseResponse() + "data: " + Chunk("", role: false) + "\n\n"),
            _ => new Response("application/json", "{\"private\":\"" + Secret + "\"}", StatusCode: 401)
        };
        await using var server = new LoopbackServer(response);

        var (exitCode, report) = await server.RunAsync(Configuration(server, workload));

        Assert.Equal(1, exitCode);
        AssertReport(report, workload, requested: 1, completed: 0, failures: 1);
        Assert.Single(server.Requests);
        Assert.DoesNotContain(Secret, report.GetRawText());
        Assert.DoesNotContain("private unexpected", report.GetRawText());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseLimitRejectsOtherwiseValidJsonByDeclaredLengthAndAccumulatedChunkedBytes(bool chunked)
    {
        using var secret = new EnvironmentSecret(Secret);
        var body = ChatResponse();
        body += new string(' ', NativeBenchmarkWorkloadProbe.MaximumResponseBytes + 1 - Encoding.UTF8.GetByteCount(body));
        await using var server = new LoopbackServer(new Response("application/json", body, Chunked: chunked));

        var (exitCode, report) = await server.RunAsync(Configuration(server, "gateway-chat-nonstream"));

        Assert.Equal(1, exitCode);
        AssertReport(report, "gateway-chat-nonstream", requested: 1, completed: 0, failures: 1);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task PerRequestDeadlineBoundsAReceivedRequestWithoutAResponse()
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(new Response("application/json", ChatResponse(), Hold: true));
        var configuration = Configuration(server, "gateway-chat-nonstream");
        configuration["timeoutSeconds"] = 1;
        var pending = server.RunAsync(configuration);
        await server.RequestReceived.WaitAsync(server.Token);

        var (exitCode, report) = await pending.WaitAsync(server.Token);

        Assert.Equal(1, exitCode);
        AssertReport(report, "gateway-chat-nonstream", requested: 1, completed: 0, failures: 1, timeout: 1);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task CallerCancellationAfterActualRequestProducesNoPartialReport()
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(new Response("text/event-stream", SseResponse(), Hold: true));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(server.Token);
        using var output = new MemoryStream();
        await server.WriteConfigurationAsync(Configuration(server, "gateway-chat-sse"));
        var pending = NativeBenchmarkWorkloadProbe.RunAsync(server.ConfigurationPath, output, caller.Token);
        await server.RequestReceived.WaitAsync(server.Token);

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(server.Token));
        Assert.Equal(0, output.Length);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task FailedWarmupStopsBeforeMeasurementAndEmitsNoReport()
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(new Response("application/json", "{\"secret\":\"" + Secret + "\"}"));
        using var output = new MemoryStream();
        await server.WriteConfigurationAsync(Configuration(server, "gateway-chat-nonstream", warmup: 1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NativeBenchmarkWorkloadProbe.RunAsync(server.ConfigurationPath, output, server.Token));

        Assert.DoesNotContain(Secret, exception.ToString());
        Assert.Equal(0, output.Length);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData("unknown-workload")]
    [InlineData("zero-requests")]
    [InlineData("missing-requests")]
    [InlineData("string-requests")]
    [InlineData("fractional-requests")]
    [InlineData("too-many-requests")]
    [InlineData("zero-concurrency")]
    [InlineData("too-many-concurrency")]
    [InlineData("concurrency-exceeds-requests")]
    [InlineData("zero-timeout")]
    [InlineData("too-large-timeout")]
    [InlineData("negative-warmup")]
    [InlineData("unknown-property")]
    [InlineData("wrong-workload-field")]
    [InlineData("uri-userinfo")]
    [InlineData("uri-query")]
    [InlineData("uri-path")]
    [InlineData("uri-fragment")]
    public async Task InvalidConfigurationRejectsBeforeTcpAndDoesNotExposeSecrets(string scenario)
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(ValidResponse("gateway-chat-nonstream"));
        var configuration = Configuration(server, "gateway-chat-nonstream");
        switch (scenario)
        {
            case "unknown-workload": configuration["workload"] = "private-" + Secret; break;
            case "zero-requests": configuration["requests"] = 0; break;
            case "missing-requests": configuration.Remove("requests"); break;
            case "string-requests": configuration["requests"] = "1"; break;
            case "fractional-requests": configuration["requests"] = 1.5; break;
            case "too-many-requests": configuration["requests"] = NativeBenchmarkWorkloadProbe.MaximumRequests + 1; break;
            case "zero-concurrency": configuration["concurrency"] = 0; break;
            case "too-many-concurrency":
                configuration["requests"] = NativeBenchmarkWorkloadProbe.MaximumConcurrency + 1;
                configuration["concurrency"] = NativeBenchmarkWorkloadProbe.MaximumConcurrency + 1;
                break;
            case "concurrency-exceeds-requests": configuration["concurrency"] = 2; break;
            case "zero-timeout": configuration["timeoutSeconds"] = 0; break;
            case "too-large-timeout": configuration["timeoutSeconds"] = NativeBenchmarkWorkloadProbe.MaximumTimeoutSeconds + 1; break;
            case "negative-warmup": configuration["warmupRequests"] = -1; break;
            case "unknown-property": configuration["apiKey"] = Secret; break;
            case "wrong-workload-field": configuration["settingKey"] = Secret; break;
            case "uri-userinfo": configuration["baseUrl"] = server.Endpoint.AbsoluteUri.Replace("http://", "http://" + Secret + "@"); break;
            case "uri-query": configuration["baseUrl"] = server.Endpoint.AbsoluteUri + "?key=" + Secret; break;
            case "uri-path": configuration["baseUrl"] = server.Endpoint.AbsoluteUri + Secret; break;
            case "uri-fragment": configuration["baseUrl"] = server.Endpoint.AbsoluteUri + "#" + Secret; break;
        }
        await AssertConfigurationRejectedAsync(server, configuration);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" \t")]
    public async Task AbsentOrBlankEnvironmentCredentialRejectsBeforeTcp(string? value)
    {
        using var secret = new EnvironmentSecret(value);
        await using var server = new LoopbackServer(ValidResponse("admin-setting-read"));

        await AssertConfigurationRejectedAsync(server, Configuration(server, "admin-setting-read"));
    }

    [Fact]
    public async Task ConfigurationByteLimitRejectsOtherwiseValidDocumentBeforeTcp()
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(ValidResponse("gateway-chat-nonstream"));
        var json = Configuration(server, "gateway-chat-nonstream").ToJsonString();
        json += new string(' ', NativeBenchmarkWorkloadProbe.MaximumConfigurationBytes + 1 - Encoding.UTF8.GetByteCount(json));
        await File.WriteAllTextAsync(server.ConfigurationPath, json, new UTF8Encoding(false), server.Token);
        using var output = new MemoryStream();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            NativeBenchmarkWorkloadProbe.RunAsync(server.ConfigurationPath, output, server.Token));

        Assert.DoesNotContain(Secret, exception.ToString());
        Assert.Equal(0, output.Length);
        Assert.Equal(0, server.AcceptedConnections);
    }

    [Fact]
    public async Task DuplicateRawConfigurationPropertyRejectsBeforeTcpEvenWhenValuesAgree()
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var server = new LoopbackServer(ValidResponse("gateway-chat-nonstream"));
        var json = Configuration(server, "gateway-chat-nonstream").ToJsonString();
        json = json[..^1] + ",\"requests\":1}";
        await File.WriteAllTextAsync(server.ConfigurationPath, json, new UTF8Encoding(false), server.Token);
        using var output = new MemoryStream();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            NativeBenchmarkWorkloadProbe.RunAsync(server.ConfigurationPath, output, server.Token));

        Assert.DoesNotContain(Secret, exception.ToString());
        Assert.Equal(0, output.Length);
        Assert.Equal(0, server.AcceptedConnections);
        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("admin-setting-read")]
    [InlineData("gateway-chat-nonstream")]
    public async Task FingerprintIgnoresTransportAndRunControlsButChangesWhenValidatedInputsChange(string workload)
    {
        using var secret = new EnvironmentSecret(Secret);
        await using var first = new LoopbackServer(ValidResponse(workload));
        var (originalExitCode, original) = await first.RunAsync(Configuration(first, workload));
        Assert.Equal(0, originalExitCode);
        var fingerprint = Row(original).GetProperty("inputFingerprint").GetString();
        await using var second = new LoopbackServer(ValidResponse(workload));
        var controls = Configuration(second, workload, requests: 2, concurrency: 2, warmup: 1);
        controls["timeoutSeconds"] = 2;
        Environment.SetEnvironmentVariable(SecretVariable, "other-private-fixture-key");

        var (equivalentExitCode, equivalent) = await second.RunAsync(controls);

        Assert.Equal(0, equivalentExitCode);
        Assert.Equal(fingerprint, Row(equivalent).GetProperty("inputFingerprint").GetString());
        Assert.All(second.Requests, request => Assert.Equal("Bearer other-private-fixture-key", request.Headers["Authorization"]));
        if (workload == "admin-setting-read")
        {
            controls["expectedValue"] = "different café";
            second.Response = new Response("application/json", AdminResponse(value: "different café"));
        }
        else
        {
            controls["prompt"] = "different prompt 雪";
            controls["expectedCompletionTokens"] = 6;
            second.Response = new Response("application/json", ChatResponse(completionTokens: 6));
        }

        var (exitCode, different) = await second.RunAsync(controls);

        Assert.Equal(0, exitCode);
        Assert.NotEqual(fingerprint, Row(different).GetProperty("inputFingerprint").GetString());
    }

    private static async Task AssertConfigurationRejectedAsync(LoopbackServer server, JsonObject configuration)
    {
        await server.WriteConfigurationAsync(configuration);
        using var output = new MemoryStream();
        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            NativeBenchmarkWorkloadProbe.RunAsync(server.ConfigurationPath, output, server.Token));
        Assert.DoesNotContain(Secret, exception.ToString());
        Assert.Equal(0, output.Length);
        Assert.Equal(0, server.AcceptedConnections);
        Assert.Empty(server.Requests);
    }

    private static JsonObject Configuration(LoopbackServer server, string workload, int requests = 1, int concurrency = 1, int warmup = 0)
    {
        var result = new JsonObject
        {
            ["workload"] = workload, ["baseUrl"] = server.Endpoint.AbsoluteUri,
            ["requests"] = requests, ["concurrency"] = concurrency,
            ["warmupRequests"] = warmup, ["timeoutSeconds"] = 5
        };
        if (workload == "admin-setting-read")
        {
            result["settingKey"] = SettingKey;
            result["expectedValue"] = Content;
        }
        else
        {
            result["model"] = "native-model";
            result["prompt"] = "prompt café 雪 🔎";
            result["expectedContent"] = Content;
            result["expectedPromptTokens"] = 10;
            result["expectedCompletionTokens"] = 5;
        }
        return result;
    }

    private static JsonElement AssertReport(JsonElement report, string workload, int requested, int completed, int failures,
        int warmup = 0, int concurrency = 1, int timeout = 5)
    {
        Assert.Equal(2, report.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(workload == "admin-setting-read" ? "admin" : "gateway", report.GetProperty("service").GetString());
        Assert.True(report.GetProperty("generatedAt").TryGetDateTimeOffset(out var generatedAt));
        Assert.Equal(TimeSpan.Zero, generatedAt.Offset);
        var row = Row(report);
        Assert.Equal(workload, row.GetProperty("id").GetString());
        Assert.Equal(workload == "admin-setting-read" ? "/v1/admin/global-settings/by-key/{key}" : "/v1/chat/completions",
            row.GetProperty("path").GetString());
        Assert.True(row.GetProperty("authenticated").GetBoolean());
        Assert.Equal(requested, row.GetProperty("requested").GetInt32());
        Assert.Equal(requested, row.GetProperty("executed").GetInt32());
        Assert.Equal(completed, row.GetProperty("completed").GetInt32());
        Assert.Equal(failures, row.GetProperty("failures").GetInt32());
        Assert.Equal(warmup, row.GetProperty("warmupRequests").GetInt32());
        Assert.Equal(concurrency, row.GetProperty("concurrency").GetInt32());
        Assert.Equal(timeout, row.GetProperty("timeoutSeconds").GetInt32());
        Assert.Matches("^[0-9a-f]{64}$", row.GetProperty("inputFingerprint").GetString()!);
        var elapsed = row.GetProperty("elapsedMilliseconds").GetDouble();
        Assert.True(double.IsFinite(elapsed) && elapsed > 0);
        var throughput = row.GetProperty("throughputPerSecond").GetDouble();
        Assert.True(double.IsFinite(throughput) && throughput >= 0);
        var p50 = row.GetProperty("p50Milliseconds").GetDouble();
        var p95 = row.GetProperty("p95Milliseconds").GetDouble();
        var p99 = row.GetProperty("p99Milliseconds").GetDouble();
        Assert.True(double.IsFinite(p50) && p50 >= 0 && p50 <= p95 && p95 <= p99 && double.IsFinite(p99));
        return row;
    }

    private static JsonElement Row(JsonElement report) => Assert.Single(report.GetProperty("workloads").EnumerateArray());

    private static Response ValidResponse(string workload) => workload switch
    {
        "admin-setting-read" => new Response("application/json", AdminResponse()),
        "gateway-chat-sse" => new Response("text/event-stream", SseResponse()),
        _ => new Response("application/json", ChatResponse())
    };

    private static string AdminResponse(string value = Content, string updatedAt = "2025-01-02T00:00:00Z") => Json(writer =>
    {
        writer.WriteNumber("id", 42); writer.WriteString("key", SettingKey); writer.WriteString("value", value);
        writer.WriteNull("description"); writer.WriteString("createdAt", "2025-01-01T00:00:00Z"); writer.WriteString("updatedAt", updatedAt);
    });

    private static string ChatResponse(string content = Content, int completionTokens = 5) => Json(writer =>
    {
        Envelope(writer, "chat.completion");
        writer.WriteStartArray("choices"); writer.WriteStartObject(); writer.WriteNumber("index", 0);
        writer.WriteStartObject("message"); writer.WriteString("role", "assistant"); writer.WriteString("content", content); writer.WriteEndObject();
        writer.WriteString("finish_reason", "stop"); writer.WriteEndObject(); writer.WriteEndArray(); Usage(writer, completionTokens);
    });

    private static string SseResponse(bool includeUsage = true, bool includeDone = true)
    {
        var result = ": fixture Unicode chunks\n\n"
            + "data: " + Chunk("", role: true) + "\n\n"
            + "data: " + Chunk("reply café ", role: false) + "\n\n"
            + "data: " + Chunk("雪 🔎", role: false) + "\n\n"
            + "data: " + Chunk(null, role: false, stop: true) + "\n\n";
        if (includeUsage)
            result += "data: " + Json(writer => { Envelope(writer, "chat.completion.chunk"); writer.WriteStartArray("choices"); writer.WriteEndArray(); Usage(writer); }) + "\n\n";
        return result + (includeDone ? "data: [DONE]\n\n" : "");
    }

    private static string Chunk(string? content, bool role, bool stop = false) => Json(writer =>
    {
        Envelope(writer, "chat.completion.chunk"); writer.WriteStartArray("choices"); writer.WriteStartObject(); writer.WriteNumber("index", 0);
        writer.WriteStartObject("delta"); if (role) writer.WriteString("role", "assistant");
        if (content is not null) writer.WriteString("content", content); writer.WriteEndObject();
        if (stop) writer.WriteString("finish_reason", "stop"); else writer.WriteNull("finish_reason");
        writer.WriteEndObject(); writer.WriteEndArray();
    });

    private static void Envelope(Utf8JsonWriter writer, string objectName)
    {
        writer.WriteString("id", "chat-native-benchmark"); writer.WriteString("object", objectName);
        writer.WriteNumber("created", 1735689600); writer.WriteString("model", "provider-model");
    }

    private static void Usage(Utf8JsonWriter writer, int completionTokens = 5)
    {
        writer.WriteStartObject("usage"); writer.WriteNumber("prompt_tokens", 10);
        writer.WriteNumber("completion_tokens", completionTokens); writer.WriteNumber("total_tokens", 10 + completionTokens); writer.WriteEndObject();
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject(); write(writer); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class EnvironmentSecret : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable(SecretVariable);
        public EnvironmentSecret(string? value) => Environment.SetEnvironmentVariable(SecretVariable, value);
        public void Dispose() => Environment.SetEnvironmentVariable(SecretVariable, _previous);
    }

    private sealed record Response(string ContentType, string Body, int StatusCode = 200, bool Hold = false, bool Chunked = false);

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));
        private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
        private readonly ConcurrentBag<Task> _handlers = [];
        private readonly ConcurrentQueue<Exception> _failures = new();
        private readonly TaskCompletionSource _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _accept;
        private readonly string _directory;
        private int _accepted;

        public LoopbackServer(Response response)
        {
            Response = response;
            _directory = Path.Combine(Path.GetTempPath(), "conduit-benchmark-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            ConfigurationPath = Path.Combine(_directory, "workload.json");
            _listener.Start();
            Endpoint = new Uri("http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture) + "/");
            _accept = AcceptAsync();
        }

        public Uri Endpoint { get; }
        public string ConfigurationPath { get; }
        public Response Response { get; set; }
        public ConcurrentQueue<NativeProviderHttpRequest> Requests { get; } = new();
        public Task RequestReceived => _received.Task;
        public CancellationToken Token => _deadline.Token;
        public int AcceptedConnections => Volatile.Read(ref _accepted);

        public Task WriteConfigurationAsync(JsonObject configuration) =>
            File.WriteAllTextAsync(ConfigurationPath, configuration.ToJsonString(), new UTF8Encoding(false), Token);

        public async Task<(int ExitCode, JsonElement Report)> RunAsync(JsonObject configuration)
        {
            await WriteConfigurationAsync(configuration);
            using var output = new MemoryStream();
            var result = await NativeBenchmarkWorkloadProbe.RunAsync(ConfigurationPath, output, Token).WaitAsync(Token);
            using var report = JsonDocument.Parse(output.ToArray());
            return (result, report.RootElement.Clone());
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var id = Interlocked.Increment(ref _accepted);
                    _clients[id] = client;
                    _handlers.Add(HandleAsync(id, client));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            catch (Exception exception) { _failures.Enqueue(exception); }
        }

        private async Task HandleAsync(int id, TcpClient client)
        {
            try
            {
                using var stream = client.GetStream();
                var request = await NativeProviderHttpRequestReader.ReadAsync(stream, _stop.Token);
                if (request is null) return;
                Requests.Enqueue(request);
                _received.TrySetResult();
                var response = Response;
                if (response.Hold) await Task.Delay(Timeout.Infinite, _stop.Token);
                var body = Encoding.UTF8.GetBytes(response.Body);
                var framing = response.Chunked ? "Transfer-Encoding: chunked\r\n"
                    : "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n";
                var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + response.StatusCode.ToString(CultureInfo.InvariantCulture)
                    + " fixture\r\nContent-Type: " + response.ContentType + "; charset=utf-8\r\n" + framing + "Connection: close\r\n\r\n");
                await stream.WriteAsync(header, _stop.Token);
                if (response.Chunked)
                {
                    for (var offset = 0; offset < body.Length; offset += 16384)
                    {
                        var length = Math.Min(16384, body.Length - offset);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(length.ToString("x", CultureInfo.InvariantCulture) + "\r\n"), _stop.Token);
                        await stream.WriteAsync(body.AsMemory(offset, length), _stop.Token);
                        await stream.WriteAsync("\r\n"u8.ToArray(), _stop.Token);
                    }
                    await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), _stop.Token);
                }
                else if (response.ContentType == "text/event-stream")
                {
                    // Seven-byte writes force multibyte text across network writes without sleeps.
                    for (var offset = 0; offset < body.Length; offset += 7)
                        await stream.WriteAsync(body.AsMemory(offset, Math.Min(7, body.Length - offset)), _stop.Token);
                }
                else await stream.WriteAsync(body, _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            catch (IOException) { /* A cancelled or size-limited client may close its owned response socket. */ }
            catch (Exception exception) { _failures.Enqueue(exception); }
            finally
            {
                _clients.TryRemove(id, out _);
                client.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                _stop.Cancel();
                _listener.Stop();
                foreach (var client in _clients.Values) client.Dispose();
                await _accept.WaitAsync(cleanup.Token);
                await Task.WhenAll(_handlers.ToArray()).WaitAsync(cleanup.Token);
                Assert.Empty(_clients);
                Assert.Empty(_failures);
            }
            finally
            {
                _deadline.Dispose(); _stop.Dispose();
                if (File.Exists(ConfigurationPath)) File.Delete(ConfigurationPath);
                Directory.Delete(_directory);
            }
        }
    }
}
