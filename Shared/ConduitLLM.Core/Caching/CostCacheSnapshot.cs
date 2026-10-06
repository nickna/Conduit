using System.Text.Json;
using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Core.Caching;

public sealed record CostCacheSnapshot(int Version, CostRow Cost, List<CostAssociationSnapshot> Associations)
{
    public static CostCacheSnapshot From(ModelCost value) => new(1, CostRow.From(value),
        value.ModelProviderTypeAssociations.Select(association => new CostAssociationSnapshot(AssociationRow.From(association),
            association.Model is null ? null : ModelRow.From(association.Model),
            association.Model?.Series is null ? null : SeriesRow.From(association.Model.Series))).ToList());
    public ModelCost ToDomain()
    {
        if (Version != 1 || Cost is null || Associations is null || Associations.Any(snapshot => snapshot?.Association is null))
            throw new JsonException("Incomplete or obsolete cost snapshot.");
        var result = Cost.ToDomain();
        result.ModelProviderTypeAssociations = Associations.Select(snapshot =>
        {
            var association = snapshot.Association.ToDomain();
            association.Model = snapshot.Model?.ToDomain()!;
            if (association.Model is not null) association.Model.Series = snapshot.Series?.ToDomain()!;
            association.ModelCost = result;
            return association;
        }).ToList();
        return result;
    }
}
public sealed record CostAssociationSnapshot(AssociationRow Association, ModelRow? Model, SeriesRow? Series);
/// <summary>Missing means no currently usable cost; it never implies a valid zero price.</summary>
public sealed record CostLookupResult(CostCacheSnapshot? Value, DateTime ValidUntil);
