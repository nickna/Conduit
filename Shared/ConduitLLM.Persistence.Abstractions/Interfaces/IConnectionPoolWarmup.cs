namespace ConduitLLM.Persistence.Interfaces;

/// <summary>Establishes and verifies one connection in the host's persistence pool.</summary>
public interface IConnectionPoolWarmup
{
    Task WarmConnectionAsync(CancellationToken cancellationToken = default);
}
