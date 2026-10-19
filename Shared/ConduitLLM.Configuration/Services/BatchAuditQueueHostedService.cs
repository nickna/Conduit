using Microsoft.Extensions.Hosting;

namespace ConduitLLM.Configuration.Services;

/// <summary>Runs a borrowed process-local audit queue on every host.</summary>
public sealed class BatchAuditQueueHostedService : IHostedService
{
    private readonly IHostedService _innerService;
    private readonly object _shutdown = new();
    private Task? _stopTask;

    public BatchAuditQueueHostedService(IHostedService inner)
    {
        _innerService = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _innerService.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_shutdown)
        {
            // The entrypoint and its host owner can both request shutdown. Join
            // the same drain, including after DI has disposed the borrowed queue.
            // A failed/canceled drain remains retryable with a new deadline.
            if (_stopTask is null || _stopTask.IsFaulted || _stopTask.IsCanceled)
                _stopTask = _innerService.StopAsync(cancellationToken);
            return _stopTask;
        }
    }
}
