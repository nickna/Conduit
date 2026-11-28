using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Phase0;

public sealed record CaseConfig
{
    public required string CaseId { get; init; }
    public string BaseUrl { get; init; } = "http://127.0.0.1:18080";
    public string ProviderUrl { get; init; } = "http://127.0.0.1:18083";
    public string Model { get; init; } = "native-aot-model";
    public string Endpoint { get; init; } = "standard-sse";
    public int Requests { get; init; } = 100;
    public int WarmupRequests { get; init; } = 20;
    public int Concurrency { get; init; } = 4;
    public int Chunks { get; init; } = 10;
    public int ChunkCharacters { get; init; } = 32;
    public int GapMilliseconds { get; init; }
    public int BurstSize { get; init; }
    public int BurstPauseMilliseconds { get; init; }
    public bool ToolCalls { get; init; }
    public int SlowReadBytes { get; init; } = 16384;
    public int SlowReadDelayMilliseconds { get; init; }
    public string Cancel { get; init; } = "none";
    public int CancelAfterContentChunks { get; init; } = 3;
    public int InputBytes { get; init; } = 1048576;
    public int TimeoutSeconds { get; init; } = 120;
    public int MaxResponseBytes { get; init; } = 33554432;

    public void Validate()
    {
        Wire.Require(Regex.IsMatch(CaseId, "^[a-zA-Z0-9_-]{1,80}$"), "Invalid caseId");
        foreach (var address in new[] { BaseUrl, ProviderUrl })
            Wire.Require(Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Scheme == "http", "Fixture URLs must be loopback HTTP");
        Wire.Require(new[] { "chat-json", "standard-sse", "enhanced-sse", "responses-json", "responses-sse", "audio" }.Contains(Endpoint), "Unsupported endpoint");
        Wire.Require(Requests is >= 1 and <= 1000 && WarmupRequests is >= 0 and <= 100 && Concurrency is >= 1 and <= 16 && Concurrency <= Requests, "Invalid request bounds");
        Wire.Require(Chunks is >= 1 and <= 10000 && ChunkCharacters is >= 1 and <= 1024 && GapMilliseconds is >= 0 and <= 100 && BurstSize is >= 0 and <= 10000 && BurstPauseMilliseconds is >= 0 and <= 1000, "Invalid provider pacing bounds");
        Wire.Require(SlowReadBytes is >= 1 and <= 65536 && SlowReadDelayMilliseconds is >= 0 and <= 100 && TimeoutSeconds is >= 5 and <= 180 && MaxResponseBytes is >= 1024 and <= 67108864, "Invalid transport bounds");
        Wire.Require(Cancel is "none" or "before-usage" or "after-usage", "Invalid cancellation mode");
        Wire.Require(Cancel == "none" || (Endpoint == "enhanced-sse" && !ToolCalls && CancelAfterContentChunks >= 1 && CancelAfterContentChunks <= Chunks), "Cancellation requires enhanced SSE and valid content count");
        Wire.Require(!ToolCalls || (Chunks >= 3 && Endpoint is "standard-sse" or "enhanced-sse"), "Tools require at least3chat chunks");
        Wire.Require(InputBytes is >= 44 and <= 8388608, "Audio fixture must be44bytes..8MiB");
    }
}

public sealed record Spec(string Id, int Chunks, int Characters, int GapMs, int BurstSize, int BurstPauseMs, bool Tools, string Cancel, int InputBytes)
{
    public static Spec For(CaseConfig c, string id) => new(id, c.Chunks, c.ChunkCharacters, c.GapMilliseconds, c.BurstSize, c.BurstPauseMilliseconds, c.ToolCalls, c.Cancel, c.InputBytes);
}

