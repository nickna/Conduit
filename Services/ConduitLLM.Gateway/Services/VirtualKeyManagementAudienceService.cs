using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Authentication;
using ConduitLLM.Gateway.Hubs;

using Microsoft.AspNetCore.SignalR;

namespace ConduitLLM.Gateway.Services;

public interface IVirtualKeyManagementAudienceService
{
    Task RegisterAsync(string connectionId, int ownerId, Action abort, CancellationToken cancellationToken);
    Task<VirtualKeyManagementSubscription> SubscribeAsync(string connectionId, int targetKeyId,
        Func<VirtualKey, CancellationToken, Task> subscribed, CancellationToken cancellationToken);
    Task UnsubscribeAsync(string connectionId, int targetKeyId, CancellationToken cancellationToken);
    Task RemoveAsync(string connectionId);
}

public sealed record VirtualKeyManagementSubscription(bool Authorized, bool TargetExists);

/// <summary>
/// Owns this node's management audiences. Every Gateway node audits its own
/// connections, including recipients of broadcasts published through a backplane.
/// Revocation normally completes within the five-second cadence plus five-second
/// audit budget; unavailable roles revoke access and incomplete removal aborts it.
/// </summary>
public sealed class VirtualKeyManagementAudienceService : BackgroundService, IVirtualKeyManagementAudienceService
{
    internal static readonly TimeSpan AuditInterval = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan OperationBudget = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly IGroupManager _groups;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<VirtualKeyManagementAudienceService> _logger;
    private readonly TimeProvider _clock;

    public VirtualKeyManagementAudienceService(IHubContext<VirtualKeyManagementHub> hub,
        IServiceScopeFactory scopes, ILogger<VirtualKeyManagementAudienceService> logger, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(logger);
        _groups = hub.Groups;
        _scopes = scopes;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    internal int ConnectionCount => _connections.Count;

    public async Task RegisterAsync(string connectionId, int ownerId, Action abort, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerId);
        ArgumentNullException.ThrowIfNull(abort);
        var connection = new Connection(connectionId, ownerId, abort);
        if (!_connections.TryAdd(connectionId, connection))
            throw new InvalidOperationException("The management connection is already registered.");
        await WithConnectionAsync(connection, cancellationToken, async token =>
        {
            await using var scope = _scopes.CreateAsyncScope();
            var keys = scope.ServiceProvider.GetRequiredService<IVirtualKeyRuntimeService>();
            if (await IsAdministratorAsync(keys, ownerId, token)) await GrantAdministratorAsync(connection, token);
            else await RevokeAdministratorAsync(connection, token);
            await AddAsync(connection, VirtualKeyManagementGroups.Status(ownerId), token);
        });
    }

    public async Task<VirtualKeyManagementSubscription> SubscribeAsync(string connectionId, int targetKeyId,
        Func<VirtualKey, CancellationToken, Task> subscribed, CancellationToken cancellationToken)
    {
        var connection = RequireConnection(connectionId);
        var outcome = new VirtualKeyManagementSubscription(false, false);
        ExceptionDispatchInfo? targetLookupFailure = null;
        await WithConnectionAsync(connection, cancellationToken, async token =>
        {
            // Authorization and all audience changes share the revocation gate.
            // No previously read true role can rejoin after a completed denial.
            await using var scope = _scopes.CreateAsyncScope();
            var keys = scope.ServiceProvider.GetRequiredService<IVirtualKeyRuntimeService>();
            if (!await IsAdministratorAsync(keys, connection.OwnerId, token))
            {
                await RevokeAdministratorAsync(connection, token);
                return;
            }
            VirtualKey? target;
            try { target = await keys.GetVirtualKeyInfoForValidationAsync(targetKeyId, token); }
            catch (Exception failure) when (failure is not OperationCanceledException && !token.IsCancellationRequested)
            {
                // A failed target read has not changed any audience. Preserve
                // this connection and the hub's existing error/metrics path.
                targetLookupFailure = ExceptionDispatchInfo.Capture(failure);
                return;
            }
            outcome = new VirtualKeyManagementSubscription(true, target is not null);
            if (target is null) return;
            await GrantAdministratorAsync(connection, token);
            if (targetKeyId != connection.OwnerId) connection.ForeignKeys.Add(targetKeyId);
            await AddAsync(connection, VirtualKeyManagementGroups.Status(targetKeyId), token);
            // The short-lived callback is never retained. Its privileged initial
            // status and acknowledgement also finish before revocation can finish.
            await subscribed(target, token);
        });
        targetLookupFailure?.Throw();
        return outcome;
    }

