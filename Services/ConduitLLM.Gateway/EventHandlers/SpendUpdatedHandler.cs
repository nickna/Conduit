using ConduitLLM.Configuration.Messaging;
using ConduitLLM.Core.Events;
using CoreInterfaces = ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Configuration.Interfaces;

namespace ConduitLLM.Gateway.EventHandlers
{
    /// <summary>
    /// Handles SpendUpdated events and sends real-time notifications through SignalR.
    /// </summary>
    public class SpendUpdatedHandler : IEventHandler<SpendUpdated>
    {
        private readonly ISpendNotificationService _notificationService;
        private readonly CoreInterfaces.IVirtualKeyRuntimeService _virtualKeyService;
        private readonly IVirtualKeyGroupRepository _groupRepository;
        private readonly ILogger<SpendUpdatedHandler> _logger;

        public SpendUpdatedHandler(
            ISpendNotificationService notificationService,
            CoreInterfaces.IVirtualKeyRuntimeService virtualKeyService,
            IVirtualKeyGroupRepository groupRepository,
            ILogger<SpendUpdatedHandler> logger)
        {
            _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
            _virtualKeyService = virtualKeyService ?? throw new ArgumentNullException(nameof(virtualKeyService));
            _groupRepository = groupRepository ?? throw new ArgumentNullException(nameof(groupRepository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task HandleAsync(SpendUpdated message, IEventContext context)
        {
            try
            {
                _logger.LogInformation(
                    "Processing spend notification for Virtual Key {KeyId}: ${Amount:F2} spent, new total: ${NewTotal:F2}",
                    message.KeyId,
                    message.Amount,
                    message.NewTotalSpend);

                // Get virtual key details for budget information
                var virtualKey = await _virtualKeyService.GetVirtualKeyInfoForValidationAsync(message.KeyId);
                if (virtualKey == null)
                {
                    _logger.LogWarning("Virtual key {KeyId} not found for spend notification", message.KeyId);
                    return;
                }

                // Get the key's group for budget information
                var group = await _groupRepository.GetByIdAsync(virtualKey.VirtualKeyGroupId);
                decimal? maxBudget = group?.Balance;

                // Extract model and provider from message headers when present; SpendUpdated
                // events from the batch pipeline aggregate many requests, so attribution is
                // often genuinely absent — leave null rather than inventing a value
                string? model = null;
                string? provider = null;
                if (context.TryGetHeader("Model", out var modelHeader))
                {
                    model = modelHeader?.ToString();
                }
                if (context.TryGetHeader("Provider", out var providerHeader))
                {
                    provider = providerHeader?.ToString();
                }

                // Log budget proximity warnings
                if (maxBudget.HasValue && maxBudget.Value > 0)
                {
                    var usagePercent = (message.NewTotalSpend / maxBudget.Value) * 100;
                    if (usagePercent >= 100)
                    {
                        _logger.LogWarning(
                            "Virtual Key {KeyId} has exceeded its budget: ${NewTotal:F2} / ${MaxBudget:F2} ({UsagePercent:F0}%)",
                            message.KeyId, message.NewTotalSpend, maxBudget.Value, usagePercent);
                    }
                    else if (usagePercent >= 90)
                    {
                        _logger.LogWarning(
                            "Virtual Key {KeyId} approaching budget limit: ${NewTotal:F2} / ${MaxBudget:F2} ({UsagePercent:F0}%)",
                            message.KeyId, message.NewTotalSpend, maxBudget.Value, usagePercent);
                    }
                }

                // Send the spend notification
                await _notificationService.NotifySpendUpdateAsync(
                    message.KeyId,
                    message.Amount,
                    message.NewTotalSpend,
                    maxBudget,
                    model,
                    provider);

                _logger.LogDebug(
                    "Spend notification sent for Virtual Key {KeyId} with correlation ID {CorrelationId}",
                    message.KeyId,
                    message.CorrelationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, 
                    "Error processing spend notification for Virtual Key {KeyId}",
                    message.KeyId);
                
                // Don't throw - we don't want to retry notifications
                // They are best-effort and should not block the main flow
            }
        }
    }
}