public sealed class Observation
{
    public required string Id { get; init; }
    public long RequestAcceptedTicks { get; init; } = Stopwatch.GetTimestamp();
    public long RoleWriteBeginTicks { get; set; }
    public long FirstContentWriteBeginTicks { get; set; }
    public long FirstContentFlushEndTicks { get; set; }
    public long UsageWriteBeginTicks { get; set; }
    public long UsageFlushEndTicks { get; set; }
    public long DoneWriteBeginTicks { get; set; }
    public long LastFlushEndTicks { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public CancellationEvidence CancellationRecorder { get; } = new();
    private long _handlerEndTicks;
    public void PublishFinal() => Volatile.Write(ref _handlerEndTicks, Stopwatch.GetTimestamp());
    public int ContentChunksFlushed { get; set; }
    public long BytesFlushed { get; set; }
    public long UploadEntryTicks { get; set; }
    public string? UploadTraceParent { get; set; }
    public long UploadBytes { get; set; }
    public string? UploadSha256 { get; set; }
    public bool Completed { get; set; }
    public string? Error { get; set; }
    public ObservationSnapshot CreateSnapshot()
    {
        // One acquire read decides progress versus final. A previously captured
        // progress DTO can never become settled during JSON serialization.
        var ended = Volatile.Read(ref _handlerEndTicks);
        if (ended == 0)
            return new() { Id = Id, RequestAcceptedTicks = RequestAcceptedTicks };
        var cancellation = CancellationRecorder.Capture();
        return new()
        {
            Id = Id, RequestAcceptedTicks = RequestAcceptedTicks, RoleWriteBeginTicks = RoleWriteBeginTicks,
            FirstContentWriteBeginTicks = FirstContentWriteBeginTicks, FirstContentFlushEndTicks = FirstContentFlushEndTicks,
            UsageWriteBeginTicks = UsageWriteBeginTicks, UsageFlushEndTicks = UsageFlushEndTicks,
            DoneWriteBeginTicks = DoneWriteBeginTicks, LastFlushEndTicks = LastFlushEndTicks,
            HandlerEndTicks = ended, ContentChunksFlushed = ContentChunksFlushed, BytesFlushed = BytesFlushed,
            UploadEntryTicks = UploadEntryTicks, UploadTraceParent = UploadTraceParent, UploadBytes = UploadBytes, UploadSha256 = UploadSha256, Completed = Completed, Error = Error,
            CancellationTicks = cancellation.ObservedTicks, CancellationEvidence = cancellation
        };
    }
}

public sealed record ObservationSnapshot
{
    public required string Id { get; init; }
    public long RequestAcceptedTicks { get; init; }
    public long RoleWriteBeginTicks { get; init; }
    public long FirstContentWriteBeginTicks { get; init; }
    public long FirstContentFlushEndTicks { get; init; }
    public long UsageWriteBeginTicks { get; init; }
    public long UsageFlushEndTicks { get; init; }
    public long DoneWriteBeginTicks { get; init; }
    public long LastFlushEndTicks { get; init; }
    public long HandlerEndTicks { get; init; }
    public int ContentChunksFlushed { get; init; }
    public long BytesFlushed { get; init; }
    public long UploadEntryTicks { get; init; }
    public string? UploadTraceParent { get; init; }
    public long UploadBytes { get; init; }
    public string? UploadSha256 { get; init; }
    public bool Completed { get; init; }
    public string? Error { get; init; }
    public long CancellationTicks { get; init; }
    public int ProviderObservationVersion => 2;
    public CancellationSnapshot CancellationEvidence { get; init; }
}

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    public const string Prefix = "PHASE0_EXT:";
    public static void Require(bool passed, string error) { if (!passed) throw new InvalidDataException(error); }
    public static string Encode(Spec spec) => Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(spec, Json));
    public static Spec Decode(string value)
    {
        Require(value.StartsWith(Prefix, StringComparison.Ordinal), "Missing scenario spec");
        var spec = JsonSerializer.Deserialize<Spec>(Convert.FromBase64String(value[Prefix.Length..]), Json) ?? throw new InvalidDataException("Missing spec");
        Require(Regex.IsMatch(spec.Id, "^[a-zA-Z0-9_-]{1,110}$") && spec.Chunks is >= 1 and <= 10000 && spec.Characters is >= 1 and <= 1024, "Invalid provider spec");
        Require(spec.GapMs is >= 0 and <= 100 && spec.BurstSize is >= 0 and <= 10000 && spec.BurstPauseMs is >= 0 and <= 1000 && spec.Cancel is "none" or "before-usage" or "after-usage", "Invalid provider pacing");
        return spec;
    }
    public static string Marker(Spec s) => "M[" + s.Id + "]";
    public static string Piece(Spec s, int index) => index == 0 ? Marker(s) + " café 雪|" : new string('x', s.Characters) + "|";
    public static string Text(Spec s) => string.Concat(Enumerable.Range(0, s.Chunks).Select(i => Piece(s, i)));
    public static string ExpectedToolArguments(Spec s) => JsonSerializer.Serialize(new { case_id = s.Id, value = 42 }, Json);
    public static object Usage => new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 };
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;
    public static byte[] Audio(int length)
    {
        var data = new byte[length];
        for (var i = 44; i < data.Length; i++) data[i] = (byte)(i % 251);
        Encoding.ASCII.GetBytes("RIFF").CopyTo(data, 0); BitConverter.GetBytes(length - 8).CopyTo(data, 4);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(data, 8); BitConverter.GetBytes(16).CopyTo(data, 16);
        BitConverter.GetBytes((short)1).CopyTo(data, 20); BitConverter.GetBytes((short)1).CopyTo(data, 22);
        BitConverter.GetBytes(16000).CopyTo(data, 24); BitConverter.GetBytes(32000).CopyTo(data, 28);
        BitConverter.GetBytes((short)2).CopyTo(data, 32); BitConverter.GetBytes((short)16).CopyTo(data, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(data, 36); BitConverter.GetBytes(length - 44).CopyTo(data, 40);
        return data;
    }
}
