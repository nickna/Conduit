using System.Diagnostics;
using System.Globalization;

using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Configuration.Messaging.Wolverine;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Messaging;

using JasperFx.CodeGeneration;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

using Wolverine;
using Wolverine.Postgresql;

namespace ConduitLLM.Benchmarks.Messaging
{
    /// <summary>
    /// Measures the sustained delivery throughput the Wolverine PostgreSQL transport can
    /// support per tuned endpoint, and the publish→handle latency at saturation (#1223).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately NOT a BenchmarkDotNet benchmark. BDN measures short, isolated,
    /// repeatable in-process operations; what matters here is the drain rate of a durable
    /// queue that is backed up — a stateful, multi-second, out-of-process property. The
    /// harness therefore runs as its own console mode.
    /// </para>
    /// <para>
    /// The host is configured through the <em>real</em> production entry points —
    /// <see cref="WolverineMessagingExtensions.AddConduitWolverine"/>,
    /// <see cref="ConduitMessagingTopology.ApplyConduitPublishRouting"/> and
    /// <see cref="WolverineEndpointPolicy.ListenWithPolicy"/> over the actual
    /// <see cref="ConduitEndpointPolicies"/> descriptors. Nothing about the transport tuning
    /// is restated here, so a later change to a policy's PrefetchCount or to the listener's
    /// polling interval is picked up by this harness automatically rather than silently
    /// invalidating a stale measurement.
    /// </para>
    /// <para>
    /// Handlers are synthetic: they count deliveries, record latency, and optionally await a
    /// configurable delay standing in for the real handler's I/O. That separates the two
    /// things a poll-based transport multiplies together — the receive-batch ceiling
    /// (<c>MaximumMessagesToReceive</c> ÷ <c>PollingInterval</c>) and the handler-bound
    /// ceiling (concurrency ÷ handler latency). Sweeping the delay traces that curve.
    /// </para>
    /// <para>
    /// Runs against an isolated database (default <c>conduit_msgbench</c>), created on demand.
    /// This is not optional: the tuned queue names are the production ones, so pointing the
    /// harness at the application database would inject synthetic spend and webhook events
    /// into queues a running Gateway is listening on.
    /// </para>
    /// </remarks>
    internal static class TransportThroughputHarness
    {
        private const string DefaultAdminConnection =
            "Host=localhost;Port=5432;Username=conduit;Password=conduitpass;Database=postgres";
        private const string DefaultBenchDatabase = "conduit_msgbench";

        /// <summary>
        /// The queues under test. Each carries the production policy descriptor and the
        /// representative event type used to generate load.
        /// </summary>
        private static readonly IReadOnlyList<QueueTarget> Targets = new[]
        {
            new QueueTarget(
                "spend",
                ConduitEndpointPolicies.SpendUpdate,
                ConduitMessagingTopology.SpendUpdateEvents,
                typeof(SpendUpdateRequested),
                (i, tag) => new SpendUpdateRequested
                {
                    KeyId = 1,
                    Amount = 0.01m,
                    RequestId = $"bench-{i}",
                    CorrelationId = tag,
                }),
            new QueueTarget(
                "webhook",
                ConduitEndpointPolicies.WebhookDelivery,
                ConduitMessagingTopology.WebhookDeliveryEvents,
                typeof(WebhookDeliveryRequested),
                (i, tag) => new WebhookDeliveryRequested
                {
                    TaskId = $"bench-{i}",
                    TaskType = "bench",
                    WebhookUrl = "http://127.0.0.1:9098/sink",
                    CorrelationId = tag,
                }),
            new QueueTarget(
                "image",
                ConduitEndpointPolicies.ImageGeneration,
                ConduitMessagingTopology.ImageGenerationEvents,
                typeof(ImageGenerationRequested),
                (i, tag) => new ImageGenerationRequested
                {
                    TaskId = $"bench-{i}",
                    VirtualKeyId = 1,
                    CorrelationId = tag,
                }),
            new QueueTarget(
                "video",
                ConduitEndpointPolicies.VideoGeneration,
                ConduitMessagingTopology.VideoGenerationEvents,
                typeof(VideoGenerationRequested),
                (i, tag) => new VideoGenerationRequested
                {
                    RequestId = $"bench-{i}",
                    VirtualKeyId = "1",
                    CorrelationId = tag,
                }),
            new QueueTarget(
                "gateway-default",
                Policy: null,
                ConduitMessagingTopology.GatewayCacheInvalidationEvents.Take(1).ToArray(),
                typeof(VirtualKeyUpdated),
                (i, tag) => new VirtualKeyUpdated
                {
                    KeyId = 1,
                    KeyHash = $"bench-{i}",
                    CorrelationId = tag,
                }),
        };

