using ConduitLLM.Core.Extensions;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Admin.DTOs;
using ConduitLLM.Configuration;
using ConduitLLM.Configuration.DTOs.VirtualKey;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Extensions;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Services;
using VirtualKeyUtilities = ConduitLLM.Configuration.Utilities.VirtualKeyUtilities;

using ConduitLLM.Configuration.Messaging;
using Microsoft.EntityFrameworkCore;

namespace ConduitLLM.Admin.Services
{
    /// <summary>
    /// Service for managing virtual keys through the Admin API.
    /// Inherits shared CRUD operations from <see cref="VirtualKeyServiceBase"/> and adds
    /// Admin-specific concerns: cache invalidation, media cleanup, discovery, validation, and usage.
    /// </summary>
    public partial class AdminVirtualKeyService : VirtualKeyServiceBase, IAdminVirtualKeyService
    {
        // Aliases for partial class files that reference the underscore-prefixed fields
        private IVirtualKeyRepository _virtualKeyRepository => VirtualKeyRepository;
        private IVirtualKeyGroupRepository _groupRepository => GroupRepository;
        private IVirtualKeySpendHistoryRepository _spendHistoryRepository => SpendHistoryRepository;

        // Admin-specific dependencies
        private readonly IVirtualKeyCache? _cache;
        private readonly IMediaLifecycleService? _mediaLifecycleService;
        private readonly IMediaDeletionEngine? _mediaDeletionEngine;
        private readonly IDistributedLockProvider? _mediaCleanupLockService;
        private readonly IModelProviderMappingRepository _modelProviderMappingRepository;
        private readonly IModelCapabilityService _modelCapabilityService;
        private readonly IDbContextFactory<ConduitDbContext> _dbContextFactory;
        private readonly DiscoveryCacheOptions _discoveryOptions;
        private readonly ILogger<AdminVirtualKeyService> _logger;
        private readonly CancellationToken _shutdownToken;

