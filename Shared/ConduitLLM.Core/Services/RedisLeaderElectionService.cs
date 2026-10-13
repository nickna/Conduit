using System.Collections.Concurrent;
using ConduitLLM.Core.Interfaces;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ConduitLLM.Core.Services;

/// <summary>Redis leader leases and their owned renewal generations.</summary>
public class RedisLeaderElectionService : ILeaderElectionService, IAsyncDisposable, IDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisLeaderElectionService> _logger;
    private readonly string _instanceId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly TimeSpan _leadershipTtl = TimeSpan.FromSeconds(60);
    private readonly TimeSpan _renewalInterval;
    private readonly ConcurrentDictionary<string, MaintenanceState> _maintenanceStates = new();
    private readonly ConcurrentDictionary<string, LeadershipState> _leadershipStates = new();
    private readonly object _lifecycleSync = new();
    private readonly Dictionary<MaintenanceState, string> _generations = new();
    private readonly HashSet<string> _leases = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private Task? _shutdownTask;
    private volatile bool _stopping;
    private volatile bool _disposed;

    private sealed class MaintenanceState(CancellationToken cancellationToken, CancellationToken shutdown) : IDisposable
    {
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown);
        public Task Task { get; set; } = Task.CompletedTask;
        public void Dispose() => Cancellation.Dispose();
    }

    private sealed class LeadershipState
    {
        public bool IsLeader { get; set; }
        public long FencingToken { get; init; }
        public DateTime LastRenewal { get; set; }
    }

    public RedisLeaderElectionService(IConnectionMultiplexer redis, ILogger<RedisLeaderElectionService> logger)
        : this(redis, logger, TimeSpan.FromSeconds(30)) { }

    internal RedisLeaderElectionService(IConnectionMultiplexer redis, ILogger<RedisLeaderElectionService> logger, TimeSpan renewalInterval)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _renewalInterval = renewalInterval;
        _shutdownToken = _shutdown.Token;
        _logger.LogInformation("Leader election service initialized with instance ID: {InstanceId}", _instanceId);
    }

    private void CheckRunning(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_stopping || _disposed) throw new OperationCanceledException(_shutdownToken);
    }

    public async Task<LeadershipAcquisitionResult> TryAcquireLeadershipAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        await _transition.WaitAsync(cancellationToken);
        try
        {
            CheckRunning(cancellationToken);
            await JoinGenerationsAsync(serviceName, retireActive: false);
            CheckRunning(cancellationToken);
            var database = _redis.GetDatabase();
            var result = await database.ScriptEvaluateAsync(@"
                if redis.call('SET', KEYS[1], ARGV[1], 'NX', 'EX', ARGV[2]) then
                    return redis.call('INCR', KEYS[2])
                else return -1 end", [GetLockKey(serviceName), GetFencingKey(serviceName)],
                [_instanceId, (int)_leadershipTtl.TotalSeconds]);
            var fencingToken = (long)result;
            if (fencingToken > 0)
            {
                // Remember a lease even when cancellation races the Redis reply.
                lock (_lifecycleSync) _leases.Add(serviceName);
                CheckRunning(cancellationToken);
                await JoinGenerationsAsync(serviceName, retireActive: true);
                CheckRunning(cancellationToken);
                _leadershipStates[serviceName] = new LeadershipState
                    { IsLeader = true, FencingToken = fencingToken, LastRenewal = DateTime.UtcNow };
                StartMaintenanceCore(serviceName, cancellationToken);
                return LeadershipAcquisitionResult.Success(fencingToken);
            }
            CheckRunning(cancellationToken);
            var currentLeader = await database.StringGetAsync(GetLockKey(serviceName));
            CheckRunning(cancellationToken);
            _logger.LogDebug("Could not acquire leadership for {ServiceName}; current leader: {CurrentLeader}", serviceName, currentLeader);
            return LeadershipAcquisitionResult.Failure();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error acquiring leadership for {ServiceName}", serviceName);
            return LeadershipAcquisitionResult.Failure();
        }
        finally { _transition.Release(); }
    }

    public async Task ReleaseLeadershipAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        await _transition.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await JoinGenerationsAsync(serviceName, retireActive: true);
            cancellationToken.ThrowIfCancellationRequested();
            await ReleaseLeaseAsync(serviceName, cancellationToken);
        }
        finally { _transition.Release(); }
    }

    private async Task ReleaseLeaseAsync(string serviceName, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var database = _redis.GetDatabase();
            await database.ScriptEvaluateAsync(@"
                if redis.call('GET', KEYS[1]) == ARGV[1] then
                    return redis.call('DEL', KEYS[1])
                else return 0 end", [GetLockKey(serviceName)], [_instanceId]);
            lock (_lifecycleSync) _leases.Remove(serviceName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error releasing leadership for {ServiceName}", serviceName);
        }
        finally { _leadershipStates.TryRemove(serviceName, out _); }
    }

    public async Task<bool> IsLeaderAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        try
        {
            CheckRunning(cancellationToken);
            var database = _redis.GetDatabase();
            CheckRunning(cancellationToken);
            var leader = await database.StringGetAsync(GetLockKey(serviceName));
            CheckRunning(cancellationToken);
            var ownsLease = leader == _instanceId;
            if (_leadershipStates.TryGetValue(serviceName, out var state)) state.IsLeader = ownsLease;
            return ownsLease;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            if (_leadershipStates.TryGetValue(serviceName, out var state)) state.IsLeader = false;
            _logger.LogError(exception, "Error checking leadership for {ServiceName}", serviceName);
            return false;
        }
    }

    public async Task<string?> GetCurrentLeaderAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        try
        {
            CheckRunning(cancellationToken);
            var value = await _redis.GetDatabase().StringGetAsync(GetLockKey(serviceName));
            CheckRunning(cancellationToken);
            return value.HasValue ? value.ToString() : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error reading leadership for {ServiceName}", serviceName);
            return null;
        }
    }

    public Task<long?> GetCurrentFencingTokenAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        CheckRunning(cancellationToken);
        return Task.FromResult(_leadershipStates.TryGetValue(serviceName, out var state) && state.IsLeader
            ? (long?)state.FencingToken : null);
    }

    public async Task<bool> ValidateFencingTokenAsync(string serviceName, long fencingToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        CheckRunning(cancellationToken);
        return _leadershipStates.TryGetValue(serviceName, out var state) && state.IsLeader &&
            state.FencingToken == fencingToken && await IsLeaderAsync(serviceName, cancellationToken);
    }

    public async Task StartLeadershipMaintenanceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        await _transition.WaitAsync(cancellationToken);
        try
        {
            CheckRunning(cancellationToken);
            await JoinGenerationsAsync(serviceName, retireActive: true);
            CheckRunning(cancellationToken);
            StartMaintenanceCore(serviceName, cancellationToken);
        }
        finally { _transition.Release(); }
    }

    private void StartMaintenanceCore(string serviceName, CancellationToken cancellationToken)
    {
        var state = new MaintenanceState(cancellationToken, _shutdownToken);
        lock (_lifecycleSync)
        {
            _maintenanceStates[serviceName] = state;
            _generations.Add(state, serviceName);
            state.Task = RunMaintenanceLoopAsync(serviceName, state);
        }
    }

    public async Task StopLeadershipMaintenanceAsync(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        await _transition.WaitAsync();
        try { await JoinGenerationsAsync(serviceName, retireActive: true); }
        finally { _transition.Release(); }
    }

    private async Task JoinGenerationsAsync(string? serviceName, bool retireActive)
    {
        MaintenanceState[] generations;
        lock (_lifecycleSync)
        {
            generations = _generations.Where(pair => serviceName is null || pair.Value == serviceName)
                .Where(pair => retireActive || !_maintenanceStates.TryGetValue(pair.Value, out var active) || active != pair.Key)
                .Select(pair => pair.Key).ToArray();
            foreach (var state in generations)
            {
                if (_generations.TryGetValue(state, out var name) &&
                    _maintenanceStates.TryGetValue(name, out var active) && active == state)
                    _maintenanceStates.TryRemove(name, out _);
                state.Cancellation.Cancel();
            }
        }
        await Task.WhenAll(generations.Select(state => state.Task));
        lock (_lifecycleSync)
            foreach (var state in generations)
            {
                _generations.Remove(state);
                state.Dispose();
            }
    }

    private async Task RunMaintenanceLoopAsync(string serviceName, MaintenanceState state)
    {
        var cancellationToken = state.Cancellation.Token;
        try
        {
            using var timer = new PeriodicTimer(_renewalInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RenewLeadershipAsync(serviceName, state, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { _logger.LogError(exception, "Error maintaining leadership for {ServiceName}", serviceName); }
    }

    private async Task RenewLeadershipAsync(string serviceName, MaintenanceState generation, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var database = _redis.GetDatabase();
            cancellationToken.ThrowIfCancellationRequested();
            var result = await database.ScriptEvaluateAsync(@"
                if redis.call('GET', KEYS[1]) == ARGV[1] then
                    return redis.call('EXPIRE', KEYS[1], ARGV[2])
                else return 0 end", [GetLockKey(serviceName)], [_instanceId, (int)_leadershipTtl.TotalSeconds]);
            cancellationToken.ThrowIfCancellationRequested();
            if ((long)result == 1)
            {
                if (_leadershipStates.TryGetValue(serviceName, out var state)) state.LastRenewal = DateTime.UtcNow;
            }
            else RetireLostLeadership(serviceName, generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (RedisConnectionException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogError(exception, "Redis error renewing leadership for {ServiceName}", serviceName);
            RetireLostLeadership(serviceName, generation);
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogError(exception, "Error renewing leadership for {ServiceName}", serviceName);
            if (!await IsLeaderAsync(serviceName, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RetireLostLeadership(serviceName, generation);
            }
        }
    }

    private void RetireLostLeadership(string serviceName, MaintenanceState generation)
    {
        lock (_lifecycleSync)
        {
            if (_maintenanceStates.TryGetValue(serviceName, out var active) && active == generation)
            {
                _maintenanceStates.TryRemove(serviceName, out _);
                if (_leadershipStates.TryGetValue(serviceName, out var leader)) leader.IsLeader = false;
            }
            generation.Cancellation.Cancel();
            // This callback runs inside its generation. A later transition joins it.
        }
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleSync)
        {
            _stopping = true;
            return _shutdownTask ??= ShutdownCoreAsync(cancellationToken);
        }
    }

    private async Task ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync();
        await _transition.WaitAsync();
        try
        {
            await JoinGenerationsAsync(null, retireActive: true);
            string[] leases;
            lock (_lifecycleSync) leases = _leases.ToArray();
            foreach (var name in leases)
            {
                if (cancellationToken.IsCancellationRequested || _disposed) break;
                try { await ReleaseLeaseAsync(name, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }
        finally { _transition.Release(); }
    }

    private static string GetLockKey(string serviceName) => $"leader:{serviceName}";
    private static string GetFencingKey(string serviceName) => $"leader:{serviceName}:fence";

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    public void Dispose()
    {
        lock (_lifecycleSync)
        {
            if (_disposed) return;
            _disposed = true;
            _stopping = true;
            _shutdown.Cancel();
            foreach (var state in _generations.Keys)
                state.Cancellation.Cancel();
            // No Redis calls belong in disposal. Hosted ShutdownAsync owns dependency work.
            if (_shutdownTask is { IsCompleted: true }) _shutdown.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
