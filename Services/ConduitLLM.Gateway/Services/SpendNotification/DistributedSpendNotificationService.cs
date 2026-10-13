using Microsoft.AspNetCore.SignalR;
using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Gateway.Serialization;
using StackExchange.Redis;

namespace ConduitLLM.Gateway.Services.SpendNotification;

/// <summary>Owns periodic and request notification work until hosted shutdown joins it.</summary>
public class DistributedSpendNotificationService : ISpendNotificationService, IHostedService, IDisposable
{
    private readonly IHubContext<SpendNotificationHub> _hubContext;
    private readonly ILogger<DistributedSpendNotificationService> _logger;
    private readonly ISpendDataRepository _repository;
    private readonly IBudgetAlertManager _budgetAlertManager;
    private readonly ISpendPatternAnalyzer _patternAnalyzer;
    private readonly TimeSpan _analysisInterval;
    private readonly object _lifecycleSync = new();
    private readonly HashSet<Task> _requests = new();
    private readonly SemaphoreSlim _backgroundTransition = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private CancellationTokenSource? _analysisCancellation;
    private Task? _analysisTask;
    private Task? _shutdownTask;
    private bool _registrationAttempted;
    private bool _stopping;
    private bool _disposed;

    public string InstanceId { get; } = $"{Environment.MachineName}_{Environment.ProcessId}_{Guid.NewGuid():N}"[..Math.Min(50,
        $"{Environment.MachineName}_{Environment.ProcessId}_{Guid.NewGuid():N}".Length)];

    public DistributedSpendNotificationService(IHubContext<SpendNotificationHub> hubContext,
        ILogger<DistributedSpendNotificationService> logger, ISpendDataRepository repository,
        IBudgetAlertManager budgetAlertManager, ISpendPatternAnalyzer patternAnalyzer)
        : this(hubContext, logger, repository, budgetAlertManager, patternAnalyzer, TimeSpan.FromMinutes(5)) { }