        /// <summary>
        /// Initializes a new instance of the AdminVirtualKeyService class
        /// </summary>
        /// <param name="virtualKeyRepository">The virtual key repository</param>
        /// <param name="spendHistoryRepository">The spend history repository</param>
        /// <param name="groupRepository">The virtual key group repository</param>
        /// <param name="logger">The logger</param>
        /// <param name="modelProviderMappingRepository">The model provider mapping repository</param>
        /// <param name="modelCapabilityService">The model capability service</param>
        /// <param name="dbContextFactory">The database context factory</param>
        /// <param name="cache">Optional Redis cache for immediate invalidation (null if not configured)</param>
        /// <param name="eventBus">Optional event bus (null if not configured)</param>
        /// <param name="mediaLifecycleService">Optional media lifecycle service for cleaning up associated media files (null if not configured)</param>
        /// <param name="mediaDeletionEngine">Guarded media deletion engine.</param>
        /// <param name="mediaCleanupLockService">Distributed lock shared with scheduled cleanup.</param>
        /// <param name="discoveryOptions">Shared discovery response settings.</param>
        /// <param name="applicationLifetime">Host shutdown notification.</param>
        public AdminVirtualKeyService(
            IVirtualKeyRepository virtualKeyRepository,
            IVirtualKeySpendHistoryRepository spendHistoryRepository,
            IVirtualKeyGroupRepository groupRepository,
            ILogger<AdminVirtualKeyService> logger,
            IModelProviderMappingRepository modelProviderMappingRepository,
            IModelCapabilityService modelCapabilityService,
            IDbContextFactory<ConduitDbContext> dbContextFactory,
            IVirtualKeyCache? cache = null,
            IEventBus? eventBus = null,
            IMediaLifecycleService? mediaLifecycleService = null,
            IMediaDeletionEngine? mediaDeletionEngine = null,
            IDistributedLockProvider? mediaCleanupLockService = null,
            Microsoft.Extensions.Options.IOptions<DiscoveryCacheOptions>? discoveryOptions = null,
            Microsoft.Extensions.Hosting.IHostApplicationLifetime? applicationLifetime = null)
            : base(virtualKeyRepository, groupRepository, spendHistoryRepository, eventBus, logger)
        {
            _cache = cache;
            _mediaLifecycleService = mediaLifecycleService;
            _mediaDeletionEngine = mediaDeletionEngine;
            _mediaCleanupLockService = mediaCleanupLockService;
            _modelProviderMappingRepository = modelProviderMappingRepository ?? throw new ArgumentNullException(nameof(modelProviderMappingRepository));
            _modelCapabilityService = modelCapabilityService ?? throw new ArgumentNullException(nameof(modelCapabilityService));
            _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
            _discoveryOptions = discoveryOptions?.Value ?? new DiscoveryCacheOptions();
            _shutdownToken = applicationLifetime?.ApplicationStopping ?? CancellationToken.None;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        #region Virtual Hook Overrides

        /// <summary>
        /// Cleans up associated media files before a virtual key is deleted.
        /// </summary>
        public Task<bool> DeleteVirtualKeyAsync(int id, CancellationToken cancellationToken)
            => DeleteVirtualKeyCoreAsync(id, cancellationToken);

        protected override Task OnBeforeVirtualKeyDeleteAsync(int keyId)
            => OnBeforeVirtualKeyDeleteAsync(keyId, CancellationToken.None);

        protected override async Task OnBeforeVirtualKeyDeleteAsync(int keyId, CancellationToken cancellationToken)
        {
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdownToken);
            cancellationToken = requestCancellation.Token;
            if (_mediaLifecycleService != null)
            {
                await using var mediaContext =
                    await _dbContextFactory.CreateDbContextAsync(cancellationToken);
                var mediaRecords = await mediaContext.MediaRecords
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(record => record.VirtualKeyId == keyId)
                    .ToListAsync(cancellationToken);
                if (mediaRecords.Count == 0)
                {
                    return;
                }

                if (_mediaDeletionEngine == null || _mediaCleanupLockService == null)
                {
                    throw new InvalidOperationException(
                        "Virtual key deletion is blocked because the guarded media deletion engine is unavailable.");
                }

                await using var lockHandle = await _mediaCleanupLockService.TryAcquireAsync(
                    MediaCleanupLock.Key,
                    TimeSpan.Zero, cancellationToken);
                if (lockHandle == null)
                {
                    throw new InvalidOperationException(
                        "Virtual key deletion is blocked while another media cleanup run is active.");
                }

                using var operationCancellation = lockHandle.CreateOperationCancellation(
                    cancellationToken, MediaCleanupLock.OperationDeadline);
                var protectedToken = operationCancellation.Token;
                protectedToken.ThrowIfCancellationRequested();
                var operation = new MediaDeletionOperationContext(
                    MediaCleanupTypes.VirtualKey,
                    "virtual-key",
                    $"virtual-key:{keyId}");
                var result = await _mediaDeletionEngine.ExecuteOperationAsync(
                    operation,
                    () => _mediaDeletionEngine.DeleteAsync(
                        new MediaDeletionRequest(
                            mediaRecords,
                            operation,
                            Purge: true), protectedToken), protectedToken);
                protectedToken.ThrowIfCancellationRequested();

                if (result.IsDryRun || result.BudgetExhausted || result.Failures > 0)
                {
                    throw new InvalidOperationException(
                        $"Virtual key deletion aborted: media cleanup was not complete " +
                        $"(dryRun={result.IsDryRun}, budgetExhausted={result.BudgetExhausted}, " +
                        $"failed={result.Failures}, deleted={result.FilesDeleted}).");
                }

                Logger.LogInformation(
                    "Deleted {DeletedCount} media files for virtual key {KeyId}",
                    result.FilesDeleted, keyId);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Virtual key deletion is blocked because media tracking is unavailable for key {keyId}.");
            }
        }

        /// <summary>
        /// Invalidates the cache entry for an updated virtual key.
        /// </summary>
        protected override async Task OnVirtualKeyUpdatedAsync(VirtualKey key, string[] changedProperties)
        {
            if (_cache != null)
                await _cache.InvalidateVirtualKeyAsync(key.KeyHash);
        }

        /// <summary>
        /// Invalidates the cache entry for a deleted virtual key.
        /// </summary>
        protected override async Task OnVirtualKeyDeletedAsync(VirtualKey key)
        {
            if (_cache != null)
                await _cache.InvalidateVirtualKeyAsync(key.KeyHash);
        }

        #endregion

        /// <inheritdoc />
        public async Task<List<VirtualKeyDto>> ListVirtualKeysAsync(int? virtualKeyGroupId = null)
        {
            if (virtualKeyGroupId.HasValue)
            {
                _logger.LogDebug("Listing virtual keys for group {GroupId}", virtualKeyGroupId.Value);
                var keysByGroup = await RepositoryPaginationExtensions.GetAllViaPaginationAsync(
                    VirtualKeyRepository.GetByVirtualKeyGroupIdPaginatedAsync, virtualKeyGroupId.Value);
                return keysByGroup.ConvertAll(VirtualKeyUtilities.MapToDto);
            }
            else
            {
                _logger.LogDebug("Listing all virtual keys");
                var keys = await RepositoryPaginationExtensions.GetAllViaPaginationAsync(
                    VirtualKeyRepository.GetPaginatedAsync);
                return keys.ConvertAll(VirtualKeyUtilities.MapToDto);
            }
        }


    }
}
