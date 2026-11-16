using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

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
    /// support per tuned endpoint, with handler completion and SQL-observed durable settlement.
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
                    RequestId = $"bench-{tag}-{i}",
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

        /// <summary>
        /// Verifies the published benchmark's dynamic compiler closure without opening a database.
        /// A normal build can contain dependencies that a publish excludes, so CI runs this
        /// from the same published directory used by the queue sweep.
        /// </summary>
        public static int VerifyRuntimeCompilation()
        {
            var options = new WolverineOptions();
            ConfigureRuntimeCompilation(options);
            using var services = options.Services.BuildServiceProvider();
            var compiler = services.GetRequiredService<IAssemblyGenerator>();
            var assembly = compiler.Generate("""
                namespace ConduitLLM.Benchmarks.Generated;
                public static class TransportRuntimeCompilationProbe
                {
                    public static int Run() => 6 * 7;
                }
                """);
            var type = assembly.GetType(
                "ConduitLLM.Benchmarks.Generated.TransportRuntimeCompilationProbe", throwOnError: true)!;
            var run = type.GetMethod("Run", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                ?? throw new InvalidOperationException("The runtime compiler did not emit the expected probe method.");
            var value = run.CreateDelegate<Func<int>>()();
            if (value != 42)
                throw new InvalidOperationException("The runtime-compiled probe returned an unexpected value.");

            Console.WriteLine($"Runtime compilation smoke passed: mode={options.CodeGeneration.TypeLoadMode}; " +
                $"compiler={compiler.GetType().Assembly.GetName().Name}; generatedValue={value}.");
            return 0;
        }

        private static void ConfigureRuntimeCompilation(WolverineOptions options)
        {
            options.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
            options.UseRuntimeCompilation();
        }

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

            if (File.Exists(options.OutputPath))
            {
                Console.Error.WriteLine("Refuse to overwrite an earlier benchmark report.");
                return 1;
            }
            var report = new TransportBenchmarkReport { DatabaseLatencyLabel = options.DatabaseLatencyLabel };
            try
            {
                if (target.Policy?.ConcurrentMessageLimit == 1 && options.PublishParallelism != 1)
                    throw new InvalidOperationException("Strict ordering validation requires publish-parallelism1.");
                var admin = new NpgsqlConnectionStringBuilder(options.AdminConnectionString!);
                if (admin.Database != "postgres") throw new InvalidOperationException("The explicit admin connection must target the private fixture's postgres maintenance database.");
                report.DatabaseHost = admin.Host;
                report.DatabasePort = admin.Port;
                Console.WriteLine($"# policy: {DescribePolicy(target, options)}; synthetic handlers; real inbox settlement");
                for (var index = 0; index < options.HandlerCostMs.Count; index++)
                {
                    var database = options.HandlerCostMs.Count == 1 ? options.Database : $"{options.Database}_{index}";
                    var connectionString = await EnsureBenchDatabaseAsync(options, database).ConfigureAwait(false);
                    var result = await MeasureAsync(target, connectionString, options, options.HandlerCostMs[index]).ConfigureAwait(false);
                    report.Results.Add(result);
                    Console.WriteLine(result.ToLine());
                    if (!result.Complete) break;
                }
                report.Passed = RunResult.AllComplete(report.Results) && report.Results.Count == options.HandlerCostMs.Count;
            }
            catch (Exception error)
            {
                report.Error = $"{error.GetType().Name}: {error.Message}";
                Console.Error.WriteLine(report.Error);
            }
            var destination = Path.GetFullPath(options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temporary = destination + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, TransportBenchmarkJsonContext.Default.TransportBenchmarkReport)).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite: false);
            return report.Passed ? 0 : 1;
        }

        private static async Task<RunResult> MeasureAsync(
            QueueTarget target,
            string connectionString,
            HarnessOptions options,
            int handlerCostMs)
        {
            var collector = new DeliveryCollector(handlerCostMs);
            DeliverySnapshot? warmup = null;
            string? failure = null;
            var warmupComplete = false;
            StoreSetup? setup = null;
            double publishRate = 0, elapsedSeconds = 0;
            var rtt = await MeasureRttAsync(connectionString).ConfigureAwait(false);
            using var host = BuildHost(target, connectionString, collector, options);
            await host.StartAsync().ConfigureAwait(false);
            try
            {
                var bus = host.Services.GetRequiredService<IMessageBus>();
                warmupComplete = await RunPhaseAsync(bus, target, connectionString, collector, options,
                    options.WarmupCount, offeredRate: 0, setupObserved: state => setup = state).ConfigureAwait(false);
                warmup = collector.Snapshot();
                if (!warmupComplete) throw new InvalidOperationException("Warmup did not finish and durably settle; measurement was not started.");
                var start = Stopwatch.GetTimestamp();
                var completed = await RunPhaseAsync(bus, target, connectionString, collector, options, options.MessageCount, options.OfferedRate,
                    rate => publishRate = rate).ConfigureAwait(false);
                elapsedSeconds = Elapsed(start);
                if (!completed) failure = "Measurement did not finish and durably settle before its deadline.";
            }
            catch (Exception error) { failure = $"{error.GetType().Name}: {error.Message}"; }
            finally
            {
                try { await host.StopAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false); }
                catch (Exception error) { failure = $"Host stop failed: {error.GetType().Name}: {error.Message}"; }
            }
            var measurement = collector.Snapshot();
            warmup ??= measurement;
            var records = measurement.Deliveries.Where(d => d.SettlementObservedTicks > 0).ToArray();
            var first = records.Length > 0 ? records.Min(d => d.StartedTicks) : 0;
            double Rate(long last) => first > 0 && last > first ? records.Length * (double)Stopwatch.Frequency / (last - first) : 0;
            var latency = records.Select(d => (d.SettlementObservedTicks - d.PublishedTicks) * 1000d / Stopwatch.Frequency).Order().ToArray();
            double Percentile(double q) => latency.Length == 0 ? 0 : latency[Math.Clamp((int)Math.Ceiling(q * latency.Length) - 1, 0, latency.Length - 1)];
            var batch = options.ReceiveBatchOverride ?? target.Policy?.PrefetchCount ?? 50;
            return new RunResult(target.Key, handlerCostMs, options.OfferedRate, batch,
                warmupComplete && measurement.Complete && failure is null, warmupComplete, publishRate,
                records.Length > 0 ? Rate(records.Max(d => d.FinishedTicks)) : 0,
                records.Length > 0 ? Rate(records.Max(d => d.SettlementObservedTicks)) : 0,
                elapsedSeconds, Percentile(.50), Percentile(.95), Percentile(.99),
                options.OfferedRate == 0 && measurement.StoreSamples.Count(s => s.Ready >= batch) >= 3,
                rtt, warmup, measurement, failure, setup);
        }

        private static async Task<bool> RunPhaseAsync(IMessageBus bus, QueueTarget target, string connectionString,
            DeliveryCollector collector, HarnessOptions options, int count, double offeredRate,
            Action<double>? published = null, Action<StoreSetup>? setupObserved = null)
        {
            collector.BeginMeasurement(count, requireOrdering: target.Policy?.ConcurrentMessageLimit == 1);
            using var phase = new CancellationTokenSource(options.Timeout);
            Task? sampling = null;
            var start = Stopwatch.GetTimestamp();
            try
            {
                if (setupObserved is not null)
                    await WaitForStoreSchemaAsync(connectionString, target, state =>
                    {
                        setupObserved(state);
                        Console.WriteLine($"# observer readiness: polls={state.Polls} elapsedMs={state.ElapsedMilliseconds:F1} ready={state.Ready} relations={string.Join(',', state.RequiredRelations)}");
                    }, phase.Token).ConfigureAwait(false);
                sampling = SampleStoreAsync(connectionString, target, collector, phase.Token);
                var publishing = PublishAsync(bus, target, collector, options, count, offeredRate);
                var first = await Task.WhenAny(publishing, sampling).WaitAsync(phase.Token).ConfigureAwait(false);
                if (first == sampling) await sampling.ConfigureAwait(false);
                await publishing.ConfigureAwait(false);
                published?.Invoke(count / Elapsed(start));
                var remaining = options.Timeout - TimeSpan.FromSeconds(Elapsed(start));
                if (remaining <= TimeSpan.Zero) return false;
                var completion = collector.WaitForCompletionAsync(remaining);
                first = await Task.WhenAny(completion, sampling).ConfigureAwait(false);
                if (first == sampling) await sampling.ConfigureAwait(false);
                return await completion.ConfigureAwait(false);
            }
            finally
            {
                await phase.CancelAsync().ConfigureAwait(false);
                if (sampling is not null)
                {
                    try { await sampling.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (phase.IsCancellationRequested) { }
                }
            }
        }

        private static async Task WaitForStoreSchemaAsync(string connectionString, QueueTarget target,
            Action<StoreSetup> observed, CancellationToken cancellationToken)
        {
            var name = (target.Policy?.Name ?? ConduitMessagingTopology.GatewayEventsQueue).Replace('-', '_');
            string[] required = [$"wolverine_queues.wolverine_queue_{name}", $"wolverine_queues.wolverine_queue_{name}_scheduled",
                "wolverine_conduit_bench.wolverine_incoming_envelopes", "wolverine_conduit_bench.wolverine_outgoing_envelopes",
                "wolverine_conduit_bench.wolverine_dead_letters"];
            await using var connection = new NpgsqlConnection(connectionString);
            await StoreSchemaReadiness.WaitAsync(required, async token =>
            {
                if (connection.State != System.Data.ConnectionState.Open)
                    await connection.OpenAsync(token).ConfigureAwait(false);
                await using var command = new NpgsqlCommand("SELECT name FROM unnest(@names::text[]) AS name WHERE to_regclass(name) IS NOT NULL ORDER BY name", connection) { CommandTimeout = 5 };
                command.Parameters.AddWithValue("names", required);
                var present = new List<string>();
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) present.Add(reader.GetString(0));
                return present.ToArray();
            }, observed, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Publishes <see cref="HarnessOptions.MessageCount"/> messages paced to
        /// <see cref="HarnessOptions.OfferedRate"/>, on an absolute schedule so a slow publish
        /// does not push the whole run late.
        /// </summary>
        private static async Task PublishAsync(IMessageBus bus, QueueTarget target, DeliveryCollector collector,
            HarnessOptions options, int count, double offeredRate)
        {
            var start = Stopwatch.GetTimestamp();
            var pending = new List<Task>(count);
            using var gate = new SemaphoreSlim(options.PublishParallelism);
            async Task SendAsync(object message)
            {
                try { await bus.PublishAsync(message).ConfigureAwait(false); }
                finally { gate.Release(); }
            }
            for (var i = 0; i < count; i++)
            {
                if (offeredRate > 0)
                {
                    var wait = i / offeredRate - Elapsed(start);
                    if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait)).ConfigureAwait(false);
                }
                await gate.WaitAsync().ConfigureAwait(false);
                var message = target.Factory(i, collector.PhaseId);
                collector.RegisterPublished(message, i, Stopwatch.GetTimestamp());
                pending.Add(SendAsync(message));
            }
            await Task.WhenAll(pending).ConfigureAwait(false);
        }

        private static async Task<double[]> MeasureRttAsync(string connectionString)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            var samples = new double[20];
            for (var i = 0; i < samples.Length; i++)
            {
                await using var command = new NpgsqlCommand("SELECT 1", connection);
                var start = Stopwatch.GetTimestamp();
                await command.ExecuteScalarAsync().ConfigureAwait(false);
                samples[i] = Elapsed(start) * 1000;
            }
            return samples;
        }

        private static async Task SampleStoreAsync(string connectionString, QueueTarget target,
            DeliveryCollector collector, CancellationToken cancellationToken)
        {
            var name = (target.Policy?.Name ?? ConduitMessagingTopology.GatewayEventsQueue).Replace('-', '_');
            if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z_]+$"))
                throw new InvalidOperationException("Unexpected benchmark queue identifier.");
            var sql = $"""
                SELECT
                  (SELECT count(*) FROM wolverine_queues.wolverine_queue_{name}),
                  (SELECT count(*) FROM wolverine_queues.wolverine_queue_{name}_scheduled),
                  (SELECT count(*) FROM wolverine_conduit_bench.wolverine_incoming_envelopes WHERE status<>'Handled'),
                  (SELECT count(*) FROM wolverine_conduit_bench.wolverine_outgoing_envelopes),
                  (SELECT count(*) FROM wolverine_conduit_bench.wolverine_dead_letters),
                  (SELECT coalesce(array_agg(id),ARRAY[]::uuid[]) FROM wolverine_conduit_bench.wolverine_incoming_envelopes
                   WHERE status='Handled' AND id=ANY(@ids))
                """;
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                while (true)
                {
                    await using (var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 5 })
                    {
                        command.Parameters.AddWithValue("ids", collector.PendingTransportIds());
                        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            throw new InvalidOperationException("Missing store observation row.");
                        collector.ObserveStore(Stopwatch.GetTimestamp(), reader.GetFieldValue<Guid[]>(5),
                            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
                    }
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                collector.Fail($"Store observation failed: {error.GetType().Name}: {error.Message}");
                throw;
            }
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
                        ConfigureRuntimeCompilation(opts);

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
        private static async Task<string> EnsureBenchDatabaseAsync(HarnessOptions options, string database)
        {
            var adminBuilder = new NpgsqlConnectionStringBuilder(options.AdminConnectionString!);

            await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await admin.OpenAsync().ConfigureAwait(false);

                await using var exists = new NpgsqlCommand(
                    "SELECT 1 FROM pg_database WHERE datname = @name", admin);
                exists.Parameters.AddWithValue("name", database);
                var found = await exists.ExecuteScalarAsync().ConfigureAwait(false) is not null;

                if (found) throw new InvalidOperationException("Refuse an existing benchmark database; use a fresh owned name.");
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
                await create.ExecuteNonQueryAsync().ConfigureAwait(false);
                Console.WriteLine($"# created isolated benchmark database '{database}'");
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
                      [--receive-batch 10] [--database conduit_msgbench_<unique>] [--output <fresh.json>] \
                      --admin-connection "<explicit private fixture connection to 'postgres' db>"

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
