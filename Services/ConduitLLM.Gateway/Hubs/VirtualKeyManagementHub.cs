using Microsoft.AspNetCore.SignalR;
using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Gateway.Metrics;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Core.Interfaces;
namespace ConduitLLM.Gateway.Hubs
{
    // SignalR group-union sends do not deduplicate connections. Broad lifecycle
    // recipients therefore partition into non-admin owners and administrators.
    internal static class VirtualKeyManagementGroups
    {
        internal static string NonAdminOwner(int keyId) => $"vkey-lifecycle-owner-{keyId}";
        internal static string Status(int keyId) => $"vkey-status-{keyId}";
    }

    /// <summary>
    /// SignalR hub for real-time virtual key management notifications.
    /// Provides real-time updates for virtual key operations, status changes, and spend tracking.
    /// </summary>
    public class VirtualKeyManagementHub : SecureHub
    {
        private readonly SignalRMetrics _metrics;
        private readonly ILogger<VirtualKeyManagementHub> _logger;
        private readonly IVirtualKeyRuntimeService _virtualKeyService;
        
        /// <summary>
        /// Initializes a new instance of the <see cref="VirtualKeyManagementHub"/> class.
        /// </summary>
        /// <param name="metrics">SignalR metrics collector.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="serviceProvider">Service provider for dependency injection.</param>
        /// <param name="virtualKeyService">Request-time virtual key service.</param>
        public VirtualKeyManagementHub(
            SignalRMetrics metrics,
            ILogger<VirtualKeyManagementHub> logger,
            IServiceProvider serviceProvider,
            IVirtualKeyRuntimeService virtualKeyService) : base(logger, serviceProvider)
        {
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _virtualKeyService = virtualKeyService ?? throw new ArgumentNullException(nameof(virtualKeyService));
        }

        /// <summary>
        /// Gets the hub name for logging and metrics.
        /// </summary>
        /// <returns>The hub name.</returns>
        protected override string GetHubName() => "VirtualKeyManagementHub";

