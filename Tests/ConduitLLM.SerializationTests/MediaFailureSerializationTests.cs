using System.Text.Json;

using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;

namespace ConduitLLM.SerializationTests;

public sealed class MediaFailureSerializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureResult_PersistsAndCachesWithoutReflection(bool video)
    {
        var failedAt = new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc);
        object result = video
            ? new VideoGenerationFailureResult("fixture failure", null, null, false, failedAt)
            : new ImageGenerationFailureResult("fixture failure", null, "fixture-provider", false, 0);
        var stored = JsonSerializer.Serialize(result, result.GetType(), AsyncTaskJsonContext.Default);
        var cached = JsonSerializer.Serialize(new AsyncTaskStatus
        {
            TaskId = "fixture-task", State = TaskState.Failed, Result = result, Error = "fixture failure"
        }, AsyncTaskJsonContext.Default.AsyncTaskStatus);
        var restored = JsonSerializer.Deserialize(cached, AsyncTaskJsonContext.Default.AsyncTaskStatus)!;
        var cachedResult = Assert.IsType<JsonElement>(restored.Result);
        Assert.Equal(stored, cachedResult.GetRawText());
        Assert.Equal(TaskState.Failed, restored.State);
        Assert.Equal("fixture failure", restored.Error);
        Assert.Equal(JsonValueKind.Null, cachedResult.GetProperty("ErrorCode").ValueKind);
        Assert.False(cachedResult.GetProperty("IsRetryable").GetBoolean());
        Assert.Equal(5, cachedResult.EnumerateObject().Count());
        if (video)
        {
            Assert.Equal(JsonValueKind.Null, cachedResult.GetProperty("Provider").ValueKind);
            Assert.Equal(failedAt, cachedResult.GetProperty("FailedAt").GetDateTime());
        }
        else Assert.Equal(0, cachedResult.GetProperty("AttemptCount").GetInt32());
    }
}
