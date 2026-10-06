using System.Diagnostics;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Metrics;
using Medallion.Threading.Postgres;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ConduitLLM.Core.Services;

/// <summary>Session ownership delegated to DistributedLock.Postgres, independent of EF lifetime.</summary>
public sealed class PostgresDistributedLockProvider : IDistributedLockProvider
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresDistributedLockProvider> _logger;

    public PostgresDistributedLockProvider(string connectionString, ILogger<PostgresDistributedLockProvider> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = "conduit-distributed-lock", Pooling = true, MinPoolSize = 0,
            MaxPoolSize = 32, Timeout = 5, CommandTimeout = 5, KeepAlive = 0,
            Enlist = false, Multiplexing = false, NoResetOnClose = false,
        }.ConnectionString;
    }

    public async Task<IDistributedLockOwnership?> TryAcquireAsync(
        string key, TimeSpan acquisitionTimeout = default, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (acquisitionTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(acquisitionTimeout));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var operation = DistributedLockMetrics.Operation(key);
        var watch = Stopwatch.StartNew();
        try
        {
            var distributedLock = new PostgresDistributedLock(
                new PostgresAdvisoryLockKey(PostgresLockIdentity.GetLockId(key)), _connectionString,
                options => options.UseTransaction(false).UseMultiplexing(false)
                    .KeepaliveCadence(TimeSpan.FromSeconds(1)));
            var handle = await distributedLock.TryAcquireAsync(acquisitionTimeout, cancellationToken).ConfigureAwait(false);
            DistributedLockMetrics.Acquisitions.WithLabels(operation, handle is null ? "busy" : "acquired").Inc();
            return handle is null ? null : new Ownership(handle, operation, _logger);
        }
        catch (OperationCanceledException)
        {
            DistributedLockMetrics.Acquisitions.WithLabels(operation, "canceled").Inc();
            throw;
        }
        catch (Exception ex)
        {
            DistributedLockMetrics.Acquisitions.WithLabels(operation, "error").Inc();
            _logger.LogError(ex, "Distributed lock acquisition failed for {Operation}", operation);
            throw;
        }
        finally { DistributedLockMetrics.Wait.WithLabels(operation).Observe(watch.Elapsed.TotalSeconds); }
    }

    private sealed class Ownership : IDistributedLockOwnership
    {
        private readonly PostgresDistributedLockHandle _handle;
        private readonly string _operation;
        private readonly ILogger _logger;
        private readonly Stopwatch _held = Stopwatch.StartNew();
        private readonly CancellationTokenRegistration _lossRegistration;
        private readonly object _gate = new();
        private Task? _dispose;

        public Ownership(PostgresDistributedLockHandle handle, string operation, ILogger logger)
        {
            _handle = handle;
            _operation = operation;
            _logger = logger;
            HandleLostToken = handle.HandleLostToken; // Activates upstream loss monitoring.
            _lossRegistration = HandleLostToken.Register(() =>
            {
                DistributedLockMetrics.Losses.WithLabels(operation).Inc();
                logger.LogWarning("Distributed lock ownership lost for {Operation}", operation);
            });
        }

        public CancellationToken HandleLostToken { get; }

        public ValueTask DisposeAsync()
        {
            lock (_gate) { return new ValueTask(_dispose ??= ReleaseAsync()); }
        }

        private async Task ReleaseAsync()
        {
            try { await _handle.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                DistributedLockMetrics.ReleaseFailures.WithLabels(_operation).Inc();
                _logger.LogError(ex, "Distributed lock release failed for {Operation}", _operation);
                // Preserve the protected operation's cancellation after an already detected loss.
                // Healthy-release failures still propagate; both failures remain observable.
                if (!HandleLostToken.IsCancellationRequested) { throw; }
            }
            finally
            {
                _lossRegistration.Dispose();
                DistributedLockMetrics.Hold.WithLabels(_operation).Observe(_held.Elapsed.TotalSeconds);
            }
        }
    }
}
