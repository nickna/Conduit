using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Repositories;
using Microsoft.EntityFrameworkCore;
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
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Consumers;
using ConduitLLM.Gateway.Extensions;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Core.Serialization;
using Wolverine;

try { await RunAsync(args); }
catch (Exception exception) { Console.Error.WriteLine(exception); Environment.ExitCode = 1; }

static async Task RunAsync(string[] args)
{
var webhookWorker = args.FirstOrDefault() == "webhook-worker";

var connectionString = Environment.GetEnvironmentVariable("CONDUIT_MEDIA_TEST_POSTGRES")
    ?? throw new InvalidOperationException("CONDUIT_MEDIA_TEST_POSTGRES is required.");
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    [WolverineMessagingExtensions.SchemaNameKey] = "wolverine_media_test",
    ["Webhooks:Delivery:AttemptTimeoutSeconds"] = "1", ["Webhooks:Delivery:ConnectTimeoutSeconds"] = "1"
}).Build();
using var host = Host.CreateDefaultBuilder()
    .ConfigureLogging(log => log.ClearProviders())
    .ConfigureServices(services =>
    {
        services.AddWolverineEventBus(); services.AddDistributedMemoryCache();
        if (webhookWorker)
        {
            services.AddWebhookHttpServices(configuration);
            services.AddScoped<WebhookDeliveryStore>();
            services.AddScoped<IWebhookDeliveryStore>(sp => new PausedDeliveryStore(sp.GetRequiredService<WebhookDeliveryStore>(), args.ElementAtOrDefault(1)));
            services.AddSingleton<IWebhookDeliveryNotificationService, ProbeNotifications>();
            services.AddScoped<IEventHandler<WebhookDeliveryRequested>, WebhookDeliveryConsumer>();
        }
    })
    .AddConduitWolverine(configuration, connectionString, "media-test-publisher",
        options =>
        {
            options.ApplyConduitPublishRouting();
            options.UseSystemTextJsonForSerialization(json => json.TypeInfoResolverChain.Insert(0, CoreMessagingJsonContext.Default));
            if (webhookWorker)
            {
                options.ApplicationAssembly = typeof(WebhookDeliveryConsumer).Assembly;
                options.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(100);
                options.AddEventBridge<WebhookDeliveryRequested>();
                options.ListenWithPolicy(ConduitEndpointPolicies.WebhookDelivery, [typeof(WebhookDeliveryRequested)]);
            }
        }).Build();
Console.Error.WriteLine("PROBE:starting-host");
await host.StartAsync();
Console.Error.WriteLine("PROBE:host-started");
if (webhookWorker)
{
    Console.WriteLine("WORKER:ready");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}
if (args.FirstOrDefault() == "terminal")
{
    Console.WriteLine("TERMINAL:starting");
    var writer = new MediaTaskTerminalWriter(host.Services.GetRequiredService<IWolverineRuntime>(),
        host.Services.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
        host.Services.GetRequiredService<IEventBus>(), NullLogger<MediaTaskTerminalWriter>.Instance);
    await writer.CommitAsync(new(args[1], TaskState.Completed, "terminal-crash-test", Progress: 100,
        Webhook: new() { TaskId = args[1], VirtualKeyId = 1, WebhookUrl = args[2], PayloadJson = "{\"status\":\"completed\"}",
            Headers = new() { ["Authorization"] = "Bearer crash-test" } }));
    Console.WriteLine("TERMINAL:committed");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}
if (args.FirstOrDefault() == "claim")
{
    var repository = new AsyncTaskRepository(new ProbeContextFactory(connectionString), NullLogger<AsyncTaskRepository>.Instance);
    var owner = $"crashed-worker:{Environment.ProcessId}";
    var result = await repository.TryClaimTaskAsync(args[1], owner, TimeSpan.FromMinutes(15));
    if (args.ElementAtOrDefault(2) == "post-provider")
        await repository.MarkProviderInvocationStartedAsync(args[1], owner);
    Console.WriteLine($"CLAIMED:{result}");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}
if (args.FirstOrDefault() == "recover")
{
    var recovery = new MediaTaskRecovery(host.Services.GetRequiredService<IWolverineRuntime>(),
        host.Services.GetRequiredService<IEventBus>(), host.Services.GetRequiredService<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
        NullLogger<MediaTaskRecovery>.Instance);
    var result = await recovery.RecoverAsync();
    Console.WriteLine($"RECOVERED:{result.Redispatched}");
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return;
}
var submission = new MediaTaskSubmission(host.Services.GetRequiredService<IWolverineRuntime>(),
    host.Services.GetRequiredService<IEventBus>(), NullLogger<MediaTaskSubmission>.Instance);
var metadata = new TaskMetadata(1)
{
    Model = "test-model", Prompt = "full persisted prompt",
    ExtensionData = new() { ["VirtualKey"] = "test-key" }
};
Console.Error.WriteLine("PROBE:submitting");
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
}

public sealed class MediaDispatchProbeMarker;

internal sealed class ProbeContextFactory(string connectionString) : IDbContextFactory<ConduitDbContext>
{
    public ConduitDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(connectionString).Options);
}

internal sealed class PausedDeliveryStore(WebhookDeliveryStore inner, string? pause) : IWebhookDeliveryStore
{
    public Task<WebhookClaim> TryClaimAsync(WebhookDeliveryRequested request, DateTime deadline, TimeSpan lease, CancellationToken ct = default) =>
        inner.TryClaimAsync(request, deadline, lease, ct);
    public async Task<int?> BeginAttemptAsync(WebhookClaim claim, CancellationToken ct = default)
    {
        if (pause == "before-send") { Console.WriteLine("DELIVERY:claimed"); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        return await inner.BeginAttemptAsync(claim, ct);
    }
    public async Task<bool> CompleteAsync(WebhookClaim claim, WebhookSendResult result, bool exhausted, CancellationToken ct = default)
    {
        if (pause == "after-acceptance" && result.Success) { Console.WriteLine("DELIVERY:accepted"); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        return await inner.CompleteAsync(claim, result, exhausted, ct);
    }
    public async Task<bool> ScheduleAsync(WebhookClaim claim, DateTime due, WebhookSendResult? result = null, CancellationToken ct = default)
    {
        var committed = await inner.ScheduleAsync(claim, due, result, ct);
        if (committed && pause == "after-schedule") { Console.WriteLine("DELIVERY:scheduled"); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
        return committed;
    }
}

internal sealed class ProbeNotifications : IWebhookDeliveryNotificationService
{
    public Task NotifyDeliveryAttemptAsync(string url, string task, string type, string evt, int attempt) => Task.CompletedTask;
    public Task NotifyDeliverySuccessAsync(string url, string task, int status, long elapsed, int attempts) => Task.CompletedTask;
    public Task NotifyDeliveryFailureAsync(string url, string task, string error, int? status, int attempt, bool permanent) => Task.CompletedTask;
    public Task NotifyRetryScheduledAsync(string url, string task, DateTime due, int attempt, int max) => Task.CompletedTask;
    public Task NotifyCircuitBreakerStateChangeAsync(string url, string state, string previous, string reason, int failures) => Task.CompletedTask;
    public Task<ConduitLLM.Configuration.DTOs.SignalR.WebhookStatistics> GetStatisticsAsync(string period = "last_hour") =>
        Task.FromResult(new ConduitLLM.Configuration.DTOs.SignalR.WebhookStatistics());
}