        public static async Task<int> RunAsync(string[] args)
        {
            var options = HarnessOptions.Parse(args);
            if (options is null)
            {
                PrintUsage();
                return 1;
            }

            var target = Targets.FirstOrDefault(t =>
                string.Equals(t.Key, options.Queue, StringComparison.OrdinalIgnoreCase));

            if (target is null)
            {
                Console.Error.WriteLine(
                    $"Unknown queue '{options.Queue}'. Known: {string.Join(", ", Targets.Select(t => t.Key))}.");
                return 1;
            }

            var connectionString = await EnsureBenchDatabaseAsync(options).ConfigureAwait(false);

            Console.WriteLine(
                $"# transport-throughput  queue={target.Key}  messages={options.MessageCount}  " +
                $"handlerCostMs=[{string.Join(",", options.HandlerCostMs)}]  " +
                $"offered={(options.OfferedRate > 0 ? options.OfferedRate.ToString("F0", CultureInfo.InvariantCulture) + "/s" : "saturate")}");
            Console.WriteLine($"# policy: {DescribePolicy(target, options)}");
            Console.WriteLine();

            var results = new List<RunResult>();
            foreach (var cost in options.HandlerCostMs)
            {
                var result = await MeasureAsync(target, connectionString, options, cost).ConfigureAwait(false);
                results.Add(result);
                Console.WriteLine(result.ToLine());
            }

            Console.WriteLine();
            Console.WriteLine(RunResult.Header);
            foreach (var r in results)
            {
                Console.WriteLine(r.ToTableRow());
            }

            return 0;
        }

