using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Measures a closed, authenticated HTTP workload against an existing fixture.</summary>
internal static class NativeBenchmarkWorkloadProbe
{
    internal const int MaximumConfigurationBytes = 64 * 1024;
    internal const int MaximumResponseBytes = 1024 * 1024;
    internal const int MaximumRequests = 100_000;
    internal const int MaximumConcurrency = 256;
    internal const int MaximumWarmupRequests = 10_000;
    internal const int MaximumTimeoutSeconds = 120;
    internal const int MaximumDiagnosticBytes = 64 * 1024 * 1024;
    private const int MaximumSseLineCharacters = 64 * 1024;
    private const string SecretEnvironmentVariable = "CONDUIT_NATIVE_BENCHMARK_API_KEY";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Writes one schema-2 report. A measured failure returns 1; configuration, warm-up,
    /// and caller cancellation failures produce no report. The caller owns the output stream.
    /// </summary>
    internal static async Task<int> RunAsync(
        string configurationPath, Stream output, CancellationToken cancellationToken = default,
        Stream? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
            throw new ArgumentException("Benchmark output must be writable.", nameof(output));
        if (diagnostics is not null && (!diagnostics.CanWrite || ReferenceEquals(output, diagnostics)))
            throw new ArgumentException("Benchmark diagnostic output must be separate and writable.", nameof(diagnostics));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            var configuration = await ReadConfigurationAsync(configurationPath, deadline.Token);
            var apiKey = Environment.GetEnvironmentVariable(SecretEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 8192 ||
                apiKey.Any(static value => char.IsControl(value) || char.IsWhiteSpace(value)))
                throw new InvalidOperationException("Benchmark authentication is unavailable.");

            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                MaxConnectionsPerServer = configuration.Concurrency,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            var origin = diagnostics is null ? 0 : Stopwatch.GetTimestamp();
            var phases = diagnostics is null ? null : new List<PhaseDiagnostic>(2);
            var warmup = await RunPhaseAsync(configuration, client, apiKey,
                configuration.WarmupRequests, deadline.Token, phases, "warmup", origin);
            if (warmup.Completed != configuration.WarmupRequests || warmup.Failures != 0)
                throw new InvalidOperationException("Benchmark warm-up failed.");

            var measured = await RunPhaseAsync(configuration, client, apiKey,
                configuration.Requests, deadline.Token, phases, "measured", origin);
            deadline.Token.ThrowIfCancellationRequested();
            // Retain original schema-2 stdout. Diagnostic rows are a separate,
            // explicitly client-scoped artifact written outside both batch timers.
            if (diagnostics is not null)
            {
                using var bytes = new MemoryStream();
                using (var diagnosticWriter = new Utf8JsonWriter(bytes))
                    WriteDiagnostics(diagnosticWriter, configuration, phases!);
                if (bytes.Length > MaximumDiagnosticBytes)
                    throw new InvalidDataException("Benchmark diagnostic output exceeds its limit.");
                deadline.Token.ThrowIfCancellationRequested();
                await diagnostics.WriteAsync(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)), deadline.Token);
                await diagnostics.FlushAsync(deadline.Token);
            }
            using var writer = new Utf8JsonWriter(output);
            WriteReport(writer, configuration, measured);
            await writer.FlushAsync(deadline.Token);
            return measured.Failures == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("Benchmark operation was cancelled.", cancellationToken);
        }
    }

    private static async Task<Configuration> ReadConfigurationAsync(string path, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Benchmark configuration is invalid.", nameof(path));
        try
        {
            await using var stream = File.OpenRead(path);
            var bytes = await ReadBoundedAsync(stream, MaximumConfigurationBytes, token);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw InvalidConfiguration();
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!properties.TryAdd(property.Name, property.Value))
                    throw InvalidConfiguration();

            var workload = ReadText(properties, "workload");
            var admin = workload == "admin-setting-read";
            if (!admin && workload is not ("gateway-chat-nonstream" or "gateway-chat-sse"))
                throw InvalidConfiguration();
            var expectedProperties = new HashSet<string>(StringComparer.Ordinal)
            {
                "workload", "baseUrl", "requests", "concurrency", "warmupRequests", "timeoutSeconds"
            };
            expectedProperties.UnionWith(admin
                ? ["settingKey", "expectedValue"]
                : ["model", "prompt", "expectedContent", "expectedPromptTokens", "expectedCompletionTokens"]);
            if (properties.Count != expectedProperties.Count || properties.Keys.Any(key => !expectedProperties.Contains(key)))
                throw InvalidConfiguration();

            var baseUrl = ReadText(properties, "baseUrl");
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin) ||
                origin.Scheme is not ("http" or "https") || string.IsNullOrEmpty(origin.Host) ||
                origin.AbsolutePath != "/" || origin.UserInfo.Length != 0 ||
                origin.Query.Length != 0 || origin.Fragment.Length != 0)
                throw InvalidConfiguration();
            var requests = ReadInteger(properties, "requests", 1, MaximumRequests);
            var concurrency = ReadInteger(properties, "concurrency", 1, MaximumConcurrency);
            if (concurrency > requests)
                throw InvalidConfiguration();
            return new Configuration(
                workload, origin, requests, concurrency,
                ReadInteger(properties, "warmupRequests", 0, MaximumWarmupRequests),
                ReadInteger(properties, "timeoutSeconds", 1, MaximumTimeoutSeconds),
                admin ? ReadText(properties, "settingKey") : null,
                admin ? ReadText(properties, "expectedValue", allowEmpty: true) : null,
                admin ? null : ReadText(properties, "model"),
                admin ? null : ReadText(properties, "prompt"),
                admin ? null : ReadText(properties, "expectedContent"),
                admin ? 0 : ReadInteger(properties, "expectedPromptTokens", 0, 1_000_000_000),
                admin ? 0 : ReadInteger(properties, "expectedCompletionTokens", 0, 1_000_000_000));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or InvalidDataException or ArgumentException)
        {
            throw InvalidConfiguration();
        }
    }

    private static string ReadText(Dictionary<string, JsonElement> properties, string name, bool allowEmpty = false)
    {
        if (!properties.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw InvalidConfiguration();
        var text = value.GetString()!;
        if (text.Length > 16_384 || (!allowEmpty && string.IsNullOrWhiteSpace(text)))
            throw InvalidConfiguration();
        return text;
    }

    private static int ReadInteger(Dictionary<string, JsonElement> properties, string name, int minimum, int maximum)
    {
        if (!properties.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var number) ||
            number < minimum || number > maximum)
            throw InvalidConfiguration();
        return number;
    }

    private static InvalidDataException InvalidConfiguration() => new("Benchmark configuration is invalid.");

    private static async Task<Phase> RunPhaseAsync(
        Configuration configuration, HttpClient client, string apiKey, int requests, CancellationToken token,
        List<PhaseDiagnostic>? diagnostics, string name, long origin)
    {
        if (requests == 0 && diagnostics is null)
            return new Phase(0, 0, 0, 0, TimeSpan.Zero, []);
        var samples = new long[requests];
        // Allocate rows and generated correlation IDs before the batch window;
        // each claimed index has one writer, so no timing-path locks are needed.
        var timings = diagnostics is null ? null : new RequestDiagnostic[requests];
        if (timings is not null)
            for (var index = 0; index < timings.Length; index++)
                timings[index] = new RequestDiagnostic(index, configuration.IsStream);
        var before = diagnostics is null ? null : RuntimeSnapshot.Capture();
        var phaseStart = diagnostics is null ? 0 : Stopwatch.GetTimestamp() - origin;
        var next = -1;
        var executed = 0;
        var completed = 0;
        var elapsed = Stopwatch.StartNew();
        var workers = new Task[Math.Min(configuration.Concurrency, requests)];
        for (var worker = 0; worker < workers.Length; worker++)
            workers[worker] = WorkAsync(worker);
        await Task.WhenAll(workers);
        elapsed.Stop();
        if (diagnostics is not null)
        {
            var phaseEnd = Stopwatch.GetTimestamp() - origin;
            diagnostics.Add(new PhaseDiagnostic(name, requests, executed, completed, executed - completed,
                elapsed.ElapsedTicks, phaseStart, phaseEnd, before!, RuntimeSnapshot.Capture(), timings!));
        }
        Array.Sort(samples);
        return new Phase(requests, executed, completed, executed - completed, elapsed.Elapsed, samples);

        async Task WorkAsync(int worker)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var index = Interlocked.Increment(ref next);
                if (index >= requests)
                    return;
                Interlocked.Increment(ref executed);
                var start = Stopwatch.GetTimestamp();
                var timing = timings?[index];
                if (timing is not null)
                {
                    timing.Worker = worker;
                    timing.StartOffsetTicks = start - origin;
                }
                try
                {
                    if (await ExecuteRequestAsync(configuration, client, apiKey, token, timing, origin))
                    {
                        Interlocked.Increment(ref completed);
                        if (timing is not null) timing.Succeeded = true;
                    }
                }
                finally
                {
                    var end = Stopwatch.GetTimestamp();
                    samples[index] = end - start;
                    if (timing is not null) timing.EndOffsetTicks = end - origin;
                }
            }
        }
    }

    private static async Task<bool> ExecuteRequestAsync(
        Configuration configuration, HttpClient client, string apiKey, CancellationToken token,
        RequestDiagnostic? diagnostic, long origin)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(configuration.TimeoutSeconds));
        try
        {
            var relativePath = configuration.IsAdmin
                ? "v1/admin/global-settings/by-key/" + Uri.EscapeDataString(configuration.SettingKey!)
                : "v1/chat/completions";
            using var request = new HttpRequestMessage(
                configuration.IsAdmin ? HttpMethod.Get : HttpMethod.Post,
                new Uri(configuration.Origin, relativePath))
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (diagnostic is not null)
                request.Headers.TryAddWithoutValidation("traceparent", "00-" + diagnostic.TraceId + "-" + diagnostic.SpanId + "-01");
            if (!configuration.IsAdmin)
            {
                request.Content = new ByteArrayContent(CreateChatRequest(configuration));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (diagnostic is not null) diagnostic.HeadersOffsetTicks = Stopwatch.GetTimestamp() - origin;
            if (response.StatusCode != HttpStatusCode.OK)
                return false;
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType != (configuration.IsStream ? "text/event-stream" : "application/json") ||
                response.Content.Headers.ContentLength is > MaximumResponseBytes)
                return false;
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            var bytes = await ReadBoundedAsync(stream, MaximumResponseBytes, deadline.Token,
                diagnostic?.Streaming, origin);
            if (diagnostic is not null)
            {
                diagnostic.BodyOffsetTicks = Stopwatch.GetTimestamp() - origin;
                diagnostic.ResponseBytes = bytes.Length;
            }
            if (configuration.IsStream)
                ValidateStream(configuration, StrictUtf8.GetString(bytes));
            else
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                if (configuration.IsAdmin)
                    ValidateSetting(configuration, document.RootElement);
                else
                    ValidateChat(configuration, document.RootElement);
            }
            if (diagnostic is not null) diagnostic.ValidatedOffsetTicks = Stopwatch.GetTimestamp() - origin;
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
            JsonException or InvalidDataException or InvalidOperationException or ArgumentException or KeyNotFoundException or
            OperationCanceledException)
        {
            return false;
        }
    }

    private static byte[] CreateChatRequest(Configuration configuration)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("model", configuration.Model);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", configuration.Prompt);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteBoolean("stream", configuration.IsStream);
            if (configuration.IsStream)
            {
                writer.WriteStartObject("stream_options");
                writer.WriteBoolean("include_usage", true);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return bytes.ToArray();
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken token,
        StreamingDiagnostic? streaming = null, long origin = 0)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0)
                return bytes.ToArray();
            if (bytes.Length + read > maximumBytes)
                throw new InvalidDataException("Benchmark input or response exceeds its limit.");
            streaming?.Observe(buffer.AsSpan(0, read), Stopwatch.GetTimestamp() - origin);
            await bytes.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }

    private static void ValidateSetting(Configuration configuration, JsonElement setting)
    {
        UniqueObject(setting);
        var fields = new HashSet<string>(StringComparer.Ordinal)
            { "id", "key", "value", "description", "createdAt", "updatedAt" };
        Require(setting.EnumerateObject().Count() == fields.Count &&
            setting.EnumerateObject().All(property => fields.Contains(property.Name)));
        Require(Integer(setting, "id") > 0 && Text(setting, "key") == configuration.SettingKey &&
            Text(setting, "value", allowEmpty: true) == configuration.ExpectedValue);
        var description = setting.GetProperty("description");
        Require(description.ValueKind is JsonValueKind.Null or JsonValueKind.String);
        Require(setting.GetProperty("createdAt").TryGetDateTimeOffset(out var created) &&
            setting.GetProperty("updatedAt").TryGetDateTimeOffset(out var updated) &&
            created.Offset == TimeSpan.Zero && updated.Offset == TimeSpan.Zero && created <= updated);
    }

    private static void ValidateChat(Configuration configuration, JsonElement root)
    {
        UniqueObject(root);
        NoError(root);
        Require(Text(root, "object") == "chat.completion");
        _ = Text(root, "id");
        _ = Text(root, "model");
        var choices = root.GetProperty("choices");
        Require(choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() == 1);
        var choice = choices[0];
        UniqueObject(choice);
        Require(Integer(choice, "index") == 0 && Text(choice, "finish_reason") == "stop");
        var message = choice.GetProperty("message");
        UniqueObject(message);
        Require(Text(message, "role") == "assistant" &&
            Text(message, "content", allowEmpty: true) == configuration.ExpectedContent);
        NoToolCalls(message);
        ValidateUsage(configuration, root.GetProperty("usage"));
    }

    private static void ValidateUsage(Configuration configuration, JsonElement usage)
    {
        UniqueObject(usage);
        Require(Integer(usage, "prompt_tokens") == configuration.ExpectedPromptTokens &&
            Integer(usage, "completion_tokens") == configuration.ExpectedCompletionTokens &&
            Integer(usage, "total_tokens") == configuration.ExpectedPromptTokens + configuration.ExpectedCompletionTokens);
    }

    private static void ValidateStream(Configuration configuration, string body)
    {
        using var reader = new StringReader(body);
        var eventData = new StringBuilder();
        var content = new StringBuilder();
        string? id = null;
        string? model = null;
        var assistant = false;
        var stopped = false;
        var usageCount = 0;
        var done = false;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            Require(line.Length <= MaximumSseLineCharacters);
            if (line.Length == 0)
            {
                if (eventData.Length != 0)
                {
                    ValidateEvent(eventData.ToString());
                    eventData.Clear();
                }
                continue;
            }
            if (line.StartsWith(':'))
                continue;
            Require(!done && line.StartsWith("data:", StringComparison.Ordinal));
            var data = line.AsSpan(5);
            if (data.StartsWith(" ", StringComparison.Ordinal))
                data = data[1..];
            if (eventData.Length != 0)
                eventData.Append('\n');
            eventData.Append(data);
        }
        Require(eventData.Length == 0 && done && assistant && stopped && usageCount == 1 &&
            content.ToString() == configuration.ExpectedContent);

        void ValidateEvent(string data)
        {
            Require(!done);
            if (data == "[DONE]")
            {
                Require(assistant && stopped && usageCount == 1 && content.ToString() == configuration.ExpectedContent);
                done = true;
                return;
            }
            using var document = JsonDocument.Parse(data, new JsonDocumentOptions { MaxDepth = 32 });
            var chunk = document.RootElement;
            UniqueObject(chunk);
            NoError(chunk);
            Require(Text(chunk, "object") == "chat.completion.chunk");
            var chunkId = Text(chunk, "id");
            var chunkModel = Text(chunk, "model");
            id ??= chunkId;
            model ??= chunkModel;
            Require(id == chunkId && model == chunkModel);
            var choices = chunk.GetProperty("choices");
            Require(choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() <= 1);
            if (choices.GetArrayLength() == 1)
            {
                Require(!stopped && usageCount == 0);
                var choice = choices[0];
                UniqueObject(choice);
                Require(Integer(choice, "index") == 0);
                var delta = choice.GetProperty("delta");
                UniqueObject(delta);
                NoToolCalls(delta);
                if (delta.TryGetProperty("role", out var role) && role.ValueKind != JsonValueKind.Null)
                {
                    Require(role.ValueKind == JsonValueKind.String && role.GetString() == "assistant");
                    assistant = true;
                }
                if (delta.TryGetProperty("content", out var text) && text.ValueKind != JsonValueKind.Null)
                {
                    Require(text.ValueKind == JsonValueKind.String);
                    content.Append(text.GetString());
                }
                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind != JsonValueKind.Null)
                {
                    Require(finish.ValueKind == JsonValueKind.String && finish.GetString() == "stop");
                    stopped = true;
                }
            }
            if (chunk.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
            {
                Require(stopped && usageCount == 0);
                ValidateUsage(configuration, usage);
                usageCount++;
            }
        }
    }

    private static void NoToolCalls(JsonElement value)
    {
        foreach (var name in new[] { "tool_calls", "function_call" })
            if (value.TryGetProperty(name, out var calls))
                Require(calls.ValueKind == JsonValueKind.Null);
    }

    private static void NoError(JsonElement value)
    {
        if (value.TryGetProperty("error", out var error))
            Require(error.ValueKind == JsonValueKind.Null);
    }

    private static void UniqueObject(JsonElement value)
    {
        Require(value.ValueKind == JsonValueKind.Object);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            Require(names.Add(property.Name));
    }

    private static int Integer(JsonElement value, string name)
    {
        Require(value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out _));
        return property.GetInt32();
    }

    private static string Text(JsonElement value, string name, bool allowEmpty = false)
    {
        Require(value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String);
        var text = property.GetString()!;
        Require(allowEmpty || !string.IsNullOrWhiteSpace(text));
        return text;
    }

    private static void Require(bool condition)
    {
        if (!condition)
            throw new InvalidDataException("Benchmark response is invalid.");
    }

    private static string Fingerprint(Configuration configuration)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("id", configuration.Workload);
            writer.WriteString("method", configuration.IsAdmin ? "GET" : "POST");
            writer.WriteString("path", configuration.PathTemplate);
            if (configuration.IsAdmin)
            {
                writer.WriteString("settingKey", configuration.SettingKey);
                writer.WriteString("expectedValue", configuration.ExpectedValue);
            }
            else
            {
                writer.WriteString("model", configuration.Model);
                writer.WriteString("prompt", configuration.Prompt);
                writer.WriteString("expectedContent", configuration.ExpectedContent);
                writer.WriteNumber("expectedPromptTokens", configuration.ExpectedPromptTokens);
                writer.WriteNumber("expectedCompletionTokens", configuration.ExpectedCompletionTokens);
            }
            writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    private static void WriteReport(Utf8JsonWriter writer, Configuration configuration, Phase measured)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 2);
        writer.WriteString("service", configuration.IsAdmin ? "admin" : "gateway");
        writer.WriteString("generatedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteStartArray("workloads");
        writer.WriteStartObject();
        writer.WriteString("id", configuration.Workload);
        writer.WriteString("method", configuration.IsAdmin ? "GET" : "POST");
        writer.WriteString("path", configuration.PathTemplate);
        writer.WriteBoolean("authenticated", true);
        writer.WriteString("inputFingerprint", Fingerprint(configuration));
        writer.WriteNumber("requested", measured.Requested);
        writer.WriteNumber("executed", measured.Executed);
        writer.WriteNumber("completed", measured.Completed);
        writer.WriteNumber("failures", measured.Failures);
        writer.WriteNumber("warmupRequests", configuration.WarmupRequests);
        writer.WriteNumber("concurrency", configuration.Concurrency);
        writer.WriteNumber("timeoutSeconds", configuration.TimeoutSeconds);
        writer.WriteNumber("elapsedMilliseconds", measured.Elapsed.TotalMilliseconds);
        writer.WriteNumber("throughputPerSecond", measured.Completed / measured.Elapsed.TotalSeconds);
        writer.WriteNumber("p50Milliseconds", Percentile(measured.Samples, 0.50));
        writer.WriteNumber("p95Milliseconds", Percentile(measured.Samples, 0.95));
        writer.WriteNumber("p99Milliseconds", Percentile(measured.Samples, 0.99));
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static double Percentile(long[] samples, double percentile) =>
        samples[Math.Clamp((int)Math.Ceiling(samples.Length * percentile) - 1, 0, samples.Length - 1)] *
        1000d / Stopwatch.Frequency;

    private static void WriteDiagnostics(Utf8JsonWriter writer, Configuration configuration, List<PhaseDiagnostic> phases)
    {
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", configuration.IsStream ? 2 : 1);
        writer.WriteString("kind", "conduit-native-benchmark-workload-diagnostics");
        writer.WriteString("scope", "client");
        writer.WriteString("service", configuration.IsAdmin ? "admin" : "gateway");
        writer.WriteString("workload", configuration.Workload);
        writer.WriteString("inputFingerprint", Fingerprint(configuration));
        writer.WriteNumber("stopwatchFrequency", Stopwatch.Frequency);
        writer.WriteNumber("runtimeTickFrequency", TimeSpan.TicksPerSecond);
        writer.WriteStartArray("phases");
        foreach (var phase in phases)
        {
            writer.WriteStartObject();
            writer.WriteString("name", phase.Name);
            writer.WriteNumber("requested", phase.Requested);
            writer.WriteNumber("executed", phase.Executed);
            writer.WriteNumber("completed", phase.Completed);
            writer.WriteNumber("failures", phase.Failures);
            writer.WriteNumber("elapsedTicks", phase.ElapsedTicks);
            writer.WriteNumber("startOffsetTicks", phase.StartOffsetTicks);
            writer.WriteNumber("endOffsetTicks", phase.EndOffsetTicks);
            WriteRuntime("runtimeBefore", phase.RuntimeBefore);
            WriteRuntime("runtimeAfter", phase.RuntimeAfter);
            writer.WriteStartArray("requests");
            foreach (var request in phase.Requests)
            {
                writer.WriteStartObject();
                writer.WriteNumber("index", request.Index);
                writer.WriteNumber("worker", request.Worker);
                writer.WriteString("traceId", request.TraceId);
                writer.WriteString("spanId", request.SpanId);
                writer.WriteNumber("startOffsetTicks", request.StartOffsetTicks);
                WriteNullable("headersOffsetTicks", request.HeadersOffsetTicks);
                WriteNullable("bodyOffsetTicks", request.BodyOffsetTicks);
                WriteNullable("validatedOffsetTicks", request.ValidatedOffsetTicks);
                writer.WriteNumber("endOffsetTicks", request.EndOffsetTicks);
                WriteNullable("responseBytes", request.ResponseBytes);
                writer.WriteBoolean("succeeded", request.Succeeded);
                if (request.Streaming is { } streaming)
                {
                    writer.WriteStartObject("streaming");
                    WriteNullable("firstBodyReadOffsetTicks", streaming.FirstBodyReadOffsetTicks);
                    WriteNullable("lastBodyReadOffsetTicks", streaming.LastBodyReadOffsetTicks);
                    WriteNullable("firstDataEventOffsetTicks", streaming.FirstDataEventOffsetTicks);
                    WriteNullable("doneEventOffsetTicks", streaming.DoneEventOffsetTicks);
                    writer.WriteNumber("bodyReadCount", streaming.BodyReadCount);
                    writer.WriteNumber("dataEventCount", streaming.DataEventCount);
                    writer.WriteNumber("doneEventCount", streaming.DoneEventCount);
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();

        void WriteNullable(string name, long? value)
        {
            if (value.HasValue) writer.WriteNumber(name, value.Value);
            else writer.WriteNull(name);
        }

        void WriteRuntime(string name, RuntimeSnapshot snapshot)
        {
            writer.WriteStartObject(name);
            writer.WriteNumber("cpuTimeTicks", snapshot.CpuTimeTicks);
            writer.WriteNumber("totalAllocatedBytes", snapshot.TotalAllocatedBytes);
            writer.WriteNumber("gcGen0", snapshot.GcGen0);
            writer.WriteNumber("gcGen1", snapshot.GcGen1);
            writer.WriteNumber("gcGen2", snapshot.GcGen2);
            writer.WriteNumber("gcPauseTicks", snapshot.GcPauseTicks);
            writer.WriteNumber("threadPoolThreads", snapshot.ThreadPoolThreads);
            writer.WriteNumber("threadPoolPending", snapshot.ThreadPoolPending);
            writer.WriteNumber("threadPoolCompleted", snapshot.ThreadPoolCompleted);
            writer.WriteNumber("minWorkerThreads", snapshot.MinWorkerThreads);
            writer.WriteNumber("maxWorkerThreads", snapshot.MaxWorkerThreads);
            writer.WriteNumber("availableWorkerThreads", snapshot.AvailableWorkerThreads);
            writer.WriteBoolean("serverGc", snapshot.ServerGc);
            writer.WriteEndObject();
        }
    }

    private sealed class RequestDiagnostic(int index, bool streaming)
    {
        internal int Index { get; } = index;
        internal int Worker { get; set; }
        internal string TraceId { get; } = NewTraceId();
        internal string SpanId { get; } = NewSpanId();
        internal long StartOffsetTicks { get; set; }
        internal long? HeadersOffsetTicks { get; set; }
        internal long? BodyOffsetTicks { get; set; }
        internal long? ValidatedOffsetTicks { get; set; }
        internal long EndOffsetTicks { get; set; }
        internal long? ResponseBytes { get; set; }
        internal bool Succeeded { get; set; }
        internal StreamingDiagnostic? Streaming { get; } = streaming ? new() : null;

        private static string NewTraceId()
        {
            ActivityTraceId id;
            do { id = ActivityTraceId.CreateRandom(); } while (id == default);
            return id.ToHexString();
        }

        private static string NewSpanId()
        {
            ActivitySpanId id;
            do { id = ActivitySpanId.CreateRandom(); } while (id == default);
            return id.ToHexString();
        }
    }

    /// <summary>
    /// Observes byte-read completion and SSE framing, without decoding or retaining payload.
    /// Events within one read share its observation timestamp. These are not wire-arrival
    /// times or content/usage validation; the complete original validator still runs at EOF.
    /// </summary>
    private sealed class StreamingDiagnostic
    {
        private int _lineLength;
        private bool _lineIsData = true;
        private int _lineDataLength;
        private bool _lineIsDone = true;
        private int _eventDataLength;
        private bool _eventIsDone = true;
        private bool _previousCr;

        internal long? FirstBodyReadOffsetTicks { get; private set; }
        internal long? LastBodyReadOffsetTicks { get; private set; }
        internal long? FirstDataEventOffsetTicks { get; private set; }
        internal long? DoneEventOffsetTicks { get; private set; }
        internal int BodyReadCount { get; private set; }
        internal int DataEventCount { get; private set; }
        internal int DoneEventCount { get; private set; }

        internal void Observe(ReadOnlySpan<byte> bytes, long timestamp)
        {
            FirstBodyReadOffsetTicks ??= timestamp;
            LastBodyReadOffsetTicks = timestamp;
            BodyReadCount++;
            foreach (var value in bytes)
            {
                if (_previousCr)
                {
                    _previousCr = false;
                    if (value == (byte)'\n') continue;
                }
                if (value is (byte)'\r' or (byte)'\n')
                {
                    EndLine(timestamp);
                    _previousCr = value == (byte)'\r';
                    continue;
                }
                if (_lineLength < 5)
                    _lineIsData &= value == "data:"u8[_lineLength];
                else if (_lineIsData && !(_lineLength == 5 && value == (byte)' '))
                {
                    _lineIsDone &= _lineDataLength < 6 && value == "[DONE]"u8[_lineDataLength];
                    _lineDataLength++;
                }
                _lineLength++;
            }
        }

        private void EndLine(long timestamp)
        {
            if (_lineLength == 0)
            {
                if (_eventDataLength != 0)
                {
                    DataEventCount++;
                    FirstDataEventOffsetTicks ??= timestamp;
                    if (_eventIsDone && _eventDataLength == 6)
                    {
                        DoneEventCount++;
                        DoneEventOffsetTicks ??= timestamp;
                    }
                }
                _eventDataLength = 0;
                _eventIsDone = true;
            }
            else if (_lineIsData && _lineLength >= 5)
            {
                if (_eventDataLength != 0)
                {
                    _eventDataLength++;
                    _eventIsDone = false;
                }
                else
                    _eventIsDone = _lineIsDone;
                _eventDataLength += _lineDataLength;
            }
            _lineLength = 0;
            _lineIsData = true;
            _lineDataLength = 0;
            _lineIsDone = true;
        }
    }

    private sealed record PhaseDiagnostic(
        string Name, int Requested, int Executed, int Completed, int Failures,
        long ElapsedTicks, long StartOffsetTicks, long EndOffsetTicks,
        RuntimeSnapshot RuntimeBefore, RuntimeSnapshot RuntimeAfter, RequestDiagnostic[] Requests);

    private sealed record RuntimeSnapshot(
        long CpuTimeTicks, long TotalAllocatedBytes, int GcGen0, int GcGen1, int GcGen2,
        long GcPauseTicks, int ThreadPoolThreads, long ThreadPoolPending, long ThreadPoolCompleted,
        int MinWorkerThreads, int MaxWorkerThreads, int AvailableWorkerThreads, bool ServerGc)
    {
        internal static RuntimeSnapshot Capture()
        {
            using var process = Process.GetCurrentProcess();
            var cpuTime = process.TotalProcessorTime.Ticks;
            ThreadPool.GetMinThreads(out var minimum, out _);
            ThreadPool.GetMaxThreads(out var maximum, out _);
            ThreadPool.GetAvailableThreads(out var available, out _);
            return new RuntimeSnapshot(cpuTime, GC.GetTotalAllocatedBytes(),
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                GC.GetTotalPauseDuration().Ticks, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount,
                ThreadPool.CompletedWorkItemCount, minimum, maximum, available, GCSettings.IsServerGC);
        }
    }

    private sealed record Configuration(
        string Workload, Uri Origin, int Requests, int Concurrency, int WarmupRequests, int TimeoutSeconds,
        string? SettingKey, string? ExpectedValue, string? Model, string? Prompt, string? ExpectedContent,
        int ExpectedPromptTokens, int ExpectedCompletionTokens)
    {
        internal bool IsAdmin => Workload == "admin-setting-read";
        internal bool IsStream => Workload == "gateway-chat-sse";
        internal string PathTemplate => IsAdmin ? "/v1/admin/global-settings/by-key/{key}" : "/v1/chat/completions";
    }

    private sealed record Phase(
        int Requested, int Executed, int Completed, int Failures, TimeSpan Elapsed, long[] Samples);
}
