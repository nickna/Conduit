using System.Text.Json;

using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Serialization;

namespace ConduitLLM.Tests.Core.Serialization;

public sealed class AsyncTaskJsonContextTests
{
    [Fact]
    public void TaskMetadataRoundTripPreservesKnownPolymorphicExtensionValues()
    {
        object metadata = new TaskMetadata(42)
        {
            ExtensionData = new Dictionary<string, object>
            {
                ["VirtualKey"] = "vk-secret",
                ["Request"] = new VideoGenerationRequest
                {
                    Model = "video-model",
                    Prompt = "ocean sunrise"
                }
            }
        };

        var json = JsonSerializer.Serialize(
            metadata,
            metadata.GetType(),
            AsyncTaskJsonContext.Default);
        var roundTrip = JsonSerializer.Deserialize(
            json,
            AsyncTaskJsonContext.Default.TaskMetadata);

        Assert.NotNull(roundTrip);
        Assert.Equal(42, roundTrip.VirtualKeyId);
        Assert.Equal("vk-secret", Assert.IsType<JsonElement>(roundTrip.ExtensionData!["VirtualKey"]).GetString());
        var request = Assert.IsType<JsonElement>(roundTrip.ExtensionData["Request"]);
        Assert.Equal("video-model", request.GetProperty("model").GetString());
    }

    [Fact]
    public void AsyncTaskStatusRoundTripPreservesRegisteredResultPayload()
    {
        var status = new AsyncTaskStatus
        {
            TaskId = "task-1",
            TaskType = "video_generation",
            State = TaskState.Completed,
            Result = new MediaGenerationTaskResult
            {
                Created = 123,
                Model = "video-model",
                Data =
                [
                    new MediaGenerationTaskResultItem { Url = "https://media.example/video.mp4" }
                ]
            }
        };

        var json = JsonSerializer.Serialize(
            status,
            AsyncTaskJsonContext.Default.AsyncTaskStatus);
        var roundTrip = JsonSerializer.Deserialize(
            json,
            AsyncTaskJsonContext.Default.AsyncTaskStatus);

        var result = Assert.IsType<JsonElement>(roundTrip!.Result);
        Assert.Equal("video-model", result.GetProperty("model").GetString());
        Assert.Equal(
            "https://media.example/video.mp4",
            result.GetProperty("data")[0].GetProperty("url").GetString());
    }
}
