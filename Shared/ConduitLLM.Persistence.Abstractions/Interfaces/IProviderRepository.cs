using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Configuration.Interfaces;

/// <summary>
/// Complete persistence operations required for configured providers.
/// </summary>
public interface IProviderRepository
{
    Task<IReadOnlyList<Provider>> ListAsync(CancellationToken cancellationToken = default);

    Task<(List<Provider> Items, int TotalCount)> GetPaginatedAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Provider?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<int> CreateAsync(Provider provider, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(Provider provider, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);

    Task<Dictionary<int, string>> GetProviderNameMapAsync(
        CancellationToken cancellationToken = default);

    Task<int> CountAsync(
        bool? enabledOnly,
        CancellationToken cancellationToken = default);
}
