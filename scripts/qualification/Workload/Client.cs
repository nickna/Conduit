using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Phase0;

public sealed class RequestResult
{
    public required string Id { get; init; }
    public bool Warmup { get; init; }
    public long RequestStartTicks { get; set; }
    public long HeadersTicks { get; set; }
    public long FirstBodyReadTicks { get; set; }
    public long FirstContentReadTicks { get; set; }
    public long ValidatedEndTicks { get; set; }
    public long CancelInitiatedTicks { get; set; }
    public int Status { get; set; }
    public long BytesRead { get; set; }
    public int BodyReads { get; set; }
    public int Events { get; set; }
    public int ContentChunks { get; set; }
    public int UsageEvents { get; set; }
    public int ToolArgumentFragments { get; set; }
    public string? RequestBillingId { get; set; }
    public bool Validated { get; set; }
    public string? Error { get; set; }
    public string? ErrorResponseBody { get; set; }
    public string? ErrorStack { get; set; }
    public string? LastSseEventType { get; set; }
    public string? LastSsePayloadPreview { get; set; }
    public JsonElement? Provider { get; set; }
    public double? TimeToFirstContentMilliseconds => FirstContentReadTicks == 0 ? null : Wire.Ms(FirstContentReadTicks - RequestStartTicks);
    public double FullyValidatedMilliseconds => Wire.Ms(ValidatedEndTicks - RequestStartTicks);
    public double? ProviderWriteBeginToClientContentMilliseconds { get; set; }
    public double? ProviderFlushEndToClientContentMilliseconds { get; set; }
    public double? CancellationToProviderObservedMilliseconds { get; set; }
}
public sealed record PhaseResult(string Name, double ElapsedMilliseconds, double ValidatedPerSecond, RequestResult[] Requests);
public sealed record WorkloadReport(int SchemaVersion, bool Passed, CaseConfig Configuration, long StopwatchFrequency, string ClockScope, string ContentTimestampScope, string CancellationScope, object Runtime, PhaseResult Warmup, PhaseResult Measured, object Summary, JsonElement ProviderObservations);

