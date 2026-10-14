using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace ConduitLLM.Configuration.Messaging.Wolverine;

public sealed class WolverineRuntimeStartupState
{
    // 0: immediate lifecycle; 1: waiting/starting; 2: started; 3: stopped/failed.
    private int _phase;

    public bool IsReady => Volatile.Read(ref _phase) is 0 or 2;
    internal void MarkDeferred() => Interlocked.CompareExchange(ref _phase, 1, 0);
    internal void MarkCompleted() => Interlocked.CompareExchange(ref _phase, 2, 1);
    // A concurrent startup completion cannot overwrite a terminal transition.
    internal void MarkUnavailable() => Interlocked.Exchange(ref _phase, 3);
}

/// <summary>Keeps web-host startup available while release-owned database resources are unavailable.</summary>
public sealed class SchemaGatedWolverineHostedService : BackgroundService
{
    private readonly IHostedService _runtime;
    private readonly Func<CancellationToken, Task<bool>> _preflight;
    private readonly WolverineRuntimeStartupState _state;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SchemaGatedWolverineHostedService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private bool _startupAttempted;
    private bool _stopAttempted;

    public SchemaGatedWolverineHostedService(IHostedService runtime,
        Func<CancellationToken, Task<bool>> preflight, WolverineRuntimeStartupState state,
        IHostApplicationLifetime lifetime, ILogger<SchemaGatedWolverineHostedService> logger,
        TimeSpan? pollInterval = null)
    {
        _runtime = runtime;
        _preflight = preflight;
        _state = state;
        _lifetime = lifetime;
        _logger = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        _state.MarkDeferred();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var ready = false;
                try { ready = await _preflight(stoppingToken); }
                catch (Exception exception) when (stoppingToken.IsCancellationRequested && IsCancellation(exception)) { return; }
                catch (Exception exception) when (IsRetryablePreflightFailure(exception))
                {
                    _logger.LogWarning(exception, "Wolverine startup is waiting for release-owned database resources");
                }
                if (ready) break;
                await Task.Delay(_pollInterval, stoppingToken);
            }
            stoppingToken.ThrowIfCancellationRequested();
            await _lifecycle.WaitAsync(stoppingToken);
            try
            {
                stoppingToken.ThrowIfCancellationRequested();
                _startupAttempted = true;
                // Wolverine can retain a started flag after partial initialization
                // and ignores lifecycle cancellation. Never retry or abandon this
                // task, and serialize shutdown until the attempt actually finishes.
                await _runtime.StartAsync(stoppingToken);
                if (!stoppingToken.IsCancellationRequested) _state.MarkCompleted();
            }
            finally { _lifecycle.Release(); }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _state.MarkUnavailable();
            _logger.LogCritical(exception, "Wolverine runtime startup failed; stopping the application without retrying partial initialization");
            _lifetime.StopApplication();
            throw;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _state.MarkUnavailable();
        await base.StopAsync(cancellationToken);
        // Waiting without abandoning an in-progress runtime start is intentional:
        // the pinned runtime also ignores its shutdown cancellation token.
        await _lifecycle.WaitAsync(CancellationToken.None);
        try
        {
            if (_startupAttempted && !_stopAttempted)
            {
                _stopAttempted = true;
                await _runtime.StopAsync(cancellationToken);
            }
        }
        finally { _lifecycle.Release(); }
    }

    public static bool IsRetryablePreflightFailure(Exception exception) => exception switch
    {
        PostgresException postgres => postgres.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName || postgres.IsTransient,
        NpgsqlException npgsql => npgsql.IsTransient,
        TimeoutException => true,
        InvalidOperationException { InnerException: { } inner } => IsRetryablePreflightFailure(inner),
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsRetryablePreflightFailure),
        _ => false
    };

    private static bool IsCancellation(Exception exception) => exception switch
    {
        OperationCanceledException => true,
        InvalidOperationException { InnerException: { } inner } => IsCancellation(inner),
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsCancellation),
        _ => false
    };
}
