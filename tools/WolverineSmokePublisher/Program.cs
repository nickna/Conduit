using ConduitLLM.Configuration.Events;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Core.Data;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Messaging;

using JasperFx;
using JasperFx.CodeGeneration;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Wolverine;
using Wolverine.Postgresql;

var probeId = args.FirstOrDefault();
var mediaNotifications = args is [_, "--media-notifications", var imageTaskId, var videoTaskId] &&
                         !string.IsNullOrWhiteSpace(imageTaskId) && !string.IsNullOrWhiteSpace(videoTaskId);
if (string.IsNullOrWhiteSpace(probeId) || (args.Length != 1 && !mediaNotifications))
{
    Console.Error.WriteLine("Usage: WolverineSmokePublisher <probe-id> [--media-notifications <image-task-id> <video-task-id>]");
    return 2;
}

var (_, connectionString) = new ConnectionStringManager()
    .GetProviderAndConnectionString("CoreAPI");

using var host = Host.CreateDefaultBuilder()
    .UseWolverine(options =>
    {
        options.ServiceName = "conduit-smoke-publisher";
        options.CodeGeneration.TypeLoadMode = TypeLoadMode.Static;
        options.Discovery.DisableConventionalDiscovery();
        options.Durability.Mode = DurabilityMode.Solo;

        options.UsePostgresqlPersistenceAndTransport(
            connectionString,
            "wolverine_conduit_smoke_publisher",
            transportSchema: WolverineMessagingExtensions.TransportSchemaName);
        options.AutoBuildMessageStorageOnStartup = AutoCreate.CreateOrUpdate;
        options.ApplyConduitPublishRouting();
    })
    .Build();

await host.StartAsync();
try
{
    var bus = host.Services.GetRequiredService<IMessageBus>();

    if (mediaNotifications)
    {
        var timestamp = new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc);
        await bus.PublishAsync(new ImageGenerationProgress
        {
            TaskId = args[2],
            ImagesCompleted = 1,
            TotalImages = 2,
            Status = "processing",
            Message = $"native-progress-{probeId}",
            CorrelationId = probeId,
        });
        await bus.PublishAsync(new VideoGenerationStarted
        {
            RequestId = args[3],
            Provider = $"native-provider-{probeId}",
            StartedAt = timestamp,
            EstimatedSeconds = null,
            CorrelationId = probeId,
        });
        await bus.PublishAsync(new VideoGenerationFailed
        {
            RequestId = args[3],
            Error = $"native-failure-{probeId}",
            Provider = $"native-provider-{probeId}",
            ErrorCode = null,
            IsRetryable = false,
            RetryCount = 0,
            MaxRetries = 3,
            FailedAt = timestamp,
            NextRetryAt = null,
            CorrelationId = probeId,
        });
        Console.WriteLine($"Published Wolverine media notifications with id {probeId}.");
        return 0;
    }

    await bus.PublishAsync(new SpendUpdateRequested
    {
        KeyId = int.MaxValue,
        Amount = 0.000001m,
        RequestId = $"static-codegen-spend-{probeId}",
        CorrelationId = probeId,
    });

    await bus.PublishAsync(new BatchSpendFlushRequestedEvent
    {
        RequestId = $"static-codegen-batch-{probeId}",
        RequestedBy = "Wolverine two-host smoke",
        Source = "CI",
        Reason = "Verify committed static handler delivery",
        IncludeStatistics = false,
    });

    await bus.PublishAsync(new ImageGenerationCancelled
    {
        TaskId = $"static-codegen-image-{probeId}",
        VirtualKeyId = int.MaxValue,
        Reason = "Static codegen delivery probe",
        CorrelationId = probeId,
    });

    await bus.PublishAsync(new VideoGenerationCancelled
    {
        RequestId = $"static-codegen-video-{probeId}",
        Reason = "Static codegen delivery probe",
        CorrelationId = probeId,
    });

    Console.WriteLine($"Published Wolverine delivery probes with id {probeId}.");
}
finally
{
    await host.StopAsync();
}

return 0;
