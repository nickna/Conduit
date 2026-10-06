using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using ConduitLLM.Configuration.Interfaces;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConduitLLM.Configuration.Services
{
    /// <summary>
    /// Service for handling critical billing system alerts and notifications
    /// </summary>
    public class BillingAlertingService : IBillingAlertingService
    {
        private readonly ILogger<BillingAlertingService> _logger;
        private readonly IBillingAuditService? _auditService;
        private static readonly object AlertCooldownLock = new();
        private static readonly Dictionary<AlertCooldownKey, DateTime> AlertCooldowns = new();
        private static readonly TimeSpan AlertCooldown = TimeSpan.FromMinutes(5);
        private static DateTime _nextCooldownPrune = DateTime.MinValue;
        private const int MaxTrackedAlerts = 1024;

        /// <summary>
        /// Initializes a new instance of the BillingAlertingService
        /// </summary>
        public BillingAlertingService(
            ILogger<BillingAlertingService> logger,
            IBillingAuditService? auditService = null)
        {
            _logger = logger;
            _auditService = auditService;
        }

        /// <inheritdoc />
        public async Task SendCriticalAlertAsync(
            string message,
            int? virtualKeyId = null,
            Dictionary<string, object?>? additionalContext = null)
        {
            try
            {
                var now = DateTime.UtcNow;
                var serializedContext = additionalContext != null
                    ? JsonSerializer.Serialize(
                        additionalContext,
                        ConfigurationJsonContext.Default.DictionaryStringObject)
                    : null;

                // Every failure must be durably recorded. Notification throttling below must not
                // suppress billing audit data used for revenue-loss reporting.
                if (_auditService != null)
                {
                    try
                    {
                        await _auditService.LogBillingEventAsync(new BillingAuditEvent
                        {
                            EventType = BillingAuditEventType.SpendUpdateFailed,
                            VirtualKeyId = virtualKeyId,
                            FailureReason = message,
                            Timestamp = now,
                            MetadataJson = serializedContext
                        });
                    }
                    catch (Exception ex)
                    {
                        // An audit-store outage must not prevent the outbound critical alert.
                        _logger.LogError(ex, "Failed to record critical billing alert in the audit log");
                    }
                }

                var alertKey = new AlertCooldownKey(message, virtualKeyId);
                var shouldNotify = TryBeginCooldown(alertKey, now);

                if (!shouldNotify)
                {
                    _logger.LogWarning("Alert suppressed due to cooldown: {Message}", message);
                    return;
                }

                // Log critical error as the currently configured outbound notification.
                _logger.LogCritical("BILLING SYSTEM CRITICAL ALERT: {Message} | VirtualKeyId: {VirtualKeyId} | Context: {Context}",
                    message, virtualKeyId, serializedContext ?? "N/A");

                // Additional notification mechanisms can be added here
                // For example, sending to external monitoring systems, PagerDuty, etc.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send critical billing alert");
            }
        }

        private static bool TryBeginCooldown(AlertCooldownKey alertKey, DateTime now)
        {
            lock (AlertCooldownLock)
            {
                if (now >= _nextCooldownPrune || AlertCooldowns.Count >= MaxTrackedAlerts)
                {
                    var cutoff = now - AlertCooldown;
                    foreach (var expiredKey in AlertCooldowns
                        .Where(entry => entry.Value <= cutoff)
                        .Select(entry => entry.Key)
                        .ToList())
                    {
                        AlertCooldowns.Remove(expiredKey);
                    }

                    _nextCooldownPrune = now + AlertCooldown;
                }

                if (AlertCooldowns.TryGetValue(alertKey, out var lastAlert) &&
                    now - lastAlert < AlertCooldown)
                {
                    return false;
                }

                if (!AlertCooldowns.ContainsKey(alertKey) && AlertCooldowns.Count >= MaxTrackedAlerts)
                {
                    var oldestKey = AlertCooldowns.MinBy(entry => entry.Value).Key;
                    AlertCooldowns.Remove(oldestKey);
                }

                AlertCooldowns[alertKey] = now;
                return true;
            }
        }

        private readonly record struct AlertCooldownKey(string Message, int? VirtualKeyId);

    }
}
