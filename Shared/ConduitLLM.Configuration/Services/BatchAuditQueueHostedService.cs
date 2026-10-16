using Microsoft.Extensions.Hosting;

namespace ConduitLLM.Configuration.Services;

/// <summary>Runs a borrowed process-local audit queue on every host.</summary>
public sealed class BatchAuditQueueHostedService : IHostedService
{
    private readonly IHostedService _innerService;

    public BatchAuditQueueHostedService(IHostedService inner)
    {
        _innerService = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _innerService.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        _innerService.StopAsync(cancellationToken);
}
