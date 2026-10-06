using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.Models.ProviderSync;
using ConduitLLM.Admin.Services;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Tests.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ConduitLLM.Tests.Admin.Services;

public sealed class MetadataSyncLockTests
{
    [Fact]
    public async Task ScheduledSync_BlocksManualWithHttp409_ThenReleasesBeforeNextInterval()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detection = new Mock<IOpenRouterDriftDetectionService>();
        detection.Setup(value => value.RunSyncAsync("Schedule", It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken _) =>
            {
                started.TrySetResult();
                await complete.Task;
                return new ProviderSyncRunDto { Status = "Completed" };
            });
        var locks = new TestDistributedLockProvider();
        await using var services = new ServiceCollection().AddLogging().AddScoped(_ => detection.Object).BuildServiceProvider();
        var scheduler = new OpenRouterMetadataSyncService(services.GetRequiredService<IServiceScopeFactory>(), locks,
            Options.Create(new OpenRouterSyncOptions()), NullLogger<OpenRouterMetadataSyncService>.Instance);
        var scheduled = scheduler.RunScheduledSyncAsync(CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var result = await ProviderSyncEndpoints.Run(detection.Object, locks,
                new DefaultHttpContext { RequestServices = services }, services.GetRequiredService<ILoggerFactory>());
            Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)result).StatusCode);
            detection.Verify(value => value.RunSyncAsync("Manual", It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { complete.TrySetResult(); await scheduled; }
        await using var next = await locks.TryAcquireAsync("openrouter:metadata-sync:leader");
        Assert.NotNull(next);
    }
}
