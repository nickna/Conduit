using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Configuration.Interfaces;

/// <summary>
/// Complete persistence operations for provider credentials and primary-key
/// selection.
/// </summary>
public interface IProviderKeyCredentialRepository
{
    Task<(List<ProviderKeyCredential> Items, int TotalCount)> GetPaginatedAsync(
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<(List<ProviderKeyCredential> Items, int TotalCount)> GetByProviderIdPaginatedAsync(
        int providerId,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<ProviderKeyCredential?> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<int> CreateAsync(
        ProviderKeyCredential credential,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(
        ProviderKeyCredential credential,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);

    Task<ProviderKeyCredential?> GetPrimaryKeyAsync(
        int providerId,
        CancellationToken cancellationToken = default);

    Task<List<ProviderKeyCredential>> GetEnabledKeysByProviderIdAsync(
        int providerId,
        CancellationToken cancellationToken = default);

    Task<bool> SetPrimaryKeyAsync(
        int providerId,
        int keyId,
        CancellationToken cancellationToken = default);

    Task<bool> HasKeyCredentialsAsync(
        int providerId,
        CancellationToken cancellationToken = default);

    Task<int> CountByProviderIdAsync(
        int providerId,
        CancellationToken cancellationToken = default);
}
