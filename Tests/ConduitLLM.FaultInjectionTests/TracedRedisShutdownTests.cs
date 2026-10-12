using System.Collections.Concurrent;
using System.Diagnostics;

using ConduitLLM.Configuration.Enums;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Options;
using ConduitLLM.Configuration.Services;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using ConduitLLM.Gateway.Services;
using ConduitLLM.Persistence.Npgsql;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moq;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Trace;
using StackExchange.Redis;

namespace ConduitLLM.FaultInjectionTests;

[Collection(BillingFaultCollection.Name)]
public sealed class TracedRedisShutdownTests(BillingFaultFixture fixture)
{
    [Theory(Timeout = 90_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HostShutdown_DrainsLateSpendBeforeRedisTracingIsDisposed(bool tracingEnabled, bool follower)
    {
        var account = await fixture.SeedAccountAsync();
        await using var dataSource = NpgsqlDataSource.Create(fixture.PostgreSqlConnectionString);
        using var redisFactory = new RedisConnectionFactory(
            Options.Create(new CacheOptions { RedisConnectionString = fixture.RedisConnectionString }),
            NullLogger<RedisConnectionFactory>.Instance);
        var redis = await redisFactory.GetConnectionAsync();
        var serviceName = $"traced-shutdown-{Guid.NewGuid():N}";
        if (follower)
            Assert.True(await redis.GetDatabase().StringSetAsync($"leader:{serviceName}",
                "external-fixture-holder", TimeSpan.FromMinutes(1), When.NotExists));
        var log = new ShutdownLogger();
        var processor = new RedisActivityProcessor();
        var completionCount = 0;
        var callbackCompleted = false;
        using var source = new ActivitySource("Conduit.ShutdownRegression");

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
        builder.Services.AddSingleton(redisFactory);
        builder.Services.AddSingleton<ILogger<BatchSpendUpdateService>>(log);
        builder.Services.AddSingleton<ILogger<LeaderElectedServiceWrapper>>(log);
        builder.Services.AddSingleton<ILogger<RedisVirtualKeyCache>>(log);
        builder.Services.AddLeaderElection();
        builder.Services.AddSingleton<IVirtualKeyCache, RedisVirtualKeyCache>();
        builder.Services.AddSingleton<IHostedService>(provider => new RedisVirtualKeyCacheLifecycleHostedService(
            (RedisVirtualKeyCache)provider.GetRequiredService<IVirtualKeyCache>()));
        builder.Services.AddSingleton(provider =>
        {
            var cache = provider.GetRequiredService<IVirtualKeyCache>();
            var service = new BatchSpendUpdateService(
                provider.GetRequiredService<IServiceScopeFactory>(), redisFactory,
                Options.Create(new BatchSpendingOptions { FlushIntervalSeconds = 3600, MaximumIntervalSeconds = 7200 }),
                log, Mock.Of<IBillingAlertingService>(), runtimeStore: new NpgsqlVirtualKeyRuntimeStore(dataSource));
            service.SpendUpdatesCompleted += (hashes, token) => cache.InvalidateVirtualKeysAsync(hashes, token);
            return service;
        });
        // The late writer stops after the leader worker. The lifecycle adapter's
        // Stopped phase must still drain this accepted work before DI disposal.
        builder.Services.AddSingleton<IHostedService>(provider => new LateSpendWriter(
            provider.GetRequiredService<BatchSpendUpdateService>(), account.KeyId));
        builder.Services.AddSingleton<BatchSpendLeaderWorker>();
        builder.Services.AddLeaderElectedHostedService(
            provider => provider.GetRequiredService<BatchSpendLeaderWorker>(),
            serviceName, ownsInnerService: false);
        builder.Services.AddSingleton<IHostedService, BatchSpendLifecycleHostedService>();
        if (tracingEnabled)
        {
            builder.Services.AddSingleton<TracerProvider>(_ => Sdk.CreateTracerProviderBuilder()
                .AddSource(source.Name)
                .AddRedisInstrumentation(redis, options => options.FlushInterval = TimeSpan.FromMilliseconds(10))
                .AddProcessor(processor)
                .Build());
        }

        using var host = builder.Build();
        var batch = host.Services.GetRequiredService<BatchSpendUpdateService>();
        batch.SpendUpdatesCompleted += async (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(processor.IsShutdown, "tracing must remain alive through the final cache callback");
            Interlocked.Increment(ref completionCount);
            await Task.Yield();
            callbackCompleted = true;
        };

        var stopped = false;
        try
        {
            await host.StartAsync();
            await (follower ? log.FollowerObserved.Task : log.StartupFlushCompleted.Task)
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(!follower, batch.IsHealthy);
            // Resolve tracing last, so DI disposes it before the previously resolved
            // batch singleton. Old disposal-time flushing ran against this ordering.
            var tracer = tracingEnabled ? host.Services.GetRequiredService<TracerProvider>() : null;
            using (source.StartActivity("queue-before-shutdown"))
                await batch.QueueSpendUpdateAsync(account.KeyId, 3m);
            if (tracingEnabled)
                await processor.RedisObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

            using (source.StartActivity("host-stop"))
                await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
            stopped = true;

            Assert.True(callbackCompleted);
            Assert.Equal(1, completionCount);
            Assert.False(processor.IsShutdown);
            await fixture.AssertAccountedForAsync(account.GroupId, 10m);
            await using (var db = fixture.CreateDbContext())
            {
                Assert.Equal(90m, await db.VirtualKeyGroups.Where(group => group.Id == account.GroupId)
                    .Select(group => group.Balance).SingleAsync());
                Assert.Equal(1, await db.VirtualKeyGroupTransactions.CountAsync(transaction =>
                    transaction.VirtualKeyGroupId == account.GroupId && transaction.TransactionType == TransactionType.Debit));
            }
            tracer?.ForceFlush();
            var disposalException = Record.Exception(host.Dispose);
            Assert.Null(disposalException);
            Assert.Equal(tracingEnabled, processor.IsShutdown);
            Assert.Empty(log.Exceptions);
            Assert.Equal(1, completionCount);
            await fixture.AssertAccountedForAsync(account.GroupId, 10m);
        }
        finally
        {
            if (!stopped)
                await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private sealed class LateSpendWriter(BatchSpendUpdateService batch, int keyId) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => batch.QueueSpendUpdateAsync(keyId, 7m);
    }

    private sealed class RedisActivityProcessor : BaseProcessor<Activity>
    {
        private int _shutdown;
        public bool IsShutdown => Volatile.Read(ref _shutdown) != 0;
        public TaskCompletionSource RedisObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void OnEnd(Activity activity)
        {
            if (activity.Source.Name.Contains("Redis", StringComparison.OrdinalIgnoreCase))
                RedisObserved.TrySetResult();
        }
        protected override bool OnShutdown(int timeoutMilliseconds)
        {
            Interlocked.Exchange(ref _shutdown, 1);
            return true;
        }
    }

    private sealed class ShutdownLogger : ILogger<BatchSpendUpdateService>, ILogger<LeaderElectedServiceWrapper>,
        ILogger<RedisVirtualKeyCache>
    {
        public TaskCompletionSource StartupFlushCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FollowerObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<Exception> Exceptions { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Exceptions.Enqueue(exception);
            var message = formatter(state, exception);
            if (message.Contains("is not leader, waiting", StringComparison.Ordinal))
                FollowerObserved.TrySetResult();
            if (message == "No pending spend updates to flush" ||
                message.StartsWith("Flushed ", StringComparison.Ordinal) &&
                message.EndsWith("pending updates from previous session", StringComparison.Ordinal))
                StartupFlushCompleted.TrySetResult();
            else if (message == "Error flushing pending updates on startup")
                StartupFlushCompleted.TrySetException(exception ?? new InvalidOperationException(message));
        }
    }
}
