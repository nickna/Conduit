using ConduitLLM.Core.Constants;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ConduitLLM.Core.Services
{
    /// <summary>
    /// Interface for tracking webhook connections across instances
    /// </summary>
    public interface IWebhookConnectionTracker
    {
        /// <summary>
        /// Adds webhooks to a connection's tracking list
        /// </summary>
        Task AddWebhooksToConnectionAsync(string connectionId, IEnumerable<string> webhookUrls);
        
        /// <summary>
        /// Removes webhooks from a connection's tracking list
        /// </summary>
        Task RemoveWebhooksFromConnectionAsync(string connectionId, IEnumerable<string> webhookUrls);
        
        /// <summary>
        /// Gets all webhooks tracked by a connection
        /// </summary>
        Task<HashSet<string>> GetConnectionWebhooksAsync(string connectionId);
        
        /// <summary>
        /// Gets all connections tracking a specific webhook
        /// </summary>
        Task<HashSet<string>> GetWebhookConnectionsAsync(string webhookUrl);
        
        /// <summary>
        /// Removes all webhooks for a connection (on disconnect)
        /// </summary>
        Task RemoveConnectionAsync(string connectionId);
        
        /// <summary>
        /// Gets count of connections tracking a webhook
        /// </summary>
        Task<int> GetWebhookConnectionCountAsync(string webhookUrl);
    }
    
    /// <summary>
    /// Redis-based implementation of webhook connection tracking
    /// Allows distributed tracking of which connections are monitoring which webhooks
    /// </summary>
    public class RedisWebhookConnectionTracker : RedisWebhookServiceBase, IWebhookConnectionTracker
    {
        private const int CONNECTION_EXPIRY_HOURS = 24;

        public RedisWebhookConnectionTracker(
            IConnectionMultiplexer redis,
            ILogger<RedisWebhookConnectionTracker> logger)
            : base(redis, logger)
        {
        }
        
        public async Task AddWebhooksToConnectionAsync(string connectionId, IEnumerable<string> webhookUrls)
        {
            try
            {
                var db = Redis.GetDatabase();
                var transaction = db.CreateTransaction();
                
                var connectionKey = RedisKeys.WebhookConnection.ConnectionWebhooks(connectionId);
                var timestampKey = RedisKeys.WebhookConnection.ConnectionTimestamp(connectionId);
                
                foreach (var webhookUrl in webhookUrls)
                {
                    // Add webhook to connection's set
                    _ = transaction.SetAddAsync(connectionKey, webhookUrl);
                    
                    // Add connection to webhook's set
                    var webhookKey = RedisKeys.WebhookConnection.WebhookConnections(GetUrlHash(webhookUrl));
                    _ = transaction.SetAddAsync(webhookKey, connectionId);
                    _ = transaction.KeyExpireAsync(webhookKey, TimeSpan.FromHours(CONNECTION_EXPIRY_HOURS));
                }
                
                // Update connection timestamp
                _ = transaction.StringSetAsync(timestampKey, DateTime.UtcNow.ToString("O"), 
                    TimeSpan.FromHours(CONNECTION_EXPIRY_HOURS));
                _ = transaction.KeyExpireAsync(connectionKey, TimeSpan.FromHours(CONNECTION_EXPIRY_HOURS));
                
                await transaction.ExecuteAsync();
                
                Logger.LogDebug("Added {Count} webhooks to connection {ConnectionId}", 
                    webhookUrls.Count(), connectionId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error adding webhooks to connection {ConnectionId}", connectionId);
            }
        }
        
        public async Task RemoveWebhooksFromConnectionAsync(string connectionId, IEnumerable<string> webhookUrls)
        {
            try
            {
                var db = Redis.GetDatabase();
                var transaction = db.CreateTransaction();
                
                var connectionKey = RedisKeys.WebhookConnection.ConnectionWebhooks(connectionId);
                
                foreach (var webhookUrl in webhookUrls)
                {
                    // Remove webhook from connection's set
                    _ = transaction.SetRemoveAsync(connectionKey, webhookUrl);
                    
                    // Remove connection from webhook's set
                    var webhookKey = RedisKeys.WebhookConnection.WebhookConnections(GetUrlHash(webhookUrl));
                    _ = transaction.SetRemoveAsync(webhookKey, connectionId);
                }
                
                await transaction.ExecuteAsync();
                
                Logger.LogDebug("Removed {Count} webhooks from connection {ConnectionId}", 
                    webhookUrls.Count(), connectionId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error removing webhooks from connection {ConnectionId}", connectionId);
            }
        }
        
        public async Task<HashSet<string>> GetConnectionWebhooksAsync(string connectionId)
        {
            try
            {
                var db = Redis.GetDatabase();
                var connectionKey = RedisKeys.WebhookConnection.ConnectionWebhooks(connectionId);
                var webhooks = await db.SetMembersAsync(connectionKey);
                
                return webhooks.Select(w => w.ToString()).ToHashSet();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error getting webhooks for connection {ConnectionId}", connectionId);
                return new HashSet<string>();
            }
        }
        
        public async Task<HashSet<string>> GetWebhookConnectionsAsync(string webhookUrl)
        {
            try
            {
                var db = Redis.GetDatabase();
                var webhookKey = RedisKeys.WebhookConnection.WebhookConnections(GetUrlHash(webhookUrl));
                var connections = await db.SetMembersAsync(webhookKey);
                
                return connections.Select(c => c.ToString()).ToHashSet();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error getting connections for webhook {WebhookUrl}", webhookUrl);
                return new HashSet<string>();
            }
        }
        
        public async Task RemoveConnectionAsync(string connectionId)
        {
            try
            {
                var db = Redis.GetDatabase();
                
                // Get all webhooks for this connection
                var connectionKey = RedisKeys.WebhookConnection.ConnectionWebhooks(connectionId);
                var webhooks = await db.SetMembersAsync(connectionKey);
                
                var transaction = db.CreateTransaction();

                // Remove connection from all webhook sets without deleting other members.
                foreach (var webhook in webhooks)
                {
                    var webhookKey = RedisKeys.WebhookConnection.WebhookConnections(
                        GetUrlHash(webhook.ToString()));
                    _ = transaction.SetRemoveAsync(webhookKey, connectionId);
                }

                // Redis removes empty sets; always remove the connection-owned keys
                // even when the client unsubscribed from its final webhook already.
                _ = transaction.KeyDeleteAsync(connectionKey);
                var timestampKey = RedisKeys.WebhookConnection.ConnectionTimestamp(connectionId);
                _ = transaction.KeyDeleteAsync(timestampKey);

                await transaction.ExecuteAsync();
                
                Logger.LogDebug("Removed connection {ConnectionId} and its {Count} webhook subscriptions", 
                    connectionId, webhooks.Length);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error removing connection {ConnectionId}", connectionId);
            }
        }
        
        public async Task<int> GetWebhookConnectionCountAsync(string webhookUrl)
        {
            try
            {
                var db = Redis.GetDatabase();
                var webhookKey = RedisKeys.WebhookConnection.WebhookConnections(GetUrlHash(webhookUrl));
                var count = await db.SetLengthAsync(webhookKey);
                
                return (int)count;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error getting connection count for webhook {WebhookUrl}", webhookUrl);
                return 0;
            }
        }
        
    }
    
    /// <summary>
    /// In-memory fallback implementation of webhook connection tracking
    /// Used when Redis is not available
    /// </summary>
    public class InMemoryWebhookConnectionTracker : IWebhookConnectionTracker
    {
        // Both maps are guarded by a single lock. This is the low-throughput fallback
        // path (Redis unavailable), and one lock keeps the two maps consistent without
        // the nested per-set locking this class previously relied on.
        private readonly Dictionary<string, HashSet<string>> _connectionWebhooks = new();
        private readonly Dictionary<string, HashSet<string>> _webhookConnections = new();
        private readonly object _lock = new();
        private readonly ILogger<InMemoryWebhookConnectionTracker> _logger;

        public InMemoryWebhookConnectionTracker(ILogger<InMemoryWebhookConnectionTracker> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task AddWebhooksToConnectionAsync(string connectionId, IEnumerable<string> webhookUrls)
        {
            var count = 0;

            lock (_lock)
            {
                if (!_connectionWebhooks.TryGetValue(connectionId, out var connectionSet))
                {
                    connectionSet = new HashSet<string>();
                    _connectionWebhooks[connectionId] = connectionSet;
                }

                foreach (var webhookUrl in webhookUrls)
                {
                    connectionSet.Add(webhookUrl);

                    if (!_webhookConnections.TryGetValue(webhookUrl, out var webhookSet))
                    {
                        webhookSet = new HashSet<string>();
                        _webhookConnections[webhookUrl] = webhookSet;
                    }

                    webhookSet.Add(connectionId);
                    count++;
                }
            }

            _logger.LogDebug("Added {Count} webhooks to connection {ConnectionId} (in-memory)",
                count, connectionId);

            return Task.CompletedTask;
        }

        public Task RemoveWebhooksFromConnectionAsync(string connectionId, IEnumerable<string> webhookUrls)
        {
            lock (_lock)
            {
                if (_connectionWebhooks.TryGetValue(connectionId, out var connectionSet))
                {
                    foreach (var webhookUrl in webhookUrls)
                    {
                        connectionSet.Remove(webhookUrl);

                        if (_webhookConnections.TryGetValue(webhookUrl, out var webhookSet))
                        {
                            webhookSet.Remove(connectionId);
                        }
                    }
                }
            }

            return Task.CompletedTask;
        }

        public Task<HashSet<string>> GetConnectionWebhooksAsync(string connectionId)
        {
            lock (_lock)
            {
                return Task.FromResult(
                    _connectionWebhooks.TryGetValue(connectionId, out var webhooks)
                        ? new HashSet<string>(webhooks)
                        : new HashSet<string>());
            }
        }

        public Task<HashSet<string>> GetWebhookConnectionsAsync(string webhookUrl)
        {
            lock (_lock)
            {
                return Task.FromResult(
                    _webhookConnections.TryGetValue(webhookUrl, out var connections)
                        ? new HashSet<string>(connections)
                        : new HashSet<string>());
            }
        }

        public Task RemoveConnectionAsync(string connectionId)
        {
            lock (_lock)
            {
                if (_connectionWebhooks.Remove(connectionId, out var webhooks))
                {
                    foreach (var webhook in webhooks)
                    {
                        if (_webhookConnections.TryGetValue(webhook, out var connections))
                        {
                            connections.Remove(connectionId);
                        }
                    }

                    _logger.LogDebug("Removed connection {ConnectionId} with {Count} webhooks (in-memory)",
                        connectionId, webhooks.Count);
                }
            }

            return Task.CompletedTask;
        }

        public Task<int> GetWebhookConnectionCountAsync(string webhookUrl)
        {
            lock (_lock)
            {
                return Task.FromResult(
                    _webhookConnections.TryGetValue(webhookUrl, out var connections)
                        ? connections.Count
                        : 0);
            }
        }
    }
}
