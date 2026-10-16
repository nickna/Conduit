#if !CONDUIT_NATIVE_AOT
// Managed validation reference cases; the EF-free published native metrics
// probe retains equivalent typed-store input and cancellation coverage.
using AwesomeAssertions;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Repositories;
using ConduitLLM.Persistence.Interfaces;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Npgsql;

using Xunit;

namespace ConduitLLM.IntegrationTests.Tests;

public sealed partial class GatewayMetricsPersistenceParityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidMetricsInputsValidateBeforeCancellationContextPoolAndSql(bool typedNpgsql)
    {
        using var counter = new InputCommandCounter();
        await InMetricsInputSchemaAsync(async (schema, source, token) =>
        {
            var factory = new InputContextFactory(source);
            IGatewayMetricsStore store = typedNpgsql ? new NpgsqlGatewayMetricsStore(source) : new EfGatewayMetricsStore(factory);
            NpgsqlConnection? held = null;
            Task? control = null;
            var invalidTasks = new List<Task>();
            using var normal = new CancellationTokenSource();
            using var preCancelled = new CancellationTokenSource();
            using var controlCaller = new CancellationTokenSource();
            preCancelled.Cancel();
            await IntegrationProofCleanup.RunAsync(async () =>
            {
                held = await source.OpenConnectionAsync(token);
                control = store.GetGenerationTaskMetricsAsync("image_generation", MetricsFrom, controlCaller.Token);
                control.IsCompleted.Should().BeFalse("the valid control cannot acquire the exact held sole pool slot");
                var factoryCalls = factory.Calls;
                factoryCalls.Should().Be(typedNpgsql ? 0 : 1);
                counter.Commands.Should().Be(0);
                factory.ForbidEntry = true;
                foreach (var callerToken in new[] { normal.Token, preCancelled.Token })
                {
                    foreach (var invalid in new[] { null, "", " \t\r\n", "\u00a0\u2003\u202f" })
                        await AssertMetricsArgumentAsync(
                            () => store.GetGenerationTaskMetricsAsync(invalid!, MetricsFrom, callerToken),
                            invalid is null ? typeof(ArgumentNullException) : typeof(ArgumentException),
                            "taskType", null, invalidTasks);
                    foreach (var limit in new[] { 0, -1, int.MinValue })
                        await AssertMetricsArgumentAsync(
                            () => store.GetTopVirtualKeySpendAsync(MetricsFrom, MetricsInstant, limit, callerToken),
                            typeof(ArgumentOutOfRangeException), "limit", limit, invalidTasks);
                }
                factory.Calls.Should().Be(factoryCalls, "invalid arguments must not create an EF context");
                counter.Commands.Should().Be(0, "neither invalid calls nor the pool-blocked control can dispatch SQL");
                control.IsCompleted.Should().BeFalse("the valid control remains blocked throughout validation");
                held.State.Should().Be(System.Data.ConnectionState.Open);
                _output.WriteLine($"metrics-invalid-guards typed={typedNpgsql} cases=14 contextEntries=0 sqlCommands=0 heldPid={held.ProcessID}");
            }, () =>
            {
                var drains = new List<Func<CancellationToken, Task>>
                {
                    async cleanup =>
                    {
                        normal.Cancel();
                        controlCaller.Cancel();
                        if (control is not null) await JoinMetricsInputProducerAsync(control, cleanup);
                    }
                };
                drains.AddRange(invalidTasks.Select<Task, Func<CancellationToken, Task>>(pending =>
                    cleanup => JoinMetricsInputProducerAsync(pending, cleanup)));
                drains.Add(async cleanup => { if (held is not null) await held.DisposeAsync().AsTask().WaitAsync(cleanup); });
                drains.Add(async cleanup => { if (control is not null) await JoinMetricsInputProducerAsync(control, cleanup); });
                drains.AddRange(invalidTasks.Select<Task, Func<CancellationToken, Task>>(pending =>
                    cleanup => JoinMetricsInputProducerAsync(pending, cleanup)));
                drains.Add(async cleanup => { factory.ForbidEntry = false; await AssertMetricsPoolReusableAsync(source, cleanup); });
                return IntegrationProofCleanup.DrainIndependentlyAsync(drains, TimeSpan.FromSeconds(5),
                    "Metrics guard control/invalid producers and sole lease could not all be drained.");
            }, "Metrics input validation and producer cleanup failed.");
            await AssertMetricsReadAsync(store, MetricsRead.Generation, token);
            await AssertMetricsReadAsync(store, MetricsRead.Spend, token);
            counter.Commands.Should().BeGreaterThan(0, "the SQL counter must observe real recovery commands");
        }, counter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidMetricsInputsPreserveOriginalStringsAndAcceptMaximumPositiveLimit(bool typedNpgsql)
    {
        await InMetricsInputSchemaAsync(async (schema, source, token) =>
        {
            var store = CreateMetricsStore(source, typedNpgsql);
            await AssertMetricsReadAsync(store, MetricsRead.Generation, token);
            foreach (var value in new[] { " image_generation ", "IMAGE_GENERATION", "generation_雪" })
                (await store.GetGenerationTaskMetricsAsync(value, MetricsFrom, token)).Should().BeEmpty();
            var all = await store.GetTopVirtualKeySpendAsync(MetricsFrom, MetricsInstant, 100, token);
            var maximum = await store.GetTopVirtualKeySpendAsync(MetricsFrom, MetricsInstant, int.MaxValue, token);
            maximum.Should().BeEquivalentTo(all);
            maximum.Select(row => row.TotalSpend).Should().Equal(all.Select(row => row.TotalSpend));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var generationCancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.GetGenerationTaskMetricsAsync("image_generation", MetricsFrom, cancelled.Token));
            Assert.Equal(cancelled.Token, generationCancellation.CancellationToken);
            var spendCancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.GetTopVirtualKeySpendAsync(MetricsFrom, MetricsInstant, int.MaxValue, cancelled.Token));
            Assert.Equal(cancelled.Token, spendCancellation.CancellationToken);
            await AssertMetricsPoolReusableAsync(source, token);
            await AssertMetricsReadAsync(store, MetricsRead.Spend, token);
        });
    }

    private static async Task AssertMetricsArgumentAsync(Func<Task> invoke, Type expectedType,
        string paramName, int? actualValue, List<Task> producers)
    {
        Task pending;
        try { pending = invoke(); }
        catch (Exception error) { pending = Task.FromException(error); }
        producers.Add(pending);
        pending.IsCompleted.Should().BeTrue("argument validation must finish before any asynchronous pool wait");
        var observed = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        if (observed is TimeoutException) throw new TimeoutException("Metrics validation exceeded the independent test budget.", observed);
        observed.Should().NotBeNull();
        observed!.GetType().Should().Be(expectedType);
        var argument = Assert.IsAssignableFrom<ArgumentException>(observed);
        argument.ParamName.Should().Be(paramName);
        if (actualValue.HasValue)
        {
            var outOfRange = Assert.IsType<ArgumentOutOfRangeException>(observed);
            outOfRange.ActualValue.Should().BeOfType<int>().Which.Should().Be(actualValue.Value);
        }
    }

    private async Task InMetricsInputSchemaAsync(Func<string, NpgsqlDataSource, CancellationToken, Task> proof,
        ILoggerFactory? loggerFactory = null)
    {
        var schema = $"gm_input_{Guid.NewGuid():N}";
        NpgsqlDataSource? source = null;
        using var setup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await IntegrationProofCleanup.RunAsync(async () =>
        {
            await CreateMetricsMatrixAsync(schema, seed: true, setup.Token);
            var builder = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(WithSearchPath(schema))
            {
                ApplicationName = schema, MinPoolSize = 0, MaxPoolSize = 1,
                Timeout = 5, CommandTimeout = 10, CancellationTimeout = 1000
            }.ConnectionString);
            if (loggerFactory is not null) builder.UseLoggerFactory(loggerFactory).EnableParameterLogging(false);
            source = builder.Build();
            await proof(schema, source, setup.Token);
        }, () => IntegrationProofCleanup.DrainIndependentlyAsync(
            [async cleanup => { if (source is not null) await source.DisposeAsync().AsTask().WaitAsync(cleanup); },
             async cleanup =>
                {
                    await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
                        { Pooling = false, Timeout = 5, CommandTimeout = 5 }.ConnectionString);
                    await connection.OpenAsync(cleanup);
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE";
                    await command.ExecuteNonQueryAsync(cleanup);
                },
             async cleanup =>
                {
                    await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_fixture.ConnectionString)
                        { Pooling = false, Timeout = 5, CommandTimeout = 5 }.ConnectionString);
                    await connection.OpenAsync(cleanup);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT count(*) FROM pg_catalog.pg_namespace WHERE nspname = @schema";
                    command.Parameters.AddWithValue("schema", schema);
                    (await command.ExecuteScalarAsync(cleanup)).Should().Be(0L);
                    _output.WriteLine($"metrics-input-schema-absent schema={schema}");
                }], TimeSpan.FromSeconds(5), "Metrics input data source/schema owners could not all be cleaned up."),
            "Metrics input proof and owned cleanup failed.");
    }

    private static async Task JoinMetricsInputProducerAsync(Task pending, CancellationToken token)
    {
        try { await pending.WaitAsync(token); }
        // Observed fault/cancellation is already part of the work outcome. A
        // cleanup deadline on a still-running producer must remain a failure.
        catch (Exception) when (pending.IsCompleted) { }
    }

    private sealed class InputContextFactory(NpgsqlDataSource source) : IDbContextFactory<ConduitDbContext>
    {
        private readonly DbContextOptions<ConduitDbContext> _options =
            new DbContextOptionsBuilder<ConduitDbContext>().UseNpgsql(source).Options;
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool ForbidEntry { get; set; }
        public ConduitDbContext CreateDbContext()
        {
            ObserveEntry();
            return new ConduitDbContext(_options);
        }
        public Task<ConduitDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            ObserveEntry();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ConduitDbContext(_options));
        }
        private void ObserveEntry()
        {
            Interlocked.Increment(ref _calls);
            if (ForbidEntry) throw new InvalidOperationException("Invalid metrics input entered the EF context factory.");
        }
    }

    private sealed class InputCommandCounter : ILoggerFactory, ILogger
    {
        private int _commands;
        public int Commands => Volatile.Read(ref _commands);
        public ILogger CreateLogger(string categoryName) => categoryName == "Npgsql.Command"
            ? this : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == NpgsqlEventId.ExecutingCommand) Interlocked.Increment(ref _commands);
        }
        public void Dispose() { }
    }
}
#endif
