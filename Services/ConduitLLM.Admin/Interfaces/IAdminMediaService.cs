using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;

namespace ConduitLLM.Admin.Interfaces
{
    /// <summary>
    /// Service interface for administrative media management operations.
    /// </summary>
    public interface IAdminMediaService
    {
        /// <summary>
        /// Gets storage statistics for all virtual keys.
        /// </summary>
        /// <param name="virtualKeyGroupId">Optional filter by virtual key group ID</param>
        /// <returns>Overall storage statistics.</returns>
        Task<OverallMediaStorageStats> GetOverallStorageStatsAsync(int? virtualKeyGroupId = null);

        /// <summary>
        /// Gets storage statistics for a specific virtual key.
        /// </summary>
        /// <param name="virtualKeyId">The ID of the virtual key.</param>
        /// <returns>Storage statistics for the virtual key.</returns>
        Task<MediaStorageStats> GetStorageStatsByVirtualKeyAsync(int virtualKeyId);

        /// <summary>
        /// Gets media records for a specific virtual key.
        /// </summary>
        /// <param name="virtualKeyId">The ID of the virtual key.</param>
        /// <param name="includeDeleted">Whether tombstoned records should be returned.</param>
        /// <returns>List of media records.</returns>
        Task<List<MediaRecord>> GetMediaByVirtualKeyAsync(
            int virtualKeyId,
            bool includeDeleted = false);

        /// <summary>
        /// Deletes a specific media record.
        /// </summary>
        /// <param name="mediaId">The ID of the media record.</param>
        /// <param name="cancellationToken">Request/shutdown cancellation.</param>
        /// <returns>The deletion result, or null when the active record was not found.</returns>
        Task<AdminMediaDeleteResult?> DeleteMediaAsync(Guid mediaId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Restores a tombstoned media record while its recovery window remains open.
        /// </summary>
        Task<MediaRestoreOutcome> RestoreMediaAsync(Guid mediaId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets media records by storage key pattern.
        /// </summary>
        /// <param name="storageKeyPattern">Pattern to match storage keys.</param>
        /// <returns>List of matching media records.</returns>
        Task<List<MediaRecord>> SearchMediaByStorageKeyAsync(string storageKeyPattern);

        /// <summary>
        /// Gets storage statistics grouped by provider.
        /// </summary>
        /// <returns>Dictionary of provider names to storage size.</returns>
        Task<Dictionary<string, long>> GetStorageStatsByProviderAsync();

        /// <summary>
        /// Gets storage statistics grouped by media type.
        /// </summary>
        /// <returns>Dictionary of media types to storage size.</returns>
        Task<Dictionary<string, long>> GetStorageStatsByMediaTypeAsync();
    }

    public sealed record AdminMediaDeleteResult(bool IsSoftDeleted, DateTime? DeletedAt);

    public enum MediaRestoreOutcome
    {
        Restored,
        NotFound,
        NotDeleted,
        GracePeriodElapsed,
        CleanupInProgress
    }
}
