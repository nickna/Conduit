using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Interfaces;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Gateway.EventHandlers
{
    /// <summary>
    /// Handles DiscoveryCacheInvalidationRequested events from Admin API
    /// Invalidates the discovery cache across all Gateway API instances
    /// </summary>
    public class DiscoveryCacheInvalidationHandler : IEventHandler<DiscoveryCacheInvalidationRequested>
    {
        private readonly IDiscoveryCacheService _discoveryCacheService;
        private readonly ILogger<DiscoveryCacheInvalidationHandler> _logger;

        public DiscoveryCacheInvalidationHandler(
            IDiscoveryCacheService discoveryCacheService,
            ILogger<DiscoveryCacheInvalidationHandler> logger)
        {
            _discoveryCacheService = discoveryCacheService ?? throw new ArgumentNullException(nameof(discoveryCacheService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Handles manual discovery cache invalidation requests from Admin API
        /// </summary>
        public async Task HandleAsync(DiscoveryCacheInvalidationRequested message, IEventContext context)
        {
            _logger.LogInformation(
                "Processing discovery cache invalidation request. Reason: {Reason}, Requested by: {RequestedBy}",
                message.Reason,
                message.RequestedBy);

            // Invalidate all discovery cache entries
            await _discoveryCacheService.InvalidateAllDiscoveryAsync(context.CancellationToken);

            _logger.LogInformation(
                "Successfully invalidated all discovery cache entries. Reason: {Reason}",
                message.Reason);
        }
    }
}
