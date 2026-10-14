using System.Text.Json;

using ConduitLLM.Configuration.Constants;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.EventHandlers;
using ConduitLLM.Gateway.Interfaces;
using ConduitLLM.Persistence;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Tests.Messaging;

using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace ConduitLLM.Tests.Gateway.EventHandlers;

public sealed class MediaFailureResultTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task FailureHandler_PersistsGeneratedResultAndContinuesNotification(
        bool video, bool terminalWriter, bool nullDetails)
    {
        var task = new AsyncTaskRuntimeRecord
        {
            Id = "fixture-task", Type = video ? "video_generation" : "image_generation",
            State = (int)TaskState.Processing, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        var store = new Mock<IAsyncTaskRuntimeStore>(MockBehavior.Strict);
        store.Setup(candidate => candidate.GetByIdAsync(task.Id, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        store.Setup(candidate => candidate.UpdateAsync(task, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var writer = new Mock<IMediaTaskTerminalWriter>(MockBehavior.Strict);
        MediaTaskTerminalTransition? committed = null;
        writer.Setup(candidate => candidate.CommitAsync(It.IsAny<MediaTaskTerminalTransition>(), It.IsAny<CancellationToken>()))
            .Callback<MediaTaskTerminalTransition, CancellationToken>((transition, _) => committed = transition)
            .ReturnsAsync(true);
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        using var provider = services.BuildServiceProvider();
        var taskService = new HybridAsyncTaskService(store.Object,
            provider.GetRequiredService<IDistributedCache>(), NullLogger<HybridAsyncTaskService>.Instance,
            terminalWriter ? writer.Object : null);
        using var progressCache = new MemoryCache(new MemoryCacheOptions());
        var progressKey = video ? CacheKeys.MediaProgress.VideoProgress(task.Id) : CacheKeys.MediaProgress.ImageProgress(task.Id);
        progressCache.Set(progressKey, "in progress");
        var errorCode = nullDetails ? null : "generation_failed";
        var failedAt = new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc);

        if (video)
        {
            var notification = new Mock<IVideoGenerationNotificationService>(MockBehavior.Strict);
            notification.Setup(candidate => candidate.NotifyVideoGenerationFailedAsync(
                task.Id, "fixture failure", false, errorCode, 0, 3, null, failedAt)).Returns(Task.CompletedTask);
            var handler = new VideoGenerationFailedHandler(taskService, progressCache, notification.Object,
                NullLogger<VideoGenerationFailedHandler>.Instance);
            await handler.HandleAsync(new VideoGenerationFailed
            {
                RequestId = task.Id, Error = "fixture failure", ErrorCode = errorCode,
                Provider = nullDetails ? null : "fixture-provider", IsRetryable = false, FailedAt = failedAt
            }, new TestEventContext());
            notification.VerifyAll();
        }
        else
        {
            var notification = new Mock<IImageGenerationNotificationService>(MockBehavior.Strict);
            notification.Setup(candidate => candidate.NotifyImageGenerationFailedAsync(task.Id, "fixture failure", false))
                .Returns(Task.CompletedTask);
            var handler = new ImageGenerationFailedHandler(taskService, progressCache, notification.Object,
                NullLogger<ImageGenerationFailedHandler>.Instance);
            await handler.HandleAsync(new ImageGenerationFailed
            {
                TaskId = task.Id, Error = "fixture failure", ErrorCode = errorCode,
                Provider = "fixture-provider", IsRetryable = false, AttemptCount = 0
            }, new TestEventContext());
            notification.VerifyAll();
        }

        var resultJson = terminalWriter ? committed!.ResultJson : task.Result;
        using var document = JsonDocument.Parse(resultJson!);
        var result = document.RootElement;
        Assert.Equal(new[] { "Error", "ErrorCode", "Provider", "IsRetryable", video ? "FailedAt" : "AttemptCount" }.Order(),
            result.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("fixture failure", result.GetProperty("Error").GetString());
        Assert.Equal(errorCode, result.GetProperty("ErrorCode").GetString());
        Assert.Equal(video && nullDetails ? null : "fixture-provider", result.GetProperty("Provider").GetString());
        Assert.False(result.GetProperty("IsRetryable").GetBoolean());
        if (video) Assert.Equal(failedAt, result.GetProperty("FailedAt").GetDateTime());
        else Assert.Equal(0, result.GetProperty("AttemptCount").GetInt32());
        Assert.False(progressCache.TryGetValue(progressKey, out _));
        if (terminalWriter)
        {
            Assert.Equal(TaskState.Failed, committed!.State);
            Assert.Equal("fixture failure", committed.Error);
            store.Verify(candidate => candidate.UpdateAsync(It.IsAny<AsyncTaskRuntimeRecord>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else
        {
            var cached = await taskService.GetTaskStatusAsync(task.Id);
            Assert.Equal(TaskState.Failed, cached!.State);
            Assert.Equal("fixture failure", cached.Error);
            Assert.Equal(resultJson, Assert.IsType<JsonElement>(cached.Result).GetRawText());
            store.Verify(candidate => candidate.GetByIdAsync(task.Id, It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
    }
}
