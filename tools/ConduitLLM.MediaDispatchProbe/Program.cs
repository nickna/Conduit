using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Messaging;
using ConduitLLM.Core.Models;
using ConduitLLM.Messaging.Wolverine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.Runtime;

var connectionString = Environment.GetEnvironmentVariable("CONDUIT_MEDIA_TEST_POSTGRES")
    ?? throw new InvalidOperationException("CONDUIT_MEDIA_TEST_POSTGRES is required.");
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    [WolverineMessagingExtensions.SchemaNameKey] = "wolverine_media_test"
}).Build();
using var host = Host.CreateDefaultBuilder()
    .ConfigureLogging(log => log.ClearProviders())
    .ConfigureServices(services => services.AddWolverineEventBus())
    .AddConduitWolverine(configuration, connectionString, "media-test-publisher",
        options => options.ApplyConduitPublishRouting()).Build();
await host.StartAsync();
var submission = new MediaTaskSubmission(host.Services.GetRequiredService<IWolverineRuntime>(),
    host.Services.GetRequiredService<IEventBus>(), NullLogger<MediaTaskSubmission>.Instance);
var metadata = new TaskMetadata(1)
{
    Model = "test-model", Prompt = "full persisted prompt",
    ExtensionData = new() { ["VirtualKey"] = "test-key" }
};
var id = args.FirstOrDefault() == "video"
    ? await submission.SubmitAsync(new VideoGenerationRequested
    {
        VirtualKeyId = "1", IsAsync = true,
        Request = new VideoGenerationRequest { Model = "test-model", Prompt = metadata.Prompt, Duration = 7 }
    }, metadata)
    : await submission.SubmitAsync(new ImageGenerationRequested
    {
        VirtualKeyId = 1, UserId = "test-user", VirtualKeyHash = "test-hash",
        Request = new ImageGenerationRequest { Model = "test-model", Prompt = metadata.Prompt, Image = "edit-image", Mask = "edit-mask", Operation = "edit" }
    }, metadata);
Console.WriteLine($"ACCEPTED:{id}");
// The integration test terminates this process without StopAsync/Dispose.
await Task.Delay(Timeout.InfiniteTimeSpan);

public sealed class MediaDispatchProbeMarker;