    public Task UnsubscribeAsync(string connectionId, int targetKeyId, CancellationToken cancellationToken)
    {
        var connection = RequireConnection(connectionId);
        return WithConnectionAsync(connection, cancellationToken, async token =>
        {
            if (targetKeyId != connection.OwnerId && connection.ForeignKeys.Contains(targetKeyId))
            {
                await RemoveGroupAsync(connection, VirtualKeyManagementGroups.Status(targetKeyId), token);
                connection.ForeignKeys.Remove(targetKeyId);
            }
        });
    }

    public async Task RemoveAsync(string connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var connection)) return;
        using var budget = new CancellationTokenSource(OperationBudget, _clock);
        using var timeout = budget.Token.Register(() => Abort(connection));
        try
        {
            await connection.Gate.WaitAsync(budget.Token);
            try { Close(connection); }
            finally { connection.Gate.Release(); }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested) { Abort(connection); }
        // SignalR removes all transport memberships when the connection closes.
    }

    public async Task AuditAsync(CancellationToken cancellationToken = default)
    {
        var connections = _connections.Values.Where(connection => Volatile.Read(ref connection.Privileged) != 0).ToArray();
        if (connections.Length == 0) return;
        using var budget = new CancellationTokenSource(OperationBudget, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        var roles = new ConcurrentDictionary<int, bool>();
        void AbortUnresolved()
        {
            foreach (var connection in connections)
                if (!roles.GetValueOrDefault(connection.OwnerId) && Volatile.Read(ref connection.Privileged) != 0)
                    Abort(connection);
        }
        using var timeout = linked.Token.Register(AbortUnresolved);
        async Task RevokeAsync(Connection connection, CancellationToken token)
        {
            try { await WithConnectionAsync(connection, token, workToken => RevokeAdministratorAsync(connection, workToken)); }
            catch (Exception failure)
            {
                _logger.LogWarning(failure, "Revoked an unavailable virtual-key management connection");
            }
        }
        try
        {
            // The single deadline includes queued lookups and revocations. It
            // does not grow by five seconds for every distinct administrator key.
            var lookups = Parallel.ForEachAsync(connections.GroupBy(connection => connection.OwnerId),
                new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = linked.Token }, async (owner, token) =>
                {
                    try
                    {
                        await using var scope = _scopes.CreateAsyncScope();
                        var administrator = await IsAdministratorAsync(
                            scope.ServiceProvider.GetRequiredService<IVirtualKeyRuntimeService>(), owner.Key, token);
                        roles[owner.Key] = administrator;
                        // Revoke a known demotion immediately; a different key's
                        // stalled lookup must not delay it or disconnect healthy admins.
                        if (!administrator)
                            await Task.WhenAll(owner.Select(connection => RevokeAsync(connection, token)));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception failure)
                    {
                        roles[owner.Key] = false;
                        _logger.LogWarning(failure, "Could not revalidate management audience owner");
                        await Task.WhenAll(owner.Select(connection => RevokeAsync(connection, token)));
                    }
                });
            try { await lookups.WaitAsync(linked.Token); }
            catch
            {
                // Each lookup owns its async scope until it really drains. A
                // noncooperative provider cannot prevent the next audit tick.
                _ = ObserveLateWorkAsync(lookups);
                throw;
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception failure)
        {
            AbortUnresolved();
            _logger.LogWarning(failure, "Could not complete management audience role audit");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(AuditInterval, _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await AuditAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var connection in _connections.Values) Abort(connection);
        }
    }

    private async Task<bool> IsAdministratorAsync(IVirtualKeyRuntimeService keys, int ownerId, CancellationToken token)
    {
        try
        {
            var key = await keys.GetVirtualKeyInfoForValidationAsync(ownerId, token);
            token.ThrowIfCancellationRequested();
            return SignalRAuthenticationService.IsTrustedAdministrator(key);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception failure)
        {
            _logger.LogWarning(failure, "Could not revalidate virtual-key management administrator role");
            return false;
        }
    }

    private async Task WithConnectionAsync(Connection connection, CancellationToken cancellationToken,
        Func<CancellationToken, Task> operation)
    {
        using var budget = new CancellationTokenSource(OperationBudget, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token, connection.Closed.Token);
        using var timeout = linked.Token.Register(() => Abort(connection));
        var entered = false;
        Task? pending = null;
        try
        {
            await connection.Gate.WaitAsync(linked.Token);
            entered = true;
            linked.Token.ThrowIfCancellationRequested();
            pending = operation(linked.Token);
            await pending.WaitAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
        }
        catch
        {
            Abort(connection);
            if (entered && pending is { IsCompleted: false })
            {
                entered = false;
                _ = DrainAndReleaseAsync(pending, connection);
            }
            else if (pending is { IsFaulted: true }) _ = pending.Exception;
            throw;
        }
        finally { if (entered) connection.Gate.Release(); }
    }

    private async Task GrantAdministratorAsync(Connection connection, CancellationToken token)
    {
        Volatile.Write(ref connection.Privileged, 1);
        await RemoveGroupAsync(connection, VirtualKeyManagementGroups.NonAdminOwner(connection.OwnerId), token);
        await AddAsync(connection, "admin", token);
    }

    private async Task RevokeAdministratorAsync(Connection connection, CancellationToken token)
    {
        await RemoveGroupAsync(connection, "admin", token);
        foreach (var target in connection.ForeignKeys)
            await RemoveGroupAsync(connection, VirtualKeyManagementGroups.Status(target), token);
        connection.ForeignKeys.Clear();
        await AddAsync(connection, VirtualKeyManagementGroups.NonAdminOwner(connection.OwnerId), token);
        Volatile.Write(ref connection.Privileged, 0);
    }

    private async Task AddAsync(Connection connection, string group, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await _groups.AddToGroupAsync(connection.Id, group, token);
        token.ThrowIfCancellationRequested();
    }

    private async Task RemoveGroupAsync(Connection connection, string group, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await _groups.RemoveFromGroupAsync(connection.Id, group, token);
        token.ThrowIfCancellationRequested();
    }

    private Connection RequireConnection(string id) => _connections.TryGetValue(id, out var connection)
        ? connection : throw new HubException("The management connection is unavailable.");

    private void Abort(Connection connection)
    {
        if (!Close(connection)) return;
        try { connection.Abort(); }
        catch (Exception failure) { _logger.LogWarning(failure, "Could not abort a revoked management connection"); }
    }

    private bool Close(Connection connection)
    {
        if (Interlocked.Exchange(ref connection.IsClosed, 1) != 0) return false;
        _connections.TryRemove(new KeyValuePair<string, Connection>(connection.Id, connection));
        try { connection.Closed.Cancel(); }
        catch (Exception failure) { _logger.LogWarning(failure, "A revoked management operation cancellation callback failed"); }
        return true;
    }

    private async Task DrainAndReleaseAsync(Task pending, Connection connection)
    {
        try { await ObserveLateWorkAsync(pending); }
        finally { connection.Gate.Release(); }
    }

    private async Task ObserveLateWorkAsync(Task pending)
    {
        try { await pending; }
        catch (OperationCanceledException) { }
        catch (Exception failure) { _logger.LogWarning(failure, "Late management audience work failed after revocation"); }
    }

    private sealed class Connection(string id, int ownerId, Action abort)
    {
        internal string Id { get; } = id;
        internal int OwnerId { get; } = ownerId;
        internal Action Abort { get; } = abort;
        internal SemaphoreSlim Gate { get; } = new(1, 1);
        internal CancellationTokenSource Closed { get; } = new();
        internal HashSet<int> ForeignKeys { get; } = [];
        internal int Privileged;
        internal int IsClosed;
    }
}
