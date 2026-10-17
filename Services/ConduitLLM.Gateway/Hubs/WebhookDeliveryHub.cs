using Microsoft.AspNetCore.SignalR;
using ConduitLLM.Core.Constants;
using ConduitLLM.Core.Services;

namespace ConduitLLM.Gateway.Hubs
{
    /// <summary>
    /// SignalR hub for real-time webhook delivery tracking and notifications.
    /// </summary>
    public class WebhookDeliveryHub : SecureHub
    {
        private readonly ILogger<WebhookDeliveryHub> _logger;
        private readonly IWebhookConnectionTracker _connectionTracker;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebhookDeliveryHub"/> class.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="serviceProvider">Service provider for dependency resolution.</param>
        /// <param name="connectionTracker">Tracks each connection's webhook group subscriptions.</param>
        public WebhookDeliveryHub(
            ILogger<WebhookDeliveryHub> logger,
            IServiceProvider serviceProvider,
            IWebhookConnectionTracker connectionTracker)
            : base(logger, serviceProvider)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _connectionTracker = connectionTracker ?? throw new ArgumentNullException(nameof(connectionTracker));
        }

        /// <summary>
        /// Gets the name of the hub for logging and metrics.
        /// </summary>
        /// <returns>The hub name.</returns>
        protected override string GetHubName() => "WebhookDeliveryHub";

        protected override async Task OnVirtualKeyConnectedAsync(int virtualKeyId, string virtualKeyName)
        {
            // Installation-wide statistics are available only to trusted administrators.
            if (virtualKeyId > 0 && await IsAdminAsync())
                await Groups.AddToGroupAsync(Context.ConnectionId, "admin", Context.ConnectionAborted);
        }

        /// <summary>
        /// Called when a client connects to the hub.
        /// </summary>
        public override async Task OnConnectedAsync()
        {
            await base.OnConnectedAsync();
            
            _logger.LogInformation(
                "Client {ConnectionId} connected to WebhookDeliveryHub",
                Context.ConnectionId);
        }

        /// <summary>
        /// Called when a client disconnects from the hub.
        /// </summary>
        /// <param name="exception">Exception that caused the disconnect, if any.</param>
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            // Clean up webhook tracking
            await _connectionTracker.RemoveConnectionAsync(Context.ConnectionId);
            
            await base.OnDisconnectedAsync(exception);
            
            _logger.LogInformation(
                "Client {ConnectionId} disconnected from WebhookDeliveryHub",
                Context.ConnectionId);
        }

        /// <summary>
        /// Subscribe to webhook delivery updates for specific URLs.
        /// </summary>
        /// <param name="webhookUrls">The webhook URLs to track.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task SubscribeToWebhooks(string[] webhookUrls)
        {
            if (webhookUrls == null || webhookUrls.Length == 0)
            {
                return;
            }

            var virtualKeyId = RequireVirtualKeyId();
            if (virtualKeyId <= 0)
            {
                throw new HubException("Unauthorized");
            }

            // Add to connection's webhook list
            await _connectionTracker.AddWebhooksToConnectionAsync(Context.ConnectionId, webhookUrls);
            
            // Join groups for these webhook URLs
            foreach (var url in webhookUrls)
            {
                var groupName = SignalRConstants.Groups.Webhook(virtualKeyId, url);
                await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
            }

            _logger.LogInformation(
                "Client {ConnectionId} subscribed to {Count} webhook URLs",
                Context.ConnectionId, webhookUrls.Length);
        }

        /// <summary>
        /// Unsubscribe from webhook delivery updates for specific URLs.
        /// </summary>
        /// <param name="webhookUrls">The webhook URLs to stop tracking.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task UnsubscribeFromWebhooks(string[] webhookUrls)
        {
            if (webhookUrls == null || webhookUrls.Length == 0)
            {
                return;
            }

            var virtualKeyId = RequireVirtualKeyId();
            if (virtualKeyId <= 0)
            {
                throw new HubException("Unauthorized");
            }

            await _connectionTracker.RemoveWebhooksFromConnectionAsync(Context.ConnectionId, webhookUrls);

            foreach (var url in webhookUrls)
            {
                var groupName = SignalRConstants.Groups.Webhook(virtualKeyId, url);
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
            }

            _logger.LogInformation(
                "Client {ConnectionId} unsubscribed from {Count} webhook URLs",
                Context.ConnectionId, webhookUrls.Length);
        }

        /// <summary>
        /// Request current statistics for tracked webhooks.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task RequestStatistics()
        {
            var virtualKeyId = GetVirtualKeyId();
            if (!virtualKeyId.HasValue)
            {
                await Clients.Caller.SendAsync("Error", "Authentication required");
                return;
            }

            // This would typically fetch statistics from a service
            // For now, send a placeholder response
            await Clients.Caller.SendAsync("StatisticsRequested", Context.ConnectionId);
            
            _logger.LogDebug(
                "Statistics requested by client {ConnectionId}",
                Context.ConnectionId);
        }

    }
}
