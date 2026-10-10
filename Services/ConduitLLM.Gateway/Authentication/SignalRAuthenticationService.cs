using Microsoft.AspNetCore.SignalR;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Configuration.Entities;

namespace ConduitLLM.Gateway.Authentication
{
    /// <summary>
    /// Standardized authentication service for SignalR hubs.
    /// Provides consistent authentication and authorization methods across all hubs.
    /// </summary>
    public interface ISignalRAuthenticationService
    {
        /// <summary>
        /// Validates and retrieves virtual key information from the hub context.
        /// </summary>
        /// <param name="context">The hub caller context</param>
        /// <returns>The virtual key entity if valid, null otherwise</returns>
        Task<VirtualKey?> GetAuthenticatedVirtualKeyAsync(HubCallerContext context);
        
        /// <summary>
        /// Checks if the authenticated virtual key has admin privileges.
        /// </summary>
        /// <param name="context">The hub caller context</param>
        /// <returns>True if the virtual key has admin privileges</returns>
        Task<bool> IsAdminAsync(HubCallerContext context);
        
        /// <summary>
        /// Validates if the authenticated virtual key can access a specific resource.
        /// </summary>
        /// <param name="context">The hub caller context</param>
        /// <param name="resourceType">The type of resource</param>
        /// <param name="resourceId">The resource identifier</param>
        /// <returns>True if access is allowed</returns>
        Task<bool> CanAccessResourceAsync(HubCallerContext context, string resourceType, string resourceId);
        
        /// <summary>
        /// Gets the virtual key ID from the context.
        /// </summary>
        /// <param name="context">The hub caller context</param>
        /// <returns>The virtual key ID if authenticated, null otherwise</returns>
        int? GetVirtualKeyId(HubCallerContext context);
        
        /// <summary>
        /// Gets the virtual key name from the context.
        /// </summary>
        /// <param name="context">The hub caller context</param>
        /// <returns>The virtual key name if authenticated, "Unknown" otherwise</returns>
        string GetVirtualKeyName(HubCallerContext context);
    }
    
    /// <summary>
    /// Implementation of the SignalR authentication service.
    /// </summary>
    public class SignalRAuthenticationService : ISignalRAuthenticationService
    {
        private readonly IVirtualKeyRuntimeService _virtualKeyService;
        private readonly IAsyncTaskService _taskService;
        private readonly ILogger<SignalRAuthenticationService> _logger;
        
        public SignalRAuthenticationService(
            IVirtualKeyRuntimeService virtualKeyService,
            IAsyncTaskService taskService,
            ILogger<SignalRAuthenticationService> logger)
        {
            _virtualKeyService = virtualKeyService ?? throw new ArgumentNullException(nameof(virtualKeyService));
            _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }
        
        public async Task<VirtualKey?> GetAuthenticatedVirtualKeyAsync(HubCallerContext context)
        {
            var virtualKeyId = GetVirtualKeyId(context);
            if (!virtualKeyId.HasValue)
            {
                _logger.LogDebug("No virtual key ID found in context");
                return null;
            }
            
            try
            {
                var virtualKey = await _virtualKeyService.GetVirtualKeyInfoForValidationAsync(virtualKeyId.Value);
                if (virtualKey == null || !virtualKey.IsEnabled)
                {
                    _logger.LogWarning("Virtual key {KeyId} not found or disabled", virtualKeyId.Value);
                    return null;
                }
                
                return virtualKey;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving virtual key {KeyId}", virtualKeyId.Value);
                return null;
            }
        }
        
        public async Task<bool> IsAdminAsync(HubCallerContext context)
        {
            // Check if this is a master key by looking for a special claim or metadata
            // For now, we'll consider a key as admin if it has a specific metadata tag
            var virtualKey = await GetAuthenticatedVirtualKeyAsync(context);
            if (virtualKey?.Metadata != null)
            {
                try
                {
                    using var metadata = System.Text.Json.JsonDocument.Parse(virtualKey.Metadata);
                    if (metadata.RootElement.TryGetProperty("isAdmin", out var isAdmin))
                    {
                        return isAdmin.ValueKind == System.Text.Json.JsonValueKind.True ||
                               (isAdmin.ValueKind == System.Text.Json.JsonValueKind.String &&
                                string.Equals(isAdmin.GetString(), "true", StringComparison.OrdinalIgnoreCase));
                    }
                }
                catch
                {
                    // If metadata is not valid JSON, ignore
                }
            }
            
            return false;
        }
        
