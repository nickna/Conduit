namespace ConduitLLM.Gateway.Services.SpendNotification;

/// <summary>Joins the borrowed singleton after every hosted producer has stopped.</summary>
public sealed class SpendNotificationLifecycleHostedService(DistributedSpendNotificationService service) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => service.StopAsync(cancellationToken);
    public Task StoppedAsync(CancellationToken cancellationToken) => service.ShutdownAsync(cancellationToken);
}