        /// <summary>
        /// Called when a client connects to the hub.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public override async Task OnConnectedAsync()
        {
            await base.OnConnectedAsync();
            
            var virtualKeyId = GetVirtualKeyId();
            if (virtualKeyId is > 0)
            {
                // Lifecycle publication is server-only. Administrative recipients join
                // through trusted authentication, never a caller-supplied group name.
                if (await IsAdminAsync())
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, "admin", Context.ConnectionAborted);
                }
                else
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId,
                        VirtualKeyManagementGroups.NonAdminOwner(virtualKeyId.Value), Context.ConnectionAborted);
                }
                // Owners and authorized management subscribers share one status
                // group, so an administrator monitoring its own key gets one event.
                await Groups.AddToGroupAsync(Context.ConnectionId,
                    VirtualKeyManagementGroups.Status(virtualKeyId.Value), Context.ConnectionAborted);

                // Send initial virtual key status to the connected client
                await SendInitialKeyStatus(virtualKeyId.Value);
                
                _logger.LogInformation(
                    "Client connected to VirtualKeyManagementHub: {ConnectionId} for VirtualKey: {VirtualKeyId}",
                    Context.ConnectionId,
                    virtualKeyId.Value);
            }
        }

        /// <summary>
        /// Subscribes to management updates for another virtual key (admin only).
        /// </summary>
        /// <param name="targetKeyId">The virtual key ID to monitor.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task SubscribeToKeyManagement(int targetKeyId)
        {
            var correlationId = GetOrCreateCorrelationId();
            
            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["CorrelationId"] = correlationId,
                ["TargetKeyId"] = targetKeyId,
                ["RequestingKeyId"] = GetVirtualKeyId()?.ToString() ?? "unknown"
            }))
            {
                try
                {
                    // Check if the requesting key has admin privileges
                    if (!await IsAdminAsync())
                    {
                        await Clients.Caller.SendAsync("Error", new HubErrorNotification
                        {
                            Message = "Unauthorized: Admin privileges required"
                        });
                        return;
                    }
                    
                    // Verify the target key exists
                    var targetKey = await _virtualKeyService.GetVirtualKeyInfoForValidationAsync(targetKeyId);
                    if (targetKey == null)
                    {
                        await Clients.Caller.SendAsync("Error", new HubErrorNotification
                        {
                            Message = $"Virtual key {targetKeyId} not found"
                        });
                        return;
                    }
                    
                    // Authorization is rechecked on subscription. Reconcile a
                    // connection promoted since it connected before joining its
                    // target audience, without overlapping owner/admin groups.
                    var ownerId = GetVirtualKeyId();
                    if (ownerId is > 0)
                        await Groups.RemoveFromGroupAsync(Context.ConnectionId,
                            VirtualKeyManagementGroups.NonAdminOwner(ownerId.Value), Context.ConnectionAborted);
                    await Groups.AddToGroupAsync(Context.ConnectionId, "admin", Context.ConnectionAborted);

                    // Add to the canonical status group for the target key.
                    var groupName = VirtualKeyManagementGroups.Status(targetKeyId);
                    await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
                    
                    _logger.LogInformation(
                        "Admin key subscribed to management updates for virtual key {TargetKeyId}",
                        targetKeyId);
                    
                    // Send current status of the target key
                    await SendKeyStatusToClient(targetKey);
                    
                    await Clients.Caller.SendAsync("SubscribedToKeyManagement", targetKeyId);
                }
                catch (Exception ex)
                {
                    _metrics.HubErrors.Add(1,
                        new("hub", "VirtualKeyManagementHub"),
                        new("error_type", ex.GetType().Name));
                    _logger.LogError(ex, "Error subscribing to key management for {TargetKeyId}", targetKeyId);
                    await Clients.Caller.SendAsync("Error", new HubErrorNotification
                    {
                        Message = "Failed to subscribe to key management"
                    });
                }
            }
        }

        /// <summary>
        /// Unsubscribes from management updates for a virtual key.
        /// </summary>
        /// <param name="targetKeyId">The virtual key ID to stop monitoring.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task UnsubscribeFromKeyManagement(int targetKeyId)
        {
            try
            {
                // Unsubscribing from management must not remove the owner's
                // connection-time status audience for its own key.
                if (GetVirtualKeyId() != targetKeyId)
                {
                    var groupName = VirtualKeyManagementGroups.Status(targetKeyId);
                    await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
                }
                
                _logger.LogInformation(
                    "Unsubscribed from management updates for virtual key {TargetKeyId}",
                    targetKeyId);
                
                await Clients.Caller.SendAsync("UnsubscribedFromKeyManagement", targetKeyId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error unsubscribing from key management for {TargetKeyId}", targetKeyId);
            }
        }

        /// <summary>
        /// Requests the current status of the authenticated virtual key.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task GetCurrentKeyStatus()
        {
            var virtualKeyId = RequireVirtualKeyId();
            
            try
            {
                var virtualKey = await _virtualKeyService.GetVirtualKeyInfoForValidationAsync(virtualKeyId);
                if (virtualKey != null)
                {
                    await SendKeyStatusToClient(virtualKey);
                }
                else
                {
                    await Clients.Caller.SendAsync("Error", new HubErrorNotification
                    {
                        Message = "Virtual key not found"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting current key status for {VirtualKeyId}", virtualKeyId);
                await Clients.Caller.SendAsync("Error", new HubErrorNotification
                {
                    Message = "Failed to get key status"
                });
            }
        }

        /// <summary>
        /// Sends the initial key status to the connected client.
        /// </summary>
        private async Task SendInitialKeyStatus(int virtualKeyId)
        {
            try
            {
                var virtualKey = await _virtualKeyService.GetVirtualKeyInfoForValidationAsync(virtualKeyId);
                if (virtualKey != null)
                {
                    await SendKeyStatusToClient(virtualKey);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending initial key status for {VirtualKeyId}", virtualKeyId);
            }
        }

        /// <summary>
        /// Sends the key status to the calling client.
        /// </summary>
        private async Task SendKeyStatusToClient(VirtualKey virtualKey)
        {
            // Runtime key snapshots already carry the complete group balance needed by
            // this notification. Re-querying the management repository here pulled EF
            // back into every native SignalR connection.
            var group = virtualKey.VirtualKeyGroup;

            var status = new VirtualKeyStatusNotification
            {
                KeyId = virtualKey.Id,
                KeyName = virtualKey.KeyName,
                IsEnabled = virtualKey.IsEnabled,
                CurrentSpend = group?.LifetimeSpent ?? 0,
                MaxBudget = group?.Balance,
                BudgetPercentage = null, // No longer applicable in bank account model
                AllowedModels = virtualKey.AllowedModels,
                RateLimitPerMinute = virtualKey.RateLimitRpm,
                CreatedAt = virtualKey.CreatedAt,
                LastUsedAt = null, // This property doesn't exist in VirtualKey entity
                ExpiresAt = virtualKey.ExpiresAt
            };
            
            await Clients.Caller.SendAsync("VirtualKeyStatus", status);
        }
    }
}
