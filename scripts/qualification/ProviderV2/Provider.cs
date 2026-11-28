using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace Phase0;

public static class Provider
{
    private static readonly ConcurrentDictionary<string, Observation> Observations = new(StringComparer.Ordinal);
    public static async Task RunAsync(int port)
    {
        Wire.Require(port is >= 1024 and <= 65535, "Invalid provider port");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 20 * 1024 * 1024);
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 16 * 1024 * 1024);
        await using var app = builder.Build();
        app.MapGet("/health", () => Results.Json(new { schemaVersion = 1, service = "phase0-extended-provider", frequency = Stopwatch.Frequency }));
        app.MapGet("/identity", () => {
            var assembly=Assembly.GetExecutingAssembly();
            var sourceSdk=assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a=>a.Key=="SourceSdk").Value;
            return Results.Json(new { schemaVersion=1,providerObservationVersion=2,processId=Environment.ProcessId,actualRuntime=Environment.Version.ToString(),architecture=RuntimeInformation.ProcessArchitecture.ToString(),
                sourceSdk,assemblySha256=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
                scope="Same fixed net10 ProviderV2 for all source cohorts; cancellation fallback observes the public aborted token at catch, not cancellation initiation." });
        });
        app.MapGet("/observations", (string prefix) =>
        {
            Wire.Require(prefix.Length is >= 1 and <= 100, "Invalid observation prefix");
            return Results.Json(new { schemaVersion = 1, providerObservationVersion = 2, stopwatchFrequency = Stopwatch.Frequency, observations = Observations.Where(kv => kv.Key.Length == prefix.Length + 6 && (kv.Key.StartsWith(prefix + "-m", StringComparison.Ordinal) || kv.Key.StartsWith(prefix + "-w", StringComparison.Ordinal))).OrderBy(kv => kv.Key).Select(kv => kv.Value.CreateSnapshot()).ToArray() }, Wire.Json);
        });
        app.MapPost("/{**path}", async context =>
        {
            if (context.Request.Headers.Authorization != "Bearer native-provider-key") { context.Response.StatusCode = 401; return; }
            Observation? observed = null;
            try
            {
                var path = context.Request.Path.Value ?? "";
                if (path.EndsWith("/audio/transcriptions", StringComparison.Ordinal))
                {
                    var uploadEntry = Stopwatch.GetTimestamp();
                    var form = await context.Request.ReadFormAsync(context.RequestAborted);
                    var spec = Wire.Decode(form["prompt"].ToString());
                    observed = NewObservation(spec);
                    observed.UploadEntryTicks = uploadEntry;
                    observed.UploadTraceParent = context.Request.Headers["traceparent"].ToString();
                    using var registration = context.RequestAborted.Register(observed.CancellationRecorder.RecordCallback);
                    var file = form.Files.GetFile("file") ?? throw new InvalidDataException("Missing synthetic audio file");
                    Wire.Require(file.Length == spec.InputBytes, "Provider upload length mismatch");
                    observed.UploadBytes = file.Length;
                    await using (var input = file.OpenReadStream()) observed.UploadSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, context.RequestAborted));
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(new { text = Wire.Marker(spec) + " audio café 雪", language = "en", duration = 1.25, task = "transcribe" }, Wire.Json);
                    context.Response.ContentType = "application/json";
                    observed.FirstContentWriteBeginTicks = Stopwatch.GetTimestamp();
                    await context.Response.Body.WriteAsync(bytes, context.RequestAborted); await context.Response.Body.FlushAsync(context.RequestAborted);
                    observed.FirstContentFlushEndTicks = observed.LastFlushEndTicks = Stopwatch.GetTimestamp(); observed.BytesFlushed += bytes.Length;
                    observed.Completed = true;
                    return;
                }
                Wire.Require(path.EndsWith("/chat/completions", StringComparison.Ordinal), "Unsupported provider route");
                using var request = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                var root = request.RootElement;
                var prompt = root.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content")).Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()!).FirstOrDefault(c => c.StartsWith(Wire.Prefix, StringComparison.Ordinal)) ?? throw new InvalidDataException("Spec did not reach provider");
                var scenario = Wire.Decode(prompt);
                observed = NewObservation(scenario);
                using var aborted = context.RequestAborted.Register(observed.CancellationRecorder.RecordCallback);
                var streaming = root.TryGetProperty("stream", out var streamFlag) && streamFlag.ValueKind == JsonValueKind.True;
                if (!streaming)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id = "chatcmpl_" + scenario.Id, @object = "chat.completion", created = 1760000000, model = "native-aot-provider-model", choices = new[] { new { index = 0, message = new { role = "assistant", content = Wire.Text(scenario) }, finish_reason = "stop" } }, usage = Wire.Usage }, Wire.Json);
                    context.Response.ContentType = "application/json";
                    observed.FirstContentWriteBeginTicks = observed.UsageWriteBeginTicks = Stopwatch.GetTimestamp();
                    await context.Response.Body.WriteAsync(bytes, context.RequestAborted); await context.Response.Body.FlushAsync(context.RequestAborted);
                    observed.FirstContentFlushEndTicks = observed.UsageFlushEndTicks = observed.LastFlushEndTicks = Stopwatch.GetTimestamp(); observed.BytesFlushed = bytes.Length;
                    observed.Completed = true;
                    return;
                }
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache, no-transform";
                observed.RoleWriteBeginTicks = Stopwatch.GetTimestamp();
                await WriteFrame(context, observed, Chunk(scenario, new { role = "assistant" }));
                for (var index = 0; index < scenario.Chunks; index++)
                {
                    context.RequestAborted.ThrowIfCancellationRequested();
                    var delta = new Dictionary<string, object?> { ["content"] = Wire.Piece(scenario, index) };
                    if (scenario.Tools && index < 3)
                    {
                        var function = new Dictionary<string, object?> { ["arguments"] = index switch { 0 => "{\"case_id\":\"", 1 => scenario.Id, _ => "\",\"value\":42}" } };
                        var call = new Dictionary<string, object?> { ["index"] = 0, ["function"] = function };
                        if (index == 0) { call["id"] = "call_" + scenario.Id; call["type"] = "function"; function["name"] = "phase0_fragmented"; }
                        delta["tool_calls"] = new[] { call };
                    }
                    await WriteFrame(context, observed, Chunk(scenario, delta), firstContent: index == 0);
                    observed.ContentChunksFlushed++;
                    if (scenario.GapMs > 0) await Task.Delay(scenario.GapMs, context.RequestAborted);
                    if (scenario.BurstSize > 0 && (index + 1) % scenario.BurstSize == 0 && index + 1 < scenario.Chunks && scenario.BurstPauseMs > 0) await Task.Delay(scenario.BurstPauseMs, context.RequestAborted);
                }
                if (scenario.Cancel == "before-usage") await Task.Delay(5000, context.RequestAborted);
                await WriteFrame(context, observed, Chunk(scenario, new { }, scenario.Tools ? "tool_calls" : "stop"));
                observed.UsageWriteBeginTicks = Stopwatch.GetTimestamp();
                await WriteFrame(context, observed, new { id = "chatcmpl_" + scenario.Id, @object = "chat.completion.chunk", created = 1760000000, model = "native-aot-provider-model", choices = Array.Empty<object>(), usage = Wire.Usage });
                observed.UsageFlushEndTicks = observed.LastFlushEndTicks;
                if (scenario.Cancel == "after-usage") await Task.Delay(5000, context.RequestAborted);
                observed.DoneWriteBeginTicks = Stopwatch.GetTimestamp();
                await WriteRaw(context, observed, "data: [DONE]\n\n");
                observed.Completed = true;
            }
            catch (Exception e) when (e is OperationCanceledException or IOException)
            {
                if (observed is not null) { observed.CancellationRecorder.ObserveAtCatch(context.RequestAborted); observed.Error = e.GetType().Name; }
            }
            catch (Exception e)
            {
                if (observed is not null) observed.Error = e.GetType().Name + ": " + e.Message;
                if (!context.Response.HasStarted) { context.Response.StatusCode = 400; await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = e.Message }, Wire.Json)); }
                else context.Abort();
            }
            finally { if (observed is not null) observed.PublishFinal(); }
        });
        await app.RunAsync();
    }
    private static Observation NewObservation(Spec spec)
    {
        Wire.Require(Observations.Count < 100000, "Provider observation bound reached");
        var value = new Observation { Id = spec.Id };
        Wire.Require(Observations.TryAdd(spec.Id, value), "Request ID reused; use unique caseId per invocation");
        return value;
    }
    private static object Chunk(Spec s, object delta, string? finish = null) => new { id = "chatcmpl_" + s.Id, @object = "chat.completion.chunk", created = 1760000000, model = "native-aot-provider-model", choices = new[] { new { index = 0, delta, finish_reason = finish } } };
    private static Task WriteFrame(HttpContext c, Observation o, object data, bool firstContent=false) => WriteRaw(c, o, "data: " + JsonSerializer.Serialize(data, Wire.Json) + "\n\n",firstContent);
    private static async Task WriteRaw(HttpContext c, Observation o, string data, bool firstContent=false)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        if(firstContent)o.FirstContentWriteBeginTicks=Stopwatch.GetTimestamp();
        await c.Response.Body.WriteAsync(bytes, c.RequestAborted); await c.Response.Body.FlushAsync(c.RequestAborted);
        o.LastFlushEndTicks = Stopwatch.GetTimestamp(); o.BytesFlushed += bytes.Length;
        if(firstContent)o.FirstContentFlushEndTicks=o.LastFlushEndTicks;
    }
}
