using Microsoft.AspNetCore.SignalR;
using ConduitLLM.Gateway.Hubs;
using ConduitLLM.Configuration.DTOs.SignalR;
using ConduitLLM.Core.Services;

namespace ConduitLLM.Gateway.Services
{
    /// <summary>
    /// Service for sending real-time virtual key management notifications via SignalR.
    /// </summary>
    public interface IVirtualKeyManagementNotificationService
    {
        /// <summary>
        /// Notifies about a virtual key creation.
        /// </summary>
        Task NotifyKeyCreatedAsync(VirtualKeyCreatedNotification notification);

        /// <summary>
        /// Notifies about a virtual key update.
        /// </summary>
        Task NotifyKeyUpdatedAsync(int virtualKeyId, VirtualKeyUpdatedNotification notification);

        /// <summary>
        /// Notifies about a virtual key deletion.
        /// </summary>
        Task NotifyKeyDeletedAsync(int virtualKeyId, VirtualKeyDeletedNotification notification);

        /// <summary>
        /// Notifies about a virtual key status change.
        /// </summary>
        Task NotifyKeyStatusChangedAsync(int virtualKeyId, VirtualKeyStatusChangedNotification notification);
    }

    /// <summary>
    /// Implementation of virtual key management notification service using SignalR.
    /// Inherits from SignalRNotificationServiceBase for standardized error handling.
    /// </summary>
    public class VirtualKeyManagementNotificationService
        : SignalRNotificationServiceBase<VirtualKeyManagementHub>,
          IVirtualKeyManagementNotificationService
    {
        public VirtualKeyManagementNotificationService(
            IHubContext<VirtualKeyManagementHub> hubContext,
            ILogger<VirtualKeyManagementNotificationService> logger)
            : base(hubContext, logger)
        {
        }

        public async Task NotifyKeyCreatedAsync(VirtualKeyCreatedNotification notification)
        {
            await SendToGroupAsync("admin", "VirtualKeyCreated", notification);

            Logger.LogInformation(
                "Sent VirtualKeyCreated notification for key {KeyName} (ID: {KeyId})",
                notification.KeyName,
                notification.KeyId);
        }

        public async Task NotifyKeyUpdatedAsync(int virtualKeyId, VirtualKeyUpdatedNotification notification)
        {
            await SendToGroupAsync(VirtualKeyManagementGroups.NonAdminOwner(virtualKeyId), "VirtualKeyUpdated", notification);
            await SendToGroupAsync("admin", "VirtualKeyUpdated", notification);

            Logger.LogInformation(
                "Sent VirtualKeyUpdated notification for key {KeyId}: {UpdatedProperties}",
                virtualKeyId,
                string.Join(", ", notification.UpdatedProperties));
        }

        public async Task NotifyKeyDeletedAsync(int virtualKeyId, VirtualKeyDeletedNotification notification)
        {
            await SendToGroupAsync(VirtualKeyManagementGroups.NonAdminOwner(virtualKeyId), "VirtualKeyDeleted", notification);
            await SendToGroupAsync("admin", "VirtualKeyDeleted", notification);

            Logger.LogInformation(
                "Sent VirtualKeyDeleted notification for key {KeyName} (ID: {KeyId})",
                notification.KeyName,
                virtualKeyId);
        }

        public async Task NotifyKeyStatusChangedAsync(int virtualKeyId, VirtualKeyStatusChangedNotification notification)
        {
            // Notify admin group if it's a critical status change
            if (notification.NewStatus == "disabled" || notification.NewStatus == "suspended")
            {
                await SendToGroupAsync(VirtualKeyManagementGroups.NonAdminOwner(virtualKeyId), "VirtualKeyStatusChanged", notification);
                await SendToGroupAsync("admin", "VirtualKeyStatusChanged", notification);
            }
            else
            {
                await SendToGroupAsync(VirtualKeyManagementGroups.Status(virtualKeyId), "VirtualKeyStatusChanged", notification);
            }

            Logger.LogInformation(
                "Sent VirtualKeyStatusChanged notification for key {KeyId}: {PreviousStatus} -> {NewStatus}",
                virtualKeyId,
                notification.PreviousStatus,
                notification.NewStatus);
        }
    }
}
