using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ConduitLLM.Configuration;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Options;
using ConduitLLM.Core.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Coordinates connection pool warming across multiple instances using
    /// distributed locking and pub/sub signaling to prevent thundering herd effects.
    ///
    /// Unlike leader election where only the leader runs, coordinated warming ensures
    /// ALL instances warm their pools, but in a staggered manner:
    /// 1. One instance acquires a lock and warms first (leader)
    /// 2. After warming, it publishes a signal via Redis Pub/Sub
    /// 3. Other instances wait for the signal, then warm their own pools
    /// </summary>
    public class CoordinatedConnectionPoolWarmer : IHostedService, IDisposable
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IDistributedLockProvider? _lockService;
        private readonly IConnectionMultiplexer? _redis;
        private readonly ILogger<CoordinatedConnectionPoolWarmer> _logger;
        private readonly ConnectionPoolWarmingOptions _options;
        private readonly string _instanceId;
        private readonly string _serviceType;
        private readonly int _connectionsToWarm;
        private readonly CancellationTokenSource _shutdown = new();
        private Task? _warmingTask;

        /// <summary>
        /// Initializes a new instance of the <see cref="CoordinatedConnectionPoolWarmer"/> class.
        /// </summary>
        /// <param name="serviceProvider">Service provider for creating scoped services.</param>
        /// <param name="lockService">Distributed lock service for coordination (optional).</param>
        /// <param name="redis">Redis connection for pub/sub signaling (optional).</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="options">Warming configuration options.</param>
        /// <param name="serviceType">Service type (CoreAPI, AdminAPI) for isolation.</param>
        public CoordinatedConnectionPoolWarmer(
            IServiceProvider serviceProvider,
            IDistributedLockProvider? lockService,
            IConnectionMultiplexer? redis,
            ILogger<CoordinatedConnectionPoolWarmer> logger,
            ConnectionPoolWarmingOptions options,
            string serviceType)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _lockService = lockService;
            _redis = redis;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _serviceType = serviceType ?? "Default";

            // Generate unique instance ID for logging and signal filtering
            _instanceId = $"{Environment.MachineName}:{Process.GetCurrentProcess().Id}:{Guid.NewGuid():N}";

            // Determine number of connections to warm based on service type
            _connectionsToWarm = DetermineConnectionsToWarm(_serviceType);
        }

        /// <summary>
        /// Starts the coordinated connection pool warming process.
        /// </summary>
        public Task StartAsync(CancellationToken cancellationToken)
            => _warmingTask = StartCoreAsync(cancellationToken);

        private async Task StartCoreAsync(CancellationToken cancellationToken)
        {
            using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token,
                _serviceProvider.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None);
            cancellationToken = work.Token;
            cancellationToken.ThrowIfCancellationRequested();
            if (_connectionsToWarm <= 0)
            {
                _logger.LogInformation(
                    "Connection pool warming skipped for {ServiceType} (0 connections configured)",
                    _serviceType);
                return;
            }

            _logger.LogInformation(
                "Starting coordinated connection pool warming for {ServiceType} " +
                "(Instance: {InstanceId}, Connections: {ConnectionCount})",
                _serviceType, _instanceId, _connectionsToWarm);

            try
            {
                var canCoordinate = _options.EnableCoordinatedWarming &&
                                    _redis != null &&
                                    _lockService != null;

                if (canCoordinate)
                {
                    await ExecuteCoordinatedWarmingAsync(cancellationToken);
                }
                else
                {
                    LogGracefulDegradationReason();
                    await WarmConnectionPoolAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Connection pool warming failed for {ServiceType}. " +
                    "Connections will be established on demand.",
                    _serviceType);
            }
        }

        /// <summary>
        /// Cancels warming and awaits protected work before service disposal.
        /// </summary>
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _shutdown.Cancel();
            if (_warmingTask is not null)
            {
                try { await _warmingTask.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
            _logger.LogDebug("Coordinated connection pool warmer stopped for {ServiceType}", _serviceType);
        }

        public void Dispose() => _shutdown.Dispose();

        private async Task ExecuteCoordinatedWarmingAsync(CancellationToken cancellationToken)
        {
            var lockKey = GetLockKey();

            // Try to acquire the warming lock (non-blocking)
            var lockHandle = await _lockService!.TryAcquireAsync(
                lockKey,
                TimeSpan.Zero,
                cancellationToken);

            if (lockHandle != null)
            {
                await ExecuteAsLeaderAsync(lockHandle, cancellationToken);
            }
            else
            {
                await ExecuteAsFollowerAsync(cancellationToken);
            }
        }

        private async Task ExecuteAsLeaderAsync(
            IDistributedLockOwnership lockHandle,
            CancellationToken cancellationToken)
        {
            try
            {
                using var work = lockHandle.CreateOperationCancellation(cancellationToken);
                cancellationToken = work.Token;
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogInformation(
                    "Instance {InstanceId} acquired warming lock for {ServiceType}. Warming pool as leader.",
                    _instanceId, _serviceType);

                await WarmConnectionPoolAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                // Publish warming complete signal
                await PublishWarmingSignalAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                _logger.LogInformation(
                    "Leader {InstanceId} completed warming for {ServiceType}. Signal published.",
                    _instanceId, _serviceType);
            }

            finally
            {
                try
                {
                    await lockHandle.DisposeAsync();
                    _logger.LogDebug("Warming lock released for {ServiceType}", _serviceType);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to release warming lock for {ServiceType}", _serviceType);
                }
            }
        }

        private async Task ExecuteAsFollowerAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation(
                "Instance {InstanceId} waiting for warming signal for {ServiceType} (timeout: {Timeout})",
                _instanceId, _serviceType, _options.SignalTimeout);

            var signalReceived = false;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var channel = GetSignalChannel();
            var subscriber = _redis!.GetSubscriber();
            ChannelMessageQueue? messageQueue = null;
            using var listenerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task listener = Task.CompletedTask;

            try
            {
                // Subscribe to warming signal channel
                messageQueue = await subscriber.SubscribeAsync(RedisChannel.Literal(channel));

                // Process messages asynchronously
                listener = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var message in messageQueue.WithCancellation(listenerCancellation.Token))
                        {
                            try
                            {
                                var messageString = message.Message.ToString();
                                var signal = JsonSerializer.Deserialize(
                                    messageString,
                                    CoreInternalJsonContext.Default.ConnectionPoolWarmingSignal);
                                if (signal != null && signal.ServiceType == _serviceType)
                                {
                                    if (_options.VerboseLogging)
                                    {
                                        _logger.LogDebug(
                                            "Received warming signal from {LeaderInstance} for {ServiceType}",
                                            signal.InstanceId, _serviceType);
                                    }

                                    signalReceived = true;
                                    tcs.TrySetResult(true);
                                    break;
                                }
                            }
                            catch (JsonException ex)
                            {
                                _logger.LogWarning(ex, "Error parsing warming signal");
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when unsubscribing
                    }
                }, listenerCancellation.Token);

                // Wait for signal or timeout
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_options.SignalTimeout);

                try
                {
                    await tcs.Task.WaitAsync(timeoutCts.Token);
                    _logger.LogInformation(
                        "Received warming signal for {ServiceType}. Proceeding with local pool warming.",
                        _serviceType);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation(
                        "Warming signal timeout reached for {ServiceType}. Proceeding with local pool warming.",
                        _serviceType);
                }
            }
            finally
            {
                listenerCancellation.Cancel();
                // Unsubscribe from channel
                try
                {
                    if (messageQueue != null) { await messageQueue.UnsubscribeAsync(); }
                }
                finally
                {
                    try { await listener; }
                    catch (OperationCanceledException) when (listenerCancellation.IsCancellationRequested) { }
                }
            }

            // Add stagger delay if signal was received (to prevent secondary thundering herd)
            if (signalReceived)
            {
                var jitteredDelay = _options.StaggerDelay +
                    TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));

                if (_options.VerboseLogging)
                {
                    _logger.LogDebug(
                        "Staggering warming for {ServiceType} by {Delay}ms",
                        _serviceType, jitteredDelay.TotalMilliseconds);
                }

                await Task.Delay(jitteredDelay, cancellationToken);
            }

            // Warm our own pool
            await WarmConnectionPoolAsync(cancellationToken);
        }

        private async Task PublishWarmingSignalAsync(CancellationToken cancellationToken)
        {
            var channel = GetSignalChannel();
            var signal = new ConnectionPoolWarmingSignal(
                _instanceId,
                _serviceType,
                DateTime.UtcNow,
                _connectionsToWarm);

            var subscriber = _redis!.GetSubscriber();
            var message = JsonSerializer.Serialize(
                signal,
                CoreInternalJsonContext.Default.ConnectionPoolWarmingSignal);

            var subscribers = await subscriber.PublishAsync(
                RedisChannel.Literal(channel),
                message);

            if (_options.VerboseLogging || subscribers > 0)
            {
                _logger.LogInformation(
                    "Published warming signal for {ServiceType} to {SubscriberCount} subscribers",
                    _serviceType, subscribers);
            }
        }

        private async Task WarmConnectionPoolAsync(CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Create parallel connections to warm the pool
                var tasks = Enumerable.Range(0, _connectionsToWarm).Select(async i =>
                {
                    using var scope = _serviceProvider.CreateScope();
                    var dbContextFactory = scope.ServiceProvider
                        .GetService<IDbContextFactory<ConduitDbContext>>();

                    if (dbContextFactory != null)
                    {
                        using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

                        // Execute a simple query to ensure the connection is fully established
                        await dbContext.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);

                        if (_options.VerboseLogging)
                        {
                            _logger.LogDebug(
                                "Warmed connection {ConnectionNumber}/{TotalConnections} for {ServiceType}",
                                i + 1, _connectionsToWarm, _serviceType);
                        }
                    }
                });

                await Task.WhenAll(tasks);
                cancellationToken.ThrowIfCancellationRequested();

                stopwatch.Stop();

                _logger.LogInformation(
                    "Connection pool warmed for {ServiceType} with {ConnectionCount} connections in {ElapsedMilliseconds}ms",
                    _serviceType, _connectionsToWarm, stopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                stopwatch.Stop();

                _logger.LogWarning(ex,
                    "Failed to warm connection pool for {ServiceType} after {ElapsedMilliseconds}ms. " +
                    "Connections will be established on demand.",
                    _serviceType, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        private void LogGracefulDegradationReason()
        {
            if (!_options.EnableCoordinatedWarming)
            {
                _logger.LogInformation(
                    "Coordinated warming disabled. Warming pool immediately for {ServiceType}.",
                    _serviceType);
            }
            else if (_redis == null)
            {
                _logger.LogInformation(
                    "Redis unavailable. Warming pool immediately for {ServiceType}.",
                    _serviceType);
            }
            else if (_lockService == null)
            {
                _logger.LogInformation(
                    "Lock service unavailable. Warming pool immediately for {ServiceType}.",
                    _serviceType);
            }
        }

        private string GetLockKey()
        {
            return $"{_options.WarmingLockKey}:{_serviceType}";
        }

        private string GetSignalChannel()
        {
            return $"{_options.WarmingSignalChannel}:{_serviceType}";
        }

        private static int DetermineConnectionsToWarm(string? serviceType)
        {
            return serviceType?.ToUpperInvariant() switch
            {
                "COREAPI" => 10,    // Warm 10 connections for Gateway API (high traffic)
                "ADMINAPI" => 5,    // Warm 5 connections for Admin API (medium traffic)
                "WEBADMIN" => 0,    // WebAdmin doesn't use database directly
                _ => 5              // Default to 5 connections
            };
        }

    }
}