        public async Task<bool> CanAccessResourceAsync(HubCallerContext context, string resourceType, string resourceId)
        {
            var virtualKeyId = GetVirtualKeyId(context);
            if (!virtualKeyId.HasValue)
            {
                _logger.LogWarning("Cannot verify resource access without virtual key ID");
                return false;
            }
            
            // Check for admin access first
            if (await IsAdminAsync(context))
            {
                _logger.LogDebug("Admin virtual key {KeyId} granted access to {ResourceType}:{ResourceId}", 
                    virtualKeyId.Value, resourceType, resourceId);
                return true;
            }
            
            // Resource-specific access checks
            switch (resourceType.ToLowerInvariant())
            {
                case "task":
                    return await CanAccessTaskAsync(virtualKeyId.Value, resourceId);
                    
                case "virtualkey":
                    // Virtual keys can only access their own data
                    return int.TryParse(resourceId, out var keyId) && keyId == virtualKeyId.Value;
                    
                case "provider":
                    // All authenticated virtual keys can access provider information
                    return true;
                    
                default:
                    _logger.LogWarning("Unknown resource type {ResourceType} for access check", resourceType);
                    return false;
            }
        }
        
        public int? GetVirtualKeyId(HubCallerContext context)
        {
            // Try from Items first (set by hub filter)
            if (context.Items.TryGetValue("VirtualKeyId", out var itemValue))
            {
                if (itemValue is int itemId)
                    return itemId;
                
                if (itemValue is long longValue)
                    return (int)longValue;
                    
                if (itemValue is string stringValue && int.TryParse(stringValue, out var parsedValue))
                    return parsedValue;
            }
            
            // Try from User claims (set by authentication handler)
            var claim = context.User?.FindFirst("VirtualKeyId");
            if (claim != null && int.TryParse(claim.Value, out var claimId))
            {
                return claimId;
            }
            
            return null;
        }
        
        public string GetVirtualKeyName(HubCallerContext context)
        {
            // Try from Items first (set by hub filter)
            if (context.Items.TryGetValue("VirtualKeyName", out var itemValue) && itemValue is string itemName)
            {
                return itemName;
            }
            
            // Try from User claims (set by authentication handler)
            return context.User?.Identity?.Name ?? "Unknown";
        }
        
        private async Task<bool> CanAccessTaskAsync(int virtualKeyId, string taskId)
        {
            try
            {
                var taskStatus = await _taskService.GetTaskStatusAsync(taskId);
                if (taskStatus == null)
                {
                    _logger.LogWarning("Task {TaskId} not found", taskId);
                    return false;
                }
                
                // Check if the task metadata contains the virtual key ID
                int? taskVirtualKeyId = null;
                
                // Now that AsyncTaskStatus uses TaskMetadata?, we just need to check if it exists
                if (taskStatus.Metadata != null)
                {
                    taskVirtualKeyId = taskStatus.Metadata.VirtualKeyId;
                    _logger.LogDebug("Task {TaskId} has metadata with VirtualKeyId: {VirtualKeyId}", 
                        taskId, taskVirtualKeyId);
                }
                
                if (taskVirtualKeyId.HasValue)
                {
                    if (taskVirtualKeyId.Value == virtualKeyId)
                    {
                        _logger.LogDebug("Virtual Key {VirtualKeyId} has access to task {TaskId}", 
                            virtualKeyId, taskId);
                        return true;
                    }
                    else
                    {
                        _logger.LogWarning("Virtual Key {VirtualKeyId} does not have access to task {TaskId} owned by Virtual Key {OwnerKeyId}", 
                            virtualKeyId, taskId, taskVirtualKeyId);
                        return false;
                    }
                }
                
                _logger.LogWarning("Task {TaskId} has no virtual key metadata or metadata is in unexpected format. Metadata type: {MetadataType}", 
                    taskId, taskStatus.Metadata?.GetType().FullName ?? "null");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking task access for {TaskId}", taskId);
                return false;
            }
        }
    }
}
