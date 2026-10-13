using ConduitLLM.Core.Interfaces;
using Microsoft.Extensions.Hosting;

namespace ConduitLLM.Core.Services;

/// <summary>Stops the borrowed election singleton while its Redis and logging dependencies are alive.</summary>
public sealed class LeaderElectionLifecycleHostedService(ILeaderElectionService election) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => election is RedisLeaderElectionService redis
        ? redis.ShutdownAsync(cancellationToken)
        : Task.CompletedTask;
}