    internal DistributedSpendNotificationService(IHubContext<SpendNotificationHub> hubContext,
        ILogger<DistributedSpendNotificationService> logger, ISpendDataRepository repository,
        IBudgetAlertManager budgetAlertManager, ISpendPatternAnalyzer patternAnalyzer, TimeSpan analysisInterval)
    {
        _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _budgetAlertManager = budgetAlertManager ?? throw new ArgumentNullException(nameof(budgetAlertManager));
        _patternAnalyzer = patternAnalyzer ?? throw new ArgumentNullException(nameof(patternAnalyzer));
        _analysisInterval = analysisInterval;
        _shutdownToken = _shutdown.Token;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _backgroundTransition.WaitAsync(cancellationToken);
        try
        {
            lock (_lifecycleSync)
            {
                if (_stopping || _disposed) return;
            }
            if (_analysisTask is { IsCompleted: false }) return;
            cancellationToken.ThrowIfCancellationRequested();
            _shutdownToken.ThrowIfCancellationRequested();
            _registrationAttempted = true;
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
            await RegisterInstanceAsync(startup.Token);
            cancellationToken.ThrowIfCancellationRequested();
            _shutdownToken.ThrowIfCancellationRequested();
            _analysisCancellation?.Dispose();
            _analysisCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
            _analysisTask = RunPatternAnalysisAsync(_analysisCancellation.Token);
            _logger.LogInformation("DistributedSpendNotificationService started with instance ID: {InstanceId}", InstanceId);
        }
        finally { _backgroundTransition.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _backgroundTransition.WaitAsync();
        try
        {
            if (_analysisCancellation is not null) await _analysisCancellation.CancelAsync();
            if (_analysisTask is not null) await _analysisTask;
            _analysisTask = null;
            _analysisCancellation?.Dispose();
            _analysisCancellation = null;
            // Ordinary leadership loss stops only this producer. Request notifications remain
            // admissible until the final hosted lifecycle phase closes and joins them.
        }
        finally { _backgroundTransition.Release(); }
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
        await StopAsync(cancellationToken);
        Task[] requests;
        lock (_lifecycleSync) requests = _requests.ToArray();
        await Task.WhenAll(requests);
        if (!_registrationAttempted || cancellationToken.IsCancellationRequested || _disposed) return;
        _registrationAttempted = false;
        try { await _repository.UnregisterInstanceAsync(InstanceId); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (RedisException exception) { _logger.LogWarning(exception, "Failed to unregister spend notification instance"); }
        catch (TimeoutException exception) { _logger.LogWarning(exception, "Timed out unregistering spend notification instance"); }
    }

    private Task TrackOperation(Func<CancellationToken, Task> operation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycleSync)
        {
            if (_stopping || _disposed) return Task.CompletedTask;
            _requests.Add(completion.Task);
        }
        _ = RunTrackedOperationAsync(operation, completion);
        return completion.Task;
    }

    private async Task RunTrackedOperationAsync(Func<CancellationToken, Task> operation, TaskCompletionSource completion)
    {
        try
        {
            _shutdownToken.ThrowIfCancellationRequested();
            await operation(_shutdownToken);
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested) { }
        catch (Exception exception) { _logger.LogError(exception, "Error processing spend notification"); }
        finally
        {
            completion.SetResult();
            lock (_lifecycleSync) _requests.Remove(completion.Task);
        }
    }

    public Task NotifySpendUpdateAsync(int virtualKeyId, decimal amount, decimal totalSpend,
        decimal? budget, string? model, string? provider) => TrackOperation(async token =>
    {
        token.ThrowIfCancellationRequested();
        await _repository.RecordSpendingPatternAsync(virtualKeyId, amount, totalSpend, token);
        token.ThrowIfCancellationRequested();
        if (budget is > 0)
        {
            await _budgetAlertManager.CheckBudgetThresholdsAsync(virtualKeyId, totalSpend, budget.Value,
                totalSpend / budget.Value * 100, token);
            token.ThrowIfCancellationRequested();
        }
        var notification = new SpendUpdateNotification
        {
            NewSpend = amount, TotalSpend = totalSpend, Budget = budget,
            BudgetPercentage = budget is > 0 ? totalSpend / budget.Value * 100 : null,
            Model = model, Provider = provider
        };
        await _hubContext.Clients.Group($"vkey-{virtualKeyId}").SendAsync("SpendUpdate", notification, token);
        token.ThrowIfCancellationRequested();
        await _patternAnalyzer.CheckUnusualSpendingAsync(virtualKeyId, token);
        token.ThrowIfCancellationRequested();
    });

    public Task NotifySpendUpdatedAsync(int virtualKeyId, decimal spendAmount, string? model, string? provider) =>
        NotifySpendUpdateAsync(virtualKeyId, spendAmount, spendAmount, null, model, provider);

    public Task SendSpendSummaryAsync(int virtualKeyId, SpendSummaryNotification summary) =>
        TrackOperation(token => _hubContext.Clients.Group($"vkey-{virtualKeyId}").SendAsync("SpendSummary", summary, token));

    public void RecordSpend(int virtualKeyId, decimal amount) =>
        _ = TrackOperation(token =>
        {
            token.ThrowIfCancellationRequested();
            return _repository.RecordSpendingPatternAsync(virtualKeyId, amount, amount, token);
        });

    public Task CheckUnusualSpendingAsync(int virtualKeyId) => TrackOperation(token =>
    {
        token.ThrowIfCancellationRequested();
        return _patternAnalyzer.CheckUnusualSpendingAsync(virtualKeyId, token);
    });

    private async Task RunPatternAnalysisAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_analysisInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { await _patternAnalyzer.AnalyzeAllPatternsAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { _logger.LogError(exception, "Error in periodic pattern analysis"); }
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RegisterInstanceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTime.UtcNow;
            await _repository.RegisterInstanceAsync(InstanceId, new SpendNotificationInstanceData(
                InstanceId, Environment.MachineName, Environment.ProcessId, now, now), cancellationToken);
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException exception) { _logger.LogWarning(exception, "Timed out registering spend notification instance"); }
        catch (RedisException exception) { _logger.LogWarning(exception, "Failed to register spend notification instance"); }
        catch (InvalidOperationException exception) { _logger.LogWarning(exception, "Invalid spend notification instance registration"); }
    }

    public void Dispose()
    {
        lock (_lifecycleSync)
        {
            if (_disposed) return;
            _disposed = true;
            _stopping = true;
            _shutdown.Cancel();
            _analysisCancellation?.Cancel();
            // Hosted shutdown owns all awaited dependency work. Disposal starts no I/O.
            // Stop owns generation disposal under its transition gate. Even a completed
            // generation's source can be in use by a concurrent Stop's CancelAsync.
            if (_shutdownTask is { IsCompleted: true }) _shutdown.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