        private static async Task<RunResult> MeasureAsync(
            QueueTarget target,
            string connectionString,
            HarnessOptions options,
            int handlerCostMs)
        {
            var collector = new DeliveryCollector(handlerCostMs);

            using var host = BuildHost(target, connectionString, collector, options);
            await host.StartAsync().ConfigureAwait(false);

            try
            {
                var bus = host.Services.GetRequiredService<IMessageBus>();

                // Warm up until the listener is actually delivering. On an exclusive listener
                // (spend, image) nothing is delivered until the durability agent assigns the
                // queue to a node, which takes seconds on a cold host; measuring through that
                // window would charge one-off leader election to every message in the run.
                collector.BeginMeasurement(options.WarmupCount);
                await PublishAllAsync(bus, target, options, options.WarmupCount).ConfigureAwait(false);
                if (!await collector.WaitForCompletionAsync(options.Timeout).ConfigureAwait(false))
                {
                    Console.Error.WriteLine(
                        $"# warm-up did not drain ({collector.Delivered}/{options.WarmupCount}) — listener may never have started");
                }

                collector.BeginMeasurement(options.MessageCount);

                var publishStart = Stopwatch.GetTimestamp();
                if (options.OfferedRate > 0)
                {
                    // Fixed offered load: the queue stays shallow, so publish→handle latency
                    // reflects transport overhead rather than backlog depth.
                    await PublishAtRateAsync(bus, target, options).ConfigureAwait(false);
                }
                else
                {
                    // Saturation: publish the whole batch up front so the listener is never
                    // starved and what we time is the transport's ceiling rather than the
                    // producer's. The achieved publish rate is reported too — if it ever lands
                    // near the delivery rate, the run was producer-bound and the delivery
                    // number is a floor, not a ceiling.
                    await PublishAllAsync(bus, target, options, options.MessageCount).ConfigureAwait(false);
                }

                var publishSeconds = Elapsed(publishStart);
                var drained = await collector.WaitForCompletionAsync(options.Timeout).ConfigureAwait(false);

                return new RunResult(
                    target.Key,
                    handlerCostMs,
                    options.OfferedRate,
                    Published: options.MessageCount,
                    Delivered: collector.Delivered,
                    Complete: drained,
                    PublishRate: options.MessageCount / publishSeconds,
                    DeliveryRate: collector.DeliveryRatePerSecond,
                    LatencyP50Ms: collector.Percentile(0.50),
                    LatencyP95Ms: collector.Percentile(0.95),
                    LatencyP99Ms: collector.Percentile(0.99),
                    MaxObservedConcurrency: collector.MaxObservedConcurrency);
            }
            finally
            {
                await host.StopAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Publishes <see cref="HarnessOptions.MessageCount"/> messages paced to
        /// <see cref="HarnessOptions.OfferedRate"/>, on an absolute schedule so a slow publish
        /// does not push the whole run late.
        /// </summary>
        private static async Task PublishAtRateAsync(IMessageBus bus, QueueTarget target, HarnessOptions options)
        {
            var interval = 1.0 / options.OfferedRate;
            var start = Stopwatch.GetTimestamp();
            var pending = new List<Task>(options.MessageCount);

            for (var i = 0; i < options.MessageCount; i++)
            {
                var dueSeconds = interval * i;
                var behind = dueSeconds - (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
                if (behind > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(behind)).ConfigureAwait(false);
                }

                var index = i;
                var tag = Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture);
                pending.Add(bus.PublishAsync(target.Factory(index, tag)).AsTask());
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }

        private static async Task PublishAllAsync(IMessageBus bus, QueueTarget target, HarnessOptions options, int count)
        {
            // Bounded fan-out: each publish is an outbox INSERT, so a little parallelism is
            // needed to get ahead of the listener without opening an unbounded connection storm.
            using var gate = new SemaphoreSlim(options.PublishParallelism);
            var tasks = new List<Task>(count);

            for (var i = 0; i < count; i++)
            {
                var index = i;
                await gate.WaitAsync().ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var tag = Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture);
                        await bus.PublishAsync(target.Factory(index, tag)).ConfigureAwait(false);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private static IHost BuildHost(
            QueueTarget target,
            string connectionString,
            DeliveryCollector collector,
            HarnessOptions options)
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConduitLLM:Messaging:Wolverine:Transport"] = "Postgresql",
                ["ConduitLLM:Messaging:Wolverine:AutoProvision"] = "true",
            };

            return Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration(cfg => cfg.AddInMemoryCollection(settings))
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    // Wolverine logs an Information line per delivery; at saturation that
                    // console I/O would itself become the bottleneck being measured.
                    logging.SetMinimumLevel(LogLevel.Warning);
                })
                .ConfigureServices(services =>
                {
                    services.AddSingleton(collector);
                    services.AddWolverineEventBus();

                    foreach (var eventType in target.EventTypes)
                    {
                        services.AddScoped(
                            typeof(IEventHandler<>).MakeGenericType(eventType),
                            typeof(BenchHandler<>).MakeGenericType(eventType));
                    }
                })
                .AddConduitWolverine(
                    new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
                    connectionString,
                    "conduit-bench",
                    opts =>
                    {
                        // Benchmark-only event sets are selected at runtime and therefore
                        // cannot use either production host's committed adapter registry.
                        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
                        opts.UseRuntimeCompilation();

                        AddBenchmarkBridges(opts, target.EventTypes);

                        // Production routing, verbatim.
                        opts.ApplyConduitPublishRouting();

                        if (target.Policy is { } policy)
                        {
                            // A retune is expressed by amending the descriptor, so the
                            // measurement still goes through the production translation in
                            // WolverineEndpointPolicy rather than a parallel copy of it.
                            if (options.ReceiveBatchOverride is { } batch)
                            {
                                policy = policy with { PrefetchCount = batch };
                            }

                            opts.ListenWithPolicy(policy, target.EventTypes);
                        }
                        else
                        {
                            // Mirrors ListenAsConduitGateway's default-queue listener.
                            opts.ListenToPostgresqlQueue(ConduitMessagingTopology.GatewayEventsQueue)
                                .PollingInterval(TimeSpan.FromMilliseconds(250))
                                .MaximumMessagesToReceive(50);
                        }
                    })
                .Build();
        }

