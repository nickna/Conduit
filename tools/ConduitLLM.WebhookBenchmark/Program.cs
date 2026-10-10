using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Messaging;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Consumers;
using ConduitLLM.Gateway.Extensions;
using ConduitLLM.Gateway.Services;
#if !WEBHOOK_BASELINE
using ConduitLLM.Messaging.Wolverine;
#endif
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wolverine;

// Same source is compiled against the unmodified baseline checkout with
// WebhookBenchmarkBaseline=true. Never point synthetic production queue names at
// an application database. The runner creates this isolated database explicitly.
var connection = Environment.GetEnvironmentVariable("CONDUIT_WEBHOOK_BENCH_POSTGRES") ?? throw new InvalidOperationException("Isolated benchmark connection required.");
if (new NpgsqlConnectionStringBuilder(connection).Database != "conduit_webhook_bench") throw new InvalidOperationException("Only conduit_webhook_bench is allowed.");
await using (var db = new ConduitDbContext(new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(connection).Options))
    await db.Database.MigrateAsync();
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    [WolverineMessagingExtensions.SchemaNameKey] = "wolverine_webhook_bench", [WolverineMessagingExtensions.AutoProvisionKey] = "true"
}).Build();
var output = new List<object>();
foreach (var instances in new[] { 1, 2 })
foreach (var mixed in new[] { false, true })
{
    var observations = new Observations();
    var receiverBuilder = WebApplication.CreateBuilder(); receiverBuilder.Logging.ClearProviders();
    receiverBuilder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
    await using var receiver = receiverBuilder.Build();
    receiver.MapPost("/{kind}/{destination}", async context =>
    {
        using var json = await JsonDocument.ParseAsync(context.Request.Body);
        var id = json.RootElement.GetProperty("task_id").GetString()!;
        observations.Received(id);
        var bad = context.Request.RouteValues["kind"]?.ToString() == "bad";
        await Task.Delay(bad ? 200 : 10, context.RequestAborted);
        context.Response.StatusCode = bad ? 503 : 202;
    });
    await receiver.StartAsync();
    var url = receiver.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    IHost BuildHost() => Host.CreateDefaultBuilder().ConfigureLogging(l => l.ClearProviders()).ConfigureServices(services =>
    {
        services.AddWolverineEventBus(); services.AddMemoryCache(); services.AddDistributedMemoryCache();
#if WEBHOOK_BASELINE
        services.AddWebhookServices(config);
        // Auxiliary spend/statistics hosted services are outside this workload.
        // This executes before UseWolverine registers its production hosted services.
        services.RemoveAll<IHostedService>();
        services.AddSingleton<IWebhookDeliveryTracker, NoOpWebhookDeliveryTracker>();
#else
        services.AddWebhookHttpServices(config);
        services.AddScoped<IWebhookDeliveryStore, WebhookDeliveryStore>();
#endif
        services.AddSingleton<IWebhookDeliveryNotificationService>(new BenchmarkNotifications(observations));
        services.AddScoped<IEventHandler<WebhookDeliveryRequested>, WebhookDeliveryConsumer>();
    }).AddConduitWolverine(config, connection, "webhook-benchmark", options =>
    {
        options.ApplicationAssembly = typeof(WebhookDeliveryConsumer).Assembly;
        options.UseSystemTextJsonForSerialization(o => o.TypeInfoResolverChain.Insert(0, CoreMessagingJsonContext.Default));
        options.ApplyConduitPublishRouting(); options.AddEventBridge<WebhookDeliveryRequested>();
        options.ListenWithPolicy(ConduitEndpointPolicies.WebhookDelivery, [typeof(WebhookDeliveryRequested)]);
        options.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(250);
    }).Build();
    var hosts = Enumerable.Range(0, instances).Select(_ => BuildHost()).ToArray();
    try
    {
        foreach (var host in hosts) await host.StartAsync();
        var bus = hosts[0].Services.GetRequiredService<IMessageBus>();
        for (var i = 0; i < 20; i++) await PublishAsync(bus, "warm-" + Guid.NewGuid(), false, i);
        await WaitAsync(() => observations.Completed.Count >= 20, TimeSpan.FromSeconds(30));
        observations.Reset();
        await SqlAsync("SELECT pg_stat_statements_reset()");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var process = Process.GetCurrentProcess(); var startCpu = process.TotalProcessorTime;
        var allocation = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
        const int messages = 200;
        var monitorStop = new CancellationTokenSource(); long threadQueue = 0, threads = 0, working = 0;
        var monitor = Task.Run(async () =>
        {
            while (!monitorStop.IsCancellationRequested)
            {
                threadQueue = Math.Max(threadQueue, ThreadPool.PendingWorkItemCount); threads = Math.Max(threads, ThreadPool.ThreadCount);
                process.Refresh(); working = Math.Max(working, process.WorkingSet64);
                await Task.Delay(20);
            }
        });
        for (var i = 0; i < messages; i++) await PublishAsync(bus, "event-" + i, mixed && i < 160, i);
        // Identical fixed observation window, including queueing and first retry.
        await Task.Delay(TimeSpan.FromSeconds(10) - watch.Elapsed > TimeSpan.Zero ? TimeSpan.FromSeconds(10) - watch.Elapsed : TimeSpan.Zero);
        monitorStop.Cancel(); await monitor;
        var elapsed = watch.Elapsed.TotalSeconds;
        var first = observations.FirstLatencies.Values.ToArray(); var completed = observations.Completed.Values.ToArray();
        var healthyIds = Enumerable.Range(mixed ? 160 : 0, mixed ? 40 : messages).Select(i => "event-" + i).ToHashSet();
        var healthyFirst = observations.FirstLatencies.Where(p => healthyIds.Contains(p.Key)).Select(p => p.Value).ToArray();
        var useful = completed.Length;
        var postCount = observations.Posts;
        var pgCalls = Convert.ToInt64(await ScalarAsync("SELECT COALESCE(SUM(calls),0)::bigint FROM pg_stat_statements"));
        var pending = observations.Enqueued.Keys.Except(observations.Completed.Keys).ToArray();
        var oldest = pending.Select(id => (DateTime.UtcNow - observations.Enqueued[id]).TotalSeconds).DefaultIfEmpty(0).Max();
        var depth = Convert.ToInt64(await ScalarAsync("SELECT COUNT(*) FROM wolverine_queues.wolverine_queue_webhook_delivery"));
        output.Add(new { implementation =
#if WEBHOOK_BASELINE
            "7b3a471d",
#else
            "current",
#endif
            instances, mixed, messages, healthy = mixed ? 40 : messages, windowSeconds = elapsed,
            delivered = useful, usefulPerSecond = useful / elapsed, posts = postCount, amplificationPerOfferedEvent = postCount / (double)messages,
            firstAttemptMs = Percentiles(first), endToEndMs = Percentiles(completed), queueDepth = depth, pending = pending.Length, oldestPendingSeconds = oldest,
            healthyFirstAttemptMs = Percentiles(healthyFirst), healthyDrainMilliseconds = completed.Length == healthyIds.Count ? completed.Max() : (double?)null,
            cpuMilliseconds = (process.TotalProcessorTime - startCpu).TotalMilliseconds,
            allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocation, peakWorkingSetBytes = working, peakThreadPoolQueue = threadQueue,
            peakThreadPoolThreads = threads, postgresCalls = pgCalls, redisCalls = 0 });
        Console.WriteLine(JsonSerializer.Serialize(output[^1]));
        async Task PublishAsync(IMessageBus target, string id, bool bad, int index)
        {
            observations.Enqueued[id] = DateTime.UtcNow;
            await target.PublishAsync(new WebhookDeliveryRequested { TaskId = id, TaskType = "video",
                WebhookUrl = $"{url}/{(bad ? "bad" : "healthy")}/{(bad ? 0 : index % 4)}", EventType = WebhookEventType.TaskCompleted,
                PayloadJson = JsonSerializer.Serialize(new { task_id = id, status = "completed", padding = new string('x', 1024) }) });
        }
    }
    finally
    {
        foreach (var host in hosts) { await host.StopAsync(); host.Dispose(); }
        await receiver.StopAsync();
        await SqlAsync("""
            DO $$ DECLARE entry record; BEGIN FOR entry IN SELECT schemaname, tablename FROM pg_tables
            WHERE schemaname IN ('wolverine_webhook_bench','wolverine_queues') LOOP
              EXECUTE format('TRUNCATE TABLE %I.%I CASCADE', entry.schemaname, entry.tablename); END LOOP;
            IF to_regclass('public."WebhookDeliveries"') IS NOT NULL THEN
              TRUNCATE TABLE "WebhookDeliveries", "WebhookReplayAudits";
            END IF; END $$;
            """);
    }
}
await File.WriteAllTextAsync(args.FirstOrDefault() ?? "webhook-benchmark.json", JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
async Task SqlAsync(string sql) { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
async Task<object?> ScalarAsync(string sql) { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return await c.ExecuteScalarAsync(); }
static object Percentiles(double[] values)
{
    Array.Sort(values); double? P(double q) => values.Length == 0 ? null : values[Math.Min(values.Length - 1, (int)Math.Ceiling(values.Length * q) - 1)];
    return new { p50 = P(.5), p95 = P(.95), p99 = P(.99) };
}
static async Task WaitAsync(Func<bool> ready, TimeSpan timeout)
{
    using var stop = new CancellationTokenSource(timeout); while (!ready()) await Task.Delay(25, stop.Token);
}
internal sealed class Observations
{
    public ConcurrentDictionary<string, DateTime> Enqueued { get; } = new();
    public ConcurrentDictionary<string, double> FirstLatencies { get; } = new();
    public ConcurrentDictionary<string, double> Completed { get; } = new();
    public int Posts;
    public void Received(string id) { Interlocked.Increment(ref Posts); if (Enqueued.TryGetValue(id, out var start)) FirstLatencies.TryAdd(id, (DateTime.UtcNow - start).TotalMilliseconds); }
    public void Complete(string id) { if (Enqueued.TryGetValue(id, out var start)) Completed.TryAdd(id, (DateTime.UtcNow - start).TotalMilliseconds); }
    public void Reset() { Enqueued.Clear(); FirstLatencies.Clear(); Completed.Clear(); Posts = 0; }
}
internal sealed class BenchmarkNotifications(Observations observations) : IWebhookDeliveryNotificationService
{
    public Task NotifyDeliveryAttemptAsync(string url, string task, string type, string evt, int attempt) => Task.CompletedTask;
    public Task NotifyDeliverySuccessAsync(string url, string task, int status, long elapsed, int attempts) { observations.Complete(task); return Task.CompletedTask; }
    public Task NotifyDeliveryFailureAsync(string url, string task, string error, int? status, int attempt, bool permanent) => Task.CompletedTask;
    public Task NotifyRetryScheduledAsync(string url, string task, DateTime due, int attempt, int max) => Task.CompletedTask;
    public Task NotifyCircuitBreakerStateChangeAsync(string url, string state, string previous, string reason, int failures) => Task.CompletedTask;
    public Task<WebhookStatistics> GetStatisticsAsync(string period = "last_hour") => Task.FromResult(new WebhookStatistics());
    public void RecordDeliveryAttempt(string url) { }
    public void RecordDeliverySuccess(string url, long elapsed) { }
    public void RecordDeliveryFailure(string url, bool permanent) { }
}
