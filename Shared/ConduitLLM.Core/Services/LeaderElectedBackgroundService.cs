using System;
using System.Threading;
using System.Threading.Tasks;
using ConduitLLM.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Base class for background services that require leader election to ensure single-instance processing.
    /// Implements fencing tokens to prevent split-brain scenarios where a stale leader continues processing.
    /// </summary>
    public abstract class LeaderElectedBackgroundService : BackgroundService, IAsyncDisposable
    {
        private readonly ILeaderElectionService _leaderElectionService;
        private readonly ILogger _logger;
        private readonly string _serviceName;
        private readonly TimeSpan _baseLeaderCheckInterval;
        private readonly TimeSpan _maxLeaderCheckInterval;
        private readonly int _maxBackoffExponent;

        private bool _isLeader;
        private bool _wasLeader;
        private long _currentFencingToken;
        private int _consecutiveFailures;
        private volatile bool _disposed;
        private readonly object _lifecycleSync = new();
        private Task? _stopTask;

        /// <summary>
        /// Initializes a new instance of the LeaderElectedBackgroundService class
        /// </summary>
        /// <param name="leaderElectionService">Leader election service</param>
        /// <param name="logger">Logger instance</param>
        /// <param name="serviceName">Unique name for this service (used for leader election)</param>
        protected LeaderElectedBackgroundService(
            ILeaderElectionService leaderElectionService,
            ILogger logger,
            string? serviceName = null)
        {
            _leaderElectionService = leaderElectionService ?? throw new ArgumentNullException(nameof(leaderElectionService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serviceName = serviceName ?? GetType().Name;
            _baseLeaderCheckInterval = TimeSpan.FromSeconds(10);
            _maxLeaderCheckInterval = TimeSpan.FromMinutes(2);
            _maxBackoffExponent = 4; // Max backoff: 10 * 2^4 = 160 seconds
            _isLeader = false;
            _wasLeader = false;
            _currentFencingToken = 0;
            _consecutiveFailures = 0;
        }

        /// <summary>
        /// Gets whether this instance is currently the leader
        /// </summary>
        protected bool IsLeader => _isLeader;

        /// <summary>
        /// Gets the current fencing token. Must be validated before performing work.
        /// </summary>
        protected long CurrentFencingToken => _currentFencingToken;

        /// <summary>
        /// Gets the unique service name used for leader election
        /// </summary>
        public string ServiceName => _serviceName;

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            lock (_lifecycleSync)
                return _disposed || _stopTask is not null || ExecuteTask is not null
                    ? Task.CompletedTask : base.StartAsync(cancellationToken);
        }

        /// <summary>
        /// Validates that the current fencing token is still valid before performing critical work.
        /// Call this before any operation that must not be performed by stale leaders.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>True if this instance is still the valid leader</returns>
        protected async Task<bool> ValidateFencingTokenAsync(CancellationToken cancellationToken)
        {
            if (!_isLeader || _currentFencingToken == 0)
            {
                return false;
            }

            return await _leaderElectionService.ValidateFencingTokenAsync(_serviceName, _currentFencingToken, cancellationToken);
        }

        /// <summary>
        /// Main execution loop with leader election
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting leader-elected service {ServiceName}", _serviceName);

            // Initial delay to allow system to stabilize
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Check if we are the leader
                    _isLeader = await _leaderElectionService.IsLeaderAsync(_serviceName, stoppingToken);
                    stoppingToken.ThrowIfCancellationRequested();

                    if (!_isLeader)
                    {
                        // Try to acquire leadership
                        var result = await _leaderElectionService.TryAcquireLeadershipAsync(_serviceName, stoppingToken);
                        _isLeader = result.Acquired;

                        if (_isLeader)
                        {
                            _currentFencingToken = result.FencingToken;
                            _consecutiveFailures = 0; // Reset backoff on success
                        }
                    }
                    stoppingToken.ThrowIfCancellationRequested();

                    if (_isLeader && !_wasLeader)
                    {
                        // Just became leader
                        _logger.LogInformation(
                            "Service {ServiceName} became leader on instance {InstanceId} with fencing token {FencingToken}",
                            _serviceName, Environment.MachineName, _currentFencingToken);
                        await OnBecameLeaderAsync(stoppingToken);
                        _wasLeader = true;
                    }
                    else if (!_isLeader && _wasLeader)
                    {
                        // Lost leadership
                        _logger.LogInformation(
                            "Service {ServiceName} lost leadership on instance {InstanceId}",
                            _serviceName, Environment.MachineName);
                        _currentFencingToken = 0;
                        await OnLostLeadershipAsync(stoppingToken);
                        _wasLeader = false;
                    }

                    if (_isLeader)
                    {
                        // Execute the service logic
                        await ExecuteLeaderWorkAsync(stoppingToken);
                        _consecutiveFailures = 0; // Reset backoff on successful work
                    }
                    else
                    {
                        // Not leader, wait with exponential backoff before checking again
                        var delay = CalculateBackoffDelay();
                        _logger.LogTrace(
                            "Service {ServiceName} is not leader, waiting {Interval} before retry (attempt {Attempt})",
                            _serviceName, delay, _consecutiveFailures + 1);
                        await Task.Delay(delay, stoppingToken);
                        _consecutiveFailures = Math.Min(_consecutiveFailures + 1, _maxBackoffExponent);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Expected when cancellation is requested
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in leader-elected service {ServiceName}", _serviceName);
                    _consecutiveFailures = Math.Min(_consecutiveFailures + 1, _maxBackoffExponent);

                    // Wait with exponential backoff before retrying
                    var delay = CalculateBackoffDelay();
                    try
                    {
                        await Task.Delay(delay, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }

        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            lock (_lifecycleSync)
                return _stopTask ??= StopAndReleaseLeadershipAsync(cancellationToken);
        }

        private async Task StopAndReleaseLeadershipAsync(CancellationToken cancellationToken)
        {
            try
            {
                // A canceled host deadline must not detach work that still uses dependencies.
                if (!_disposed)
                    await base.StopAsync(CancellationToken.None);
                if (ExecuteTask is { } execution)
                    await execution;
            }
            catch (OperationCanceledException)
            {
                // The background service's own cancellation ends its producer loop.
            }
            finally
            {
                await CleanupLeadershipAsync(cancellationToken);
            }
        }

        /// <summary>
        /// Calculates the backoff delay with exponential increase
        /// </summary>
        private TimeSpan CalculateBackoffDelay()
        {
            var multiplier = Math.Pow(2, _consecutiveFailures);
            var delay = TimeSpan.FromMilliseconds(_baseLeaderCheckInterval.TotalMilliseconds * multiplier);

            if (delay > _maxLeaderCheckInterval)
            {
                delay = _maxLeaderCheckInterval;
            }

            // Add jitter (±10%) to prevent thundering herd
            var jitterFactor = 0.9 + (Random.Shared.NextDouble() * 0.2);
            return TimeSpan.FromMilliseconds(delay.TotalMilliseconds * jitterFactor);
        }

        /// <summary>
        /// Cleans up leadership state during shutdown
        /// </summary>
        private async Task CleanupLeadershipAsync(CancellationToken cancellationToken)
        {
            if (!_disposed && (_isLeader || _wasLeader || _currentFencingToken != 0))
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _leaderElectionService.ReleaseLeadershipAsync(_serviceName, cancellationToken);
                    _logger.LogInformation("Service {ServiceName} released leadership on shutdown", _serviceName);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error releasing leadership for service {ServiceName} on shutdown", _serviceName);
                }
                finally
                {
                    _isLeader = false;
                    _wasLeader = false;
                    _currentFencingToken = 0;
                }
            }
        }

        /// <summary>
        /// Called when this instance becomes the leader.
        /// Override to perform initialization tasks when becoming leader.
        /// </summary>
        protected virtual Task OnBecameLeaderAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Called when this instance loses leadership.
        /// Override to perform cleanup tasks when losing leadership.
        /// </summary>
        protected virtual Task OnLostLeadershipAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// Executes the main work of the service when this instance is the leader.
        /// Must be implemented by derived classes.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>Task representing the asynchronous operation</returns>
        protected abstract Task ExecuteLeaderWorkAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Asynchronously disposes resources used by the service
        /// </summary>
        public virtual ValueTask DisposeAsync()
        {
            DisposeLocalResources();
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// Disposes resources used by the service.
        /// Prefer using DisposeAsync when possible.
        /// </summary>
        public override void Dispose()
            => DisposeLocalResources();

        private void DisposeLocalResources()
        {
            lock (_lifecycleSync)
            {
                if (_disposed) return;
                _disposed = true;
                base.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }
}