        /// <summary>
        /// Creates the isolated benchmark database if it does not exist and returns its
        /// connection string.
        /// </summary>
        private static async Task<string> EnsureBenchDatabaseAsync(HarnessOptions options)
        {
            var adminBuilder = new NpgsqlConnectionStringBuilder(options.AdminConnectionString);
            var database = options.Database;

            await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await admin.OpenAsync().ConfigureAwait(false);

                await using var exists = new NpgsqlCommand(
                    "SELECT 1 FROM pg_database WHERE datname = @name", admin);
                exists.Parameters.AddWithValue("name", database);
                var found = await exists.ExecuteScalarAsync().ConfigureAwait(false) is not null;

                if (!found)
                {
                    // Identifier cannot be parameterized; quote it instead.
                    await using var create = new NpgsqlCommand(
                        $"CREATE DATABASE \"{database.Replace("\"", "\"\"")}\"", admin);
                    await create.ExecuteNonQueryAsync().ConfigureAwait(false);
                    Console.WriteLine($"# created isolated benchmark database '{database}'");
                }
            }

            var target = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Database = database,
            };
            return target.ConnectionString;
        }

        private static string DescribePolicy(QueueTarget target, HarnessOptions options)
        {
            if (target.Policy is not { } p)
            {
                return $"{ConduitMessagingTopology.GatewayEventsQueue} (untuned default: 250ms poll, batch 50)";
            }

            if (options.ReceiveBatchOverride is { } overrideBatch)
            {
                p = p with { PrefetchCount = overrideBatch };
            }

            var ordering = p.ConcurrentMessageLimit == 1
                ? "strict ordering (1 node, sequential)"
                : p.SingleActiveConsumer
                    ? $"exclusive node, parallelism {p.ConcurrentMessageLimit?.ToString(CultureInfo.InvariantCulture) ?? "50 (default)"}"
                    : $"parallelism {p.ConcurrentMessageLimit?.ToString(CultureInfo.InvariantCulture) ?? "Wolverine default"}";

            var batch = p.PrefetchCount?.ToString(CultureInfo.InvariantCulture) ?? "50 (default)";
            return $"{p.Name} — {ordering}; receive batch {batch} per 250ms poll";
        }

        private static double Elapsed(long startTimestamp) =>
            Math.Max((Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency, 1e-6);

        private static void AddBenchmarkBridges(WolverineOptions options, IReadOnlyList<Type> eventTypes)
        {
            if (eventTypes.Contains(typeof(SpendUpdateRequested))) options.AddEventBridge<SpendUpdateRequested>();
            if (eventTypes.Contains(typeof(WebhookDeliveryRequested))) options.AddEventBridge<WebhookDeliveryRequested>();
            if (eventTypes.Contains(typeof(ImageGenerationRequested))) options.AddEventBridge<ImageGenerationRequested>();
            if (eventTypes.Contains(typeof(ImageGenerationCancelled))) options.AddEventBridge<ImageGenerationCancelled>();
            if (eventTypes.Contains(typeof(VideoGenerationRequested))) options.AddEventBridge<VideoGenerationRequested>();
            if (eventTypes.Contains(typeof(VideoGenerationCancelled))) options.AddEventBridge<VideoGenerationCancelled>();
            if (eventTypes.Contains(typeof(VideoProgressCheckRequested))) options.AddEventBridge<VideoProgressCheckRequested>();
            if (eventTypes.Contains(typeof(VirtualKeyUpdated))) options.AddEventBridge<VirtualKeyUpdated>();
        }

        private static void PrintUsage()
        {
            Console.Error.WriteLine("""
                Usage:
                  dotnet run -c Release --project Tests/ConduitLLM.Benchmarks -- transport-throughput \
                      --queue <spend|webhook|image|video|gateway-default> \
                      [--messages 2000] [--handler-cost-ms 0,5,10,25] [--rate 0] \
                      [--warmup 10] [--publish-parallelism 32] [--timeout-seconds 300] \
                      [--database conduit_msgbench] [--admin-connection "<npgsql connstring to 'postgres' db>"]

                Measures sustained delivery throughput and publish->handle latency for one
                tuned queue, using the production endpoint policies. Runs against an isolated
                database so synthetic events never reach a live service's queues (#1223).

                --rate 0 (default) saturates the queue and reports the delivery ceiling.
                --rate N offers a fixed N msg/s and reports latency at that load; use this for
                latency, since at saturation the backlog — not the transport — sets latency.
                """);
        }

        private sealed record QueueTarget(
            string Key,
            EndpointPolicy? Policy,
            IReadOnlyList<Type> EventTypes,
            Type PrimaryEventType,
            Func<int, string, object> Factory);
    }
}
