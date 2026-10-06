namespace ConduitLLM.Core.Interfaces;

/// <summary>Routing dependencies expire the entire mapping graph, including old aliases and lists.</summary>
public interface IModelMappingCacheInvalidator
{
    Task InvalidateAsync(CancellationToken cancellationToken = default);
}
