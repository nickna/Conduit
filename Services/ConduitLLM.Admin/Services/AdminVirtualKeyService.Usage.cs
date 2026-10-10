using System.Diagnostics.CodeAnalysis;

using ConduitLLM.Admin.Extensions;
using ConduitLLM.Core.Extensions;
using ConduitLLM.Configuration.Constants;
using ConduitLLM.Configuration.DTOs.VirtualKey;
using ConduitLLM.Configuration.Entities;
using ConduitLLM.Configuration.Extensions;

using Microsoft.EntityFrameworkCore;

using VirtualKeyUtilities = ConduitLLM.Configuration.Utilities.VirtualKeyUtilities;

namespace ConduitLLM.Admin.Services
{
    /// <summary>
    /// Service for managing virtual keys through the Admin API - Usage and Maintenance functionality
    /// </summary>
    public partial class AdminVirtualKeyService
    {
        /// <inheritdoc />
        [UnconditionalSuppressMessage(
            "Trimming",
            "IL2026",
            Justification = "Admin EF queries are outside the supported NativeAOT data-plane contract (ADR 0006). Owner: database/runtime. Upstream: dotnet/efcore#29754. Remove when this query uses a fixed-shape native store or EF expression construction is trim-safe.")]
        public async Task PerformMaintenanceAsync()
        {
            _logger.LogInformation("Starting virtual key maintenance tasks");

            var now = DateTime.UtcNow;

            await using var context = await _dbContextFactory.CreateDbContextAsync();

            // Capture identifying fields of the keys we're about to disable so the per-key
            // audit log lines below have something to reference. Same predicate as the
            // UPDATE; modulo a tiny race with concurrent disables, the lists agree.
            var expiredKeys = await context.VirtualKeys
                .AsNoTracking()
                .Where(vk => vk.IsEnabled && vk.ExpiresAt != null && vk.ExpiresAt < now)
                .Select(vk => new { vk.Id, vk.KeyName })
                .ToListAsync();

            if (expiredKeys.Count == 0)
            {
                _logger.LogInformation("Virtual key maintenance completed. No expired keys to disable.");
                return;
            }

            _logger.LogInformation("Processing maintenance for {KeyCount} expired virtual keys", expiredKeys.Count);

            // Disable all matching keys in a single SQL UPDATE rather than N round-trips.
            var keysDisabled = await context.VirtualKeys
                .Where(vk => vk.IsEnabled && vk.ExpiresAt != null && vk.ExpiresAt < now)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(vk => vk.IsEnabled, false)
                    .SetProperty(vk => vk.UpdatedAt, now));

            foreach (var key in expiredKeys)
            {
                _logger.LogInformation("Disabled expired virtual key {KeyId} ({KeyName})",
                    key.Id, LoggingSanitizer.S(key.KeyName));
            }

            _logger.LogInformation("Virtual key maintenance completed. Keys disabled: {KeysDisabled}", keysDisabled);
        }

        /// <inheritdoc />
        public async Task<VirtualKeyDto?> GetVirtualKeyByIdAsync(int id)
        {
            var key = await _virtualKeyRepository.GetByIdAsync(id);
            if (key == null)
            {
                return null;
            }
            return VirtualKeyUtilities.MapToDto(key);
        }

        /// <inheritdoc />
        public async Task<VirtualKeyGroupDto?> GetKeyGroupAsync(int id)
        {
            var group = await _groupRepository.GetByKeyIdAsync(id);
            if (group == null)
            {
                return null;
            }

            return VirtualKeyGroupDto.FromEntity(group);
        }

        /// <inheritdoc />
        public async Task<VirtualKeyUsageDto?> GetUsageByKeyAsync(string keyValue)
        {
            if (string.IsNullOrEmpty(keyValue))
            {
                _logger.LogWarning("GetUsageByKeyAsync called with empty key value");
                return null;
            }

            if (!keyValue.StartsWith(VirtualKeyConstants.KeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("GetUsageByKeyAsync called with invalid key format (missing prefix)");
                return null;
            }

            // Hash the key for lookup
            var keyHash = VirtualKeyUtilities.HashKey(keyValue);

            // Get the virtual key by hash
            var virtualKey = await _virtualKeyRepository.GetByKeyHashAsync(keyHash);
            if (virtualKey == null)
            {
                _logger.LogInformation("Virtual key not found for hash lookup");
                return null;
            }

            // Get the group information
            var group = await _groupRepository.GetByKeyIdAsync(virtualKey.Id);
            if (group == null)
            {
                _logger.LogWarning("Virtual key group not found for key {KeyId}", virtualKey.Id);
                return null;
            }

            // Get spending history for this specific key
            var spendHistory = await _spendHistoryRepository.GetByVirtualKeyIdAsync(virtualKey.Id);
            var totalRequests = spendHistory.Count();
            // Note: VirtualKeySpendHistory doesn't track individual tokens, only amounts
            // We'll need to estimate based on spending or leave it as 0
            var totalTokens = 0L; // Token tracking would require different data structure
            var lastUsedAt = spendHistory.OrderByDescending(s => s.Timestamp).FirstOrDefault()?.Timestamp;

            return new VirtualKeyUsageDto
            {
                KeyId = virtualKey.Id,
                KeyName = virtualKey.KeyName,
                GroupId = group.Id,
                GroupName = group.GroupName,
                Balance = group.Balance,
                LifetimeCreditsAdded = group.LifetimeCreditsAdded,
                LifetimeSpent = group.LifetimeSpent,
                TotalRequests = totalRequests,
                TotalTokens = totalTokens,
                IsEnabled = virtualKey.IsEnabled,
                ExpiresAt = virtualKey.ExpiresAt,
                CreatedAt = virtualKey.CreatedAt,
                LastUsedAt = lastUsedAt,
                RateLimitRpm = virtualKey.RateLimitRpm,
                RateLimitRpd = virtualKey.RateLimitRpd,
                RateLimitTpm = virtualKey.RateLimitTpm,
                MaxParallelRequests = virtualKey.MaxParallelRequests,
                RateLimitPriority = virtualKey.RateLimitPriority,
                AllowedModels = VirtualKeyUtilities.ParseAllowedModels(virtualKey.AllowedModels)
            };
        }

    }
}
