using System.Security.Cryptography;
using System.Text;

namespace ConduitLLM.Core.Constants
{
    /// <summary>
    /// Centralized constants for SignalR communication to ensure consistency
    /// between Gateway API hubs and WebAdmin clients
    /// </summary>
    public static class SignalRConstants
    {
        /// <summary>
        /// SignalR Hub endpoints
        /// </summary>
        public static class Hubs
        {
            public const string NavigationState = "/hubs/navigation-state";
            public const string ImageGeneration = "/hubs/image-generation";
            public const string VideoGeneration = "/hubs/video-generation";
            public const string SystemNotification = "/hubs/notifications";
            public const string Task = "/hubs/tasks";
        }

        /// <summary>
        /// Hub method names that clients can invoke
        /// </summary>
        public static class HubMethods
        {
            // Common subscription methods
            public const string SubscribeToTask = "SubscribeToTask";
            public const string UnsubscribeFromTask = "UnsubscribeFromTask";
            
            // Navigation state methods
            public const string Subscribe = "Subscribe";
            public const string Unsubscribe = "Unsubscribe";
        }

        /// <summary>
        /// Client method names that hubs can invoke
        /// </summary>
        public static class ClientMethods
        {
            // Task progress events (generic)
            public const string TaskProgress = "TaskProgress";
            public const string TaskCompleted = "TaskCompleted";
            public const string TaskFailed = "TaskFailed";
            public const string TaskStarted = "TaskStarted";
            
            // Video generation specific events (legacy - kept for compatibility)
            public const string VideoGenerationStarted = "VideoGenerationStarted";
            public const string VideoGenerationProgress = "VideoGenerationProgress";
            public const string VideoGenerationCompleted = "VideoGenerationCompleted";
            public const string VideoGenerationFailed = "VideoGenerationFailed";
            public const string VideoGenerationCancelled = "VideoGenerationCancelled";

            // Image generation specific events (legacy - kept for compatibility)
            public const string ImageGenerationStarted = "ImageGenerationStarted";
            public const string ImageGenerationProgress = "ImageGenerationProgress";
            public const string ImageGenerationCompleted = "ImageGenerationCompleted";
            public const string ImageGenerationFailed = "ImageGenerationFailed";
            public const string ImageGenerationCancelled = "ImageGenerationCancelled";
            
            // Navigation state events
            public const string NavigationStateUpdated = "NavigationStateUpdated";
            
            // System notification events
            public const string NotificationReceived = "NotificationReceived";
            public const string SystemAlert = "SystemAlert";
        }

        /// <summary>
        /// Group name patterns for SignalR groups
        /// </summary>
        public static class Groups
        {
            /// <summary>
            /// Get group name for image generation task
            /// </summary>
            public static string ImageTask(string taskId) => $"image-{taskId}";
            
            /// <summary>
            /// Get group name for video generation task
            /// </summary>
            public static string VideoTask(string taskId) => $"video-{taskId}";
            
            /// <summary>
            /// Get group name for generic task
            /// </summary>
            public static string Task(string taskId) => $"task-{taskId}";

            /// <summary>
            /// Get the owner-scoped destination group shared by webhook publishers and subscribers.
            /// </summary>
            public static string Webhook(int virtualKeyId, string webhookUrl)
            {
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(virtualKeyId);
                var uri = new Uri(webhookUrl, UriKind.Absolute);
                var destination = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
                return $"webhook-{virtualKeyId}-{destination}";
            }
            
            /// <summary>
            /// Group for all authenticated users
            /// </summary>
            public const string AuthenticatedUsers = "authenticated-users";
            
            /// <summary>
            /// Group for admin users
            /// </summary>
            public const string AdminUsers = "admin-users";
        }

        /// <summary>
        /// Connection metadata keys
        /// </summary>
        public static class ConnectionMetadata
        {
            public const string VirtualKeyId = "VirtualKeyId";
            public const string UserRole = "UserRole";
            public const string ClientType = "ClientType";
        }

        /// <summary>
        /// Event types for structured logging and monitoring
        /// </summary>
        public static class EventTypes
        {
            public const string HubConnected = "SignalR.Hub.Connected";
            public const string HubDisconnected = "SignalR.Hub.Disconnected";
            public const string SubscriptionAdded = "SignalR.Subscription.Added";
            public const string SubscriptionRemoved = "SignalR.Subscription.Removed";
            public const string MessageSent = "SignalR.Message.Sent";
            public const string MessageFailed = "SignalR.Message.Failed";
        }
    }
}