public static class Client
{
    public static async Task<WorkloadReport> RunAsync(CaseConfig config)
    {
        config.Validate();
        var apiKey = Environment.GetEnvironmentVariable("CONDUIT_EXT_BENCHMARK_API_KEY");
        Wire.Require(!string.IsNullOrWhiteSpace(apiKey), "CONDUIT_EXT_BENCHMARK_API_KEY missing");
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, MaxConnectionsPerServer = config.Concurrency, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var audio = config.Endpoint == "audio" ? Wire.Audio(config.InputBytes) : null;
        var audioSha = audio is null ? null : Wire.Sha256(audio);
        var warmup = await RunPhase(config, http, apiKey!, audio, "warmup", config.WarmupRequests);
        var measured = warmup.Requests.All(r => r.Validated) ? await RunPhase(config, http, apiKey!, audio, "measured", config.Requests) : new PhaseResult("measured", 0, 0, []);
        using var control = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
        JsonElement observations = default;
        var observationDeadline = Stopwatch.StartNew();
        var all = warmup.Requests.Concat(measured.Requests).ToArray();
        while (true)
        {
            var raw = await control.GetByteArrayAsync(config.ProviderUrl.TrimEnd('/') + "/observations?prefix=" + Uri.EscapeDataString(config.CaseId));
            Wire.Require(raw.Length <= 16 * 1024 * 1024, "Observation report exceeds bound");
            using var document = JsonDocument.Parse(raw);
            Wire.Require(document.RootElement.GetProperty("schemaVersion").GetInt32() == 1 && document.RootElement.GetProperty("stopwatchFrequency").GetInt64() == Stopwatch.Frequency, "Provider clock/schema mismatch");
            observations = document.RootElement.Clone();
            var entries = observations.GetProperty("observations").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, e => e);
            var settled = all.All(r => entries.TryGetValue(r.Id, out var o) && (config.Cancel == "none" ? o.GetProperty("completed").GetBoolean() : o.GetProperty("cancellationTicks").GetInt64() != 0));
            if (settled || observationDeadline.Elapsed.TotalSeconds > 7) break;
            await Task.Delay(50);
        }
        var byId = observations.GetProperty("observations").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!, e => e);
        foreach (var row in all)
        {
            try
            {
                Wire.Require(byId.TryGetValue(row.Id, out var provider), "No provider observation"); row.Provider = provider.Clone();
                long T(string name) => provider.GetProperty(name).GetInt64();
                Wire.Require(T("firstContentWriteBeginTicks") > 0 && row.FirstContentReadTicks > 0, "Missing first-content evidence");
                row.ProviderWriteBeginToClientContentMilliseconds = Wire.Ms(row.FirstContentReadTicks - T("firstContentWriteBeginTicks"));
                if (T("firstContentFlushEndTicks") > 0) row.ProviderFlushEndToClientContentMilliseconds = Wire.Ms(row.FirstContentReadTicks - T("firstContentFlushEndTicks"));
                if (config.Cancel == "none")
                {
                    Wire.Require(provider.GetProperty("completed").GetBoolean() && T("firstContentFlushEndTicks") > 0, "Provider did not complete");
                    if (config.Endpoint == "audio") Wire.Require(provider.GetProperty("uploadBytes").GetInt64() == config.InputBytes && provider.GetProperty("uploadSha256").GetString() == audioSha, "Upload exact bytes/hash differ");
                    else Wire.Require(T("usageWriteBeginTicks") > 0, "Provider usage not written");
                }
                else
                {
                    Wire.Require(row.CancelInitiatedTicks > 0 && T("cancellationTicks") > 0 && T("doneWriteBeginTicks") == 0 && !provider.GetProperty("completed").GetBoolean(), "Provider disconnect cancellation not established before DONE");
                    Wire.Require(config.Cancel == "before-usage" ? T("usageWriteBeginTicks") == 0 && row.UsageEvents == 0 : T("usageFlushEndTicks") > 0 && row.UsageEvents == 1, "Cancellation usage boundary mismatch");
                    row.CancellationToProviderObservedMilliseconds = Wire.Ms(T("cancellationTicks") - row.CancelInitiatedTicks);
                }
            }
            catch (Exception e) { row.Validated = false; row.Error = (row.Error is null ? "" : row.Error + "; ") + e.Message; }
        }
        var passed = warmup.Requests.Length == config.WarmupRequests && measured.Requests.Length == config.Requests && all.All(r => r.Validated) && byId.Count == all.Length;
        var accepted = measured.Requests.Where(r => r.Validated).ToArray();
        object Distribution(IEnumerable<double> source) { var a = source.Order().ToArray(); return new { samples = a.Length, p50 = Q(a, .5), p95 = Q(a, .95), p99 = Q(a, .99), minimum = a.Length == 0 ? (double?)null : a[0], maximum = a.Length == 0 ? (double?)null : a[^1] }; }
        var summary = new { measuredRequests = measured.Requests.Length, validated = accepted.Length, failures = measured.Requests.Count(r => !r.Validated), validatedPerSecond = measured.ElapsedMilliseconds > 0 ? accepted.Length * 1000d / measured.ElapsedMilliseconds : 0, firstContentMilliseconds = Distribution(accepted.Where(r => r.TimeToFirstContentMilliseconds.HasValue).Select(r => r.TimeToFirstContentMilliseconds!.Value)), fullyValidatedMilliseconds = Distribution(accepted.Select(r => r.FullyValidatedMilliseconds)), providerWriteBeginToClientContentMilliseconds = Distribution(accepted.Where(r => r.ProviderWriteBeginToClientContentMilliseconds.HasValue).Select(r => r.ProviderWriteBeginToClientContentMilliseconds!.Value)), cancellationToProviderObservedMilliseconds = Distribution(accepted.Where(r => r.CancellationToProviderObservedMilliseconds.HasValue).Select(r => r.CancellationToProviderObservedMilliseconds!.Value)), totalBytes = measured.Requests.Sum(r => r.BytesRead) };
        return new(1, passed, config, Stopwatch.Frequency, "Raw Stopwatch ticks; cross-process deltas require provider and client on the same OS/kernel monotonic clock. Root orchestration must retain that topology.", "Client firstContentReadTicks is captured when the last byte of the unique ASCII M[requestId] marker is returned by HTTP content ReadAsync, before parsing; provider records write-begin and after awaited FlushAsync separately. Flush-end-to-client may be negative because a client can read before provider flush continuation resumes.", "Cancelled rows measure validated cancellation trigger, then require observed upstream disconnect and exact before/after-usage boundary. FullyValidatedMilliseconds ends at client cancellation disposal, not later server accounting completion.", new { runtime = Environment.Version.ToString(), os = System.Runtime.InteropServices.RuntimeInformation.OSDescription, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), synthetic = true, syntheticUsage = "chat10input/5output; audio1.25seconds independent of text/chunk/uploadsize" }, warmup, measured, summary, observations);
    }
    private static double? Q(double[] values, double q) => values.Length == 0 ? null : values[Math.Clamp((int)Math.Ceiling(values.Length * q) - 1, 0, values.Length - 1)];

    private static async Task<PhaseResult> RunPhase(CaseConfig c, HttpClient http, string apiKey, byte[]? audio, string phase, int count)
    {
        var items = Enumerable.Range(0, count).Select(i =>
        {
            var spec = Spec.For(c, c.CaseId + (phase == "warmup" ? "-w" : "-m") + i.ToString("D4"));
            return (Spec: spec, Expected: c.Endpoint == "audio" ? Wire.Marker(spec) + " audio café 雪" : Wire.Text(spec), Row: new RequestResult { Id = spec.Id, Warmup = phase == "warmup" });
        }).ToArray();
        var next = -1;
        var clock = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, Math.Min(c.Concurrency, count)).Select(async _ =>
        {
            while (true)
            {
                var i = Interlocked.Increment(ref next); if (i >= count) return;
                await Execute(c, http, apiKey, audio, items[i].Spec, items[i].Expected, items[i].Row);
            }
        }));
        clock.Stop();
        return new(phase, clock.Elapsed.TotalMilliseconds, clock.Elapsed.TotalSeconds > 0 ? items.Count(i => i.Row.Validated) / clock.Elapsed.TotalSeconds : 0, items.Select(i => i.Row).ToArray());
    }

    private static async Task Execute(CaseConfig c, HttpClient http, string apiKey, byte[]? audio, Spec spec, string expected, RequestResult row)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(c.TimeoutSeconds));
        HttpResponseMessage? response = null;
        try
        {
            using var request = CreateRequest(c, spec, audio);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.TryAddWithoutValidation("X-Phase0-Case-Id", spec.Id);
            request.Version = HttpVersion.Version11; request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            row.RequestStartTicks = Stopwatch.GetTimestamp();
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            row.HeadersTicks = Stopwatch.GetTimestamp(); row.Status = (int)response.StatusCode;
            row.RequestBillingId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.Single() : null;
            if(response.StatusCode!=HttpStatusCode.OK)
            {
                await using var errorStream=await response.Content.ReadAsStreamAsync(deadline.Token);
                var errorBuffer=new byte[8192];var used=0;
                while(used<errorBuffer.Length){var read=await errorStream.ReadAsync(errorBuffer.AsMemory(used),deadline.Token);if(read==0)break;used+=read;}
                row.ErrorResponseBody=Encoding.UTF8.GetString(errorBuffer,0,used);
                row.BytesRead=used;
                throw new InvalidDataException("HTTP status "+row.Status+"; bounded error body retained");
            }
            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token);
            await using var paced = new ObservedStream(source, row, Wire.Marker(spec), c.SlowReadBytes, c.SlowReadDelayMilliseconds, c.MaxResponseBytes);
            var isSse = c.Endpoint.EndsWith("sse", StringComparison.Ordinal);
            Wire.Require(response.Content.Headers.ContentType?.MediaType == (isSse ? "text/event-stream" : "application/json"), "Unexpected response media type");
            if (isSse)
            {
                using var reader = new StreamReader(paced, new UTF8Encoding(false, true), false, 1024, true);
                await ValidateSse(c, spec, expected, row, reader, deadline.Token);
                if (c.Cancel != "none") { row.CancelInitiatedTicks = Stopwatch.GetTimestamp(); deadline.Cancel(); response.Dispose(); }
            }
            else
            {
                using var bytes = new MemoryStream(); await paced.CopyToAsync(bytes, deadline.Token);
                using var document = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)));
                if (c.Endpoint == "audio")
                {
                    var root = document.RootElement; Wire.Require(root.GetProperty("text").GetString() == expected && root.GetProperty("duration").GetDouble() == 1.25 && root.GetProperty("language").GetString() == "en" && root.GetProperty("task").GetString() == "transcribe", "Audio response mismatch");
                }
                else if(c.Endpoint == "chat-json")
                {
                    var root=document.RootElement;Wire.Require(root.GetProperty("object").GetString()=="chat.completion" && root.GetProperty("model").GetString()==c.Model,"Chat JSON discriminator/model mismatch");
                    var choices=root.GetProperty("choices");Wire.Require(choices.GetArrayLength()==1 && choices[0].GetProperty("index").GetInt32()==0 && choices[0].GetProperty("finish_reason").GetString()=="stop","Chat JSON choice mismatch");
                    var message=choices[0].GetProperty("message");Wire.Require(message.GetProperty("role").GetString()=="assistant" && message.GetProperty("content").GetString()==expected,"Chat JSON message mismatch");ValidateUsage(root.GetProperty("usage"),false);
                }
                else ValidateResponse(document.RootElement, expected, c.Model);
            }
            Wire.Require(row.FirstContentReadTicks > 0, "No first content marker found"); row.Validated = true;
        }
        catch (Exception e) { row.Error = e.GetType().Name + ": " + e.Message; row.ErrorStack=e.ToString(); row.Validated = false; }
        finally { response?.Dispose(); row.ValidatedEndTicks = Stopwatch.GetTimestamp(); }
    }

    private static HttpRequestMessage CreateRequest(CaseConfig c, Spec spec, byte[]? audio)
    {
        var prompt = Wire.Encode(spec);
        var path = c.Endpoint switch { "chat-json" or "standard-sse" => "/v1/chat/completions", "enhanced-sse" => "/v1/conduit/chat/completions", "responses-json" or "responses-sse" => "/v1/responses", "audio" => "/v1/audio/transcriptions", _ => throw new InvalidDataException() };
        var request = new HttpRequestMessage(HttpMethod.Post, c.BaseUrl.TrimEnd('/') + path);
        if (c.Endpoint == "audio")
        {
            var form = new MultipartFormDataContent(); var file = new ByteArrayContent(audio!); file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", spec.Id + ".wav"); form.Add(new StringContent(c.Model), "model"); form.Add(new StringContent(prompt), "prompt"); form.Add(new StringContent("json"), "response_format"); request.Content = form;
        }
        else
        {
            object body;
            if (c.Endpoint.StartsWith("responses-", StringComparison.Ordinal)) body = new { model = c.Model, input = prompt, stream = c.Endpoint == "responses-sse",store=false };
            else
            {
                var chat = new Dictionary<string, object?> { ["model"] = c.Model, ["messages"] = new[] { new { role = "user", content = prompt } }, ["stream"] = c.Endpoint != "chat-json", ["enable_agentic_mode"] = false };
                if(c.Endpoint!="chat-json")chat["stream_options"]=new { include_usage=true };
                if (c.ToolCalls) chat["tools"] = new[] { new { type = "function", function = new { name = "phase0_fragmented", description = "Synthetic manual tool call fixture", parameters = new { type = "object", properties = new { case_id = new { type = "string" }, value = new { type = "integer" } }, required = new[] { "case_id", "value" } } } } };
                body = chat;
            }
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, Wire.Json)); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        return request;
    }

    private static async Task ValidateSse(CaseConfig c, Spec spec, string expected, RequestResult row, StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); var toolArguments = new StringBuilder(); var toolId = ""; var toolName = "";
        var data = new StringBuilder(); var eventType = ""; var done = false; var stopped = false; var role = false; var metricsFinal = 0; var sequence = 0;
        var responseEvents = new List<string>(); string? chunkId = null; string? chunkModel = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(token);
            if (line is null) break;
            Wire.Require(line.Length <= 65536, "SSE line bound exceeded");
            if (line.Length != 0)
            {
                if (line.StartsWith(':')) continue;
                if (line.StartsWith("event:", StringComparison.Ordinal)) { Wire.Require(eventType.Length == 0, "Duplicate SSE event type"); eventType = line[6..].Trim(); continue; }
                Wire.Require(line.StartsWith("data:", StringComparison.Ordinal), "Unsupported SSE field");
                if (data.Length > 0) data.Append('\n'); data.Append(line[5..].TrimStart()); continue;
            }
            if (data.Length == 0) { eventType = ""; continue; }
            row.Events++;
            var payload = data.ToString(); data.Clear();
            row.LastSseEventType=eventType;row.LastSsePayloadPreview=payload[..Math.Min(payload.Length,4096)];
            Wire.Require(!done, "Event after terminal sentinel");
            if (payload == "[DONE]") { Wire.Require(c.Endpoint != "responses-sse" && eventType.Length == 0, "Unexpected DONE framing"); done = true; eventType = ""; continue; }
            using var document = JsonDocument.Parse(payload); var root = document.RootElement;
            if (root.TryGetProperty("error", out var error)) Wire.Require(error.ValueKind == JsonValueKind.Null, "SSE error event");
            if (c.Endpoint == "responses-sse")
            {
                Wire.Require(eventType == root.GetProperty("type").GetString() && root.GetProperty("sequence_number").GetInt32() == ++sequence, "Responses event type/sequence mismatch"); responseEvents.Add(eventType);
                if (eventType == "response.output_text.delta") { text.Append(root.GetProperty("delta").GetString()); row.ContentChunks++; }
                else if (eventType == "response.output_text.done") Wire.Require(root.GetProperty("text").GetString() == expected, "Responses text.done differs");
                else if (eventType == "response.completed") { ValidateResponse(root.GetProperty("response"), expected, c.Model); row.UsageEvents++; stopped = true; }
                else Wire.Require(eventType is "response.created" or "response.in_progress" or "response.output_item.added" or "response.content_part.added" or "response.content_part.done" or "response.output_item.done", "Unsupported Responses event");
            }
            else if (eventType.Length > 0)
            {
                Wire.Require(c.Endpoint == "enhanced-sse" && eventType is "metrics" or "metrics-final", "Unexpected named chat event");
                if (eventType == "metrics-final") metricsFinal++;
            }
            else
            {
                Wire.Require(root.GetProperty("object").GetString() == "chat.completion.chunk", "Chat chunk discriminator mismatch");
                var id = root.GetProperty("id").GetString(); var model = root.GetProperty("model").GetString(); chunkId ??= id; chunkModel ??= model;
                Wire.Require(!string.IsNullOrEmpty(id) && chunkId == id && chunkModel == model && model == c.Model, "Chat chunk identity/model changed");
                var choices = root.GetProperty("choices"); Wire.Require(choices.GetArrayLength() <= 1, "Multiple choices unexpected");
                foreach (var choice in choices.EnumerateArray())
                {
                    Wire.Require(!stopped && choice.GetProperty("index").GetInt32() == 0, "Invalid choice ordering/index");
                    var delta = choice.GetProperty("delta");
                    if (delta.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String) { Wire.Require(r.GetString() == "assistant", "Invalid role"); role = true; }
                    if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } value) { text.Append(value); row.ContentChunks++; }
                    if (delta.TryGetProperty("tool_calls", out var tools) && tools.ValueKind == JsonValueKind.Array)
                        foreach (var tool in tools.EnumerateArray())
                        {
                            Wire.Require(c.ToolCalls && tool.GetProperty("index").GetInt32() == 0, "Unexpected tool/index");
                            if (tool.TryGetProperty("id", out var ti) && ti.ValueKind == JsonValueKind.String) { Wire.Require(toolId.Length == 0 || toolId == ti.GetString(), "Tool ID changed"); toolId = ti.GetString()!; }
                            if (tool.TryGetProperty("type", out var tt) && tt.ValueKind == JsonValueKind.String) Wire.Require(tt.GetString() == "function", "Tool type mismatch");
                            var f = tool.GetProperty("function");
                            if (f.TryGetProperty("name", out var tn) && tn.ValueKind == JsonValueKind.String && tn.GetString() is { Length: > 0 } name) { Wire.Require(toolName.Length == 0 || toolName == name, "Tool name changed"); toolName = name; }
                            if (f.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String && args.GetString() is { Length: > 0 } fragment) { toolArguments.Append(fragment); row.ToolArgumentFragments++; }
                        }
                    if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String) { Wire.Require(finish.GetString() == (c.ToolCalls ? "tool_calls" : "stop"), "Finish reason differs"); stopped = true; }
                }
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object) { ValidateUsage(usage, false); Wire.Require(stopped && choices.GetArrayLength() == 0, "Usage must follow finish with empty choices"); row.UsageEvents++; }
            }
            eventType = "";
            if (c.Cancel == "before-usage" && row.ContentChunks >= c.CancelAfterContentChunks) { Wire.Require(row.UsageEvents == 0, "Usage preceded cancellation boundary"); return; }
            if (c.Cancel == "after-usage" && row.UsageEvents == 1) return;
        }
        Wire.Require(data.Length == 0 && c.Cancel == "none" && stopped && row.ContentChunks == c.Chunks && text.ToString() == expected && row.UsageEvents == 1, "Incomplete/text/usage mismatch");
        if (c.Endpoint == "responses-sse")
        {
            var exact = new[] { "response.created", "response.in_progress", "response.output_item.added", "response.content_part.added" }.Concat(Enumerable.Repeat("response.output_text.delta", c.Chunks)).Concat(new[] { "response.output_text.done", "response.content_part.done", "response.output_item.done", "response.completed" });
            Wire.Require(responseEvents.SequenceEqual(exact), "Responses pinned event order differs");
        }
        else
        {
            Wire.Require(done && role && (c.Endpoint != "enhanced-sse" || metricsFinal == 1), "Missing DONE/role/enhanced final metrics");
            if (c.ToolCalls) Wire.Require(toolId == "call_" + spec.Id && toolName == "phase0_fragmented" && row.ToolArgumentFragments == 3 && toolArguments.ToString() == Wire.ExpectedToolArguments(spec), "Tool fragments did not survive exactly");
            else Wire.Require(row.ToolArgumentFragments == 0, "Unexpected tool fragments");
        }
    }
    private static void ValidateResponse(JsonElement root, string expected, string model)
    {
        Wire.Require(root.GetProperty("object").GetString() == "response" && root.GetProperty("status").GetString() == "completed" && root.GetProperty("model").GetString() == model && (!root.TryGetProperty("error",out var error)||error.ValueKind == JsonValueKind.Null), "Response object status/model/error mismatch");
        var output = root.GetProperty("output"); Wire.Require(output.GetArrayLength() == 1, "Responses output count differs");
        var message = output[0]; Wire.Require(message.GetProperty("type").GetString() == "message" && message.GetProperty("role").GetString() == "assistant" && message.GetProperty("status").GetString() == "completed", "Responses output message differs");
        var content = message.GetProperty("content"); Wire.Require(content.GetArrayLength() == 1 && content[0].GetProperty("type").GetString() == "output_text" && content[0].GetProperty("text").GetString() == expected && root.GetProperty("output_text").GetString() == expected, "Responses output text differs"); ValidateUsage(root.GetProperty("usage"), true);
    }
    private static void ValidateUsage(JsonElement usage, bool response) => Wire.Require(usage.GetProperty(response ? "input_tokens" : "prompt_tokens").GetInt32() == 10 && usage.GetProperty(response ? "output_tokens" : "completion_tokens").GetInt32() == 5 && usage.GetProperty("total_tokens").GetInt32() == 15, "Usage token values differ");
}

internal sealed class ObservedStream(Stream inner, RequestResult row, string marker, int maximumRead, int delayMs, int maximumBytes) : Stream
{
    private readonly byte[] _marker = Encoding.ASCII.GetBytes(marker);
    private int _matched;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (delayMs > 0) await Task.Delay(delayMs, cancellationToken);
        var count = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximumRead)], cancellationToken);
        var now = Stopwatch.GetTimestamp();
        if (count > 0)
        {
            row.FirstBodyReadTicks = row.FirstBodyReadTicks == 0 ? now : row.FirstBodyReadTicks; row.BytesRead += count; row.BodyReads++;
            Wire.Require(row.BytesRead <= maximumBytes, "Response byte limit exceeded");
            if (row.FirstContentReadTicks == 0)
                for (var i = 0; i < count; i++)
                {
                    var value = buffer.Span[i]; _matched = value == _marker[_matched] ? _matched + 1 : value == _marker[0] ? 1 : 0;
                    if (_matched == _marker.Length) { row.FirstContentReadTicks = now; break; }
                }
        }
        return count;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false; public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
