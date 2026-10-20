using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Persistence.Interfaces;

/// <summary>Reads the persisted routing policy for a model alias without exposing a query provider.</summary>
public interface IModelRoutePolicyReader
{
    /// <summary>Returns the policy when present, including administratively disabled policies.</summary>
    Task<ModelRoutePolicy?> GetByAliasAsync(string modelAlias, CancellationToken cancellationToken = default);
}
