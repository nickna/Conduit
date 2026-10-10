using ConduitLLM.Core.Extensions;
using ConduitLLM.Admin.Extensions;
using ConduitLLM.Admin.Endpoints;
using ConduitLLM.Admin.Interfaces;
using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Core.Events;
using ConduitLLM.Core.Services;

using ConduitLLM.Configuration.Messaging;

using ConduitLLM.Configuration.Interfaces;
namespace ConduitLLM.Admin.Services
{
    /// <summary>
    /// Service for managing global settings through the Admin API
    /// </summary>
    public class AdminGlobalSettingService : EventPublishingServiceBase, IAdminGlobalSettingService
    {
        private readonly IGlobalSettingRepository _globalSettingRepository;
        private readonly ILogger<AdminGlobalSettingService> _logger;

        /// <summary>
        /// Initializes a new instance of the AdminGlobalSettingService
        /// </summary>
        /// <param name="globalSettingRepository">The global setting repository</param>
        /// <param name="eventBus">Optional event bus (null if not configured)</param>
        /// <param name="logger">The logger</param>
        public AdminGlobalSettingService(
            IGlobalSettingRepository globalSettingRepository,
            ILogger<AdminGlobalSettingService> logger,
            IEventBus? eventBus = null)
            : base(eventBus, logger)
        {
            _globalSettingRepository = globalSettingRepository ?? throw new ArgumentNullException(nameof(globalSettingRepository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Log event publishing configuration status
            LogEventPublishingConfiguration(nameof(AdminGlobalSettingService));
        }

        /// <inheritdoc />
        public async Task<IEnumerable<GlobalSettingDto>> GetAllSettingsAsync()
        {
            _logger.LogDebug("Getting all global settings");

            var settings = await _globalSettingRepository.ListAsync();
            return settings
                .Where(setting => !setting.Key.Equals(
                    GlobalSettingDefinitionRegistry.ProtectedWebAdminKey,
                    StringComparison.Ordinal))
                .Select(s => s.ToDto())
                .ToList();
        }

        /// <inheritdoc />
        public async Task<GlobalSettingDto?> GetSettingByIdAsync(int id)
        {
            _logger.LogDebug("Getting global setting with ID: {Id}", id);

            var setting = await _globalSettingRepository.GetByIdAsync(id);
            return setting?.ToDto();
        }

        /// <inheritdoc />
        public async Task<GlobalSettingDto?> GetSettingByKeyAsync(string key)
        {
            _logger.LogDebug("Getting global setting with key: {Key}", LoggingSanitizer.S(key));

            var setting = await _globalSettingRepository.GetByKeyAsync(key);
            return setting?.ToDto();
        }

        /// <inheritdoc />
        public async Task<GlobalSettingDto> CreateSettingAsync(CreateGlobalSettingDto setting)
        {
            _logger.LogDebug("Creating new global setting with key: {Key}", LoggingSanitizer.S(setting.Key));

            if (setting.Key.Equals(
                GlobalSettingDefinitionRegistry.ProtectedWebAdminKey,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "WebAdmin_VirtualKey can only be managed through the explicit by-key bootstrap API.");
            }

            // Check if a setting with the same key already exists
            var existingSetting = await _globalSettingRepository.GetByKeyAsync(setting.Key);
            if (existingSetting != null)
            {
                throw new InvalidOperationException($"A global setting with key '{setting.Key}' already exists");
            }

            await ValidateValueAsync(setting.Key, setting.Value);

            // Convert to entity
            var entity = setting.ToEntity();

            // Save to database
            var id = await _globalSettingRepository.CreateAsync(entity);

            // Get the created setting
            var createdSetting = ConduitLLM.Core.Utilities.ReadBackGuard.RequireCreated(
                await _globalSettingRepository.GetByIdAsync(id),
                "global setting",
                id);

            // Publish GlobalSettingChanged event for cache synchronization
            await PublishEventAsync(
                new GlobalSettingChanged
                {
                    SettingId = createdSetting.Id,
                    SettingKey = createdSetting.Key,
                    ChangeType = "Created",
                    ChangedProperties = new[] { "Created" },
                    CorrelationId = Guid.NewGuid().ToString()
                },
                $"create global setting {createdSetting.Id}",
                new { SettingKey = createdSetting.Key });

            return createdSetting.ToDto();
        }

        /// <inheritdoc />
        public async Task<bool> UpdateSettingAsync(int id, UpdateGlobalSettingDto setting)
        {
            _logger.LogDebug("Updating global setting with ID: {Id}", id);

            // Get the existing setting
            var existingSetting = await _globalSettingRepository.GetByIdAsync(id);
            if (existingSetting == null)
            {
                _logger.LogWarning("Global setting with ID {Id} not found", id);
                return false;
            }

            if (existingSetting.Key.Equals(
                GlobalSettingDefinitionRegistry.ProtectedWebAdminKey,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "WebAdmin_VirtualKey can only be managed through the explicit by-key bootstrap API.");
            }

            var valueDefined = setting.IsDefined(nameof(setting.Value));
            var descriptionDefined = setting.IsDefined(nameof(setting.Description));
            if (valueDefined && setting.Value is null)
            {
                throw new InvalidOperationException("Global setting value cannot be null.");
            }
            if (valueDefined)
            {
                await ValidateValueAsync(existingSetting.Key, setting.Value!);
            }

            // Track changed properties for event publishing
            var changedProperties = new List<string>();
            var originalKey = existingSetting.Key;

            // Check what properties will change
            if (valueDefined && existingSetting.Value != setting.Value)
            {
                changedProperties.Add(nameof(existingSetting.Value));
            }

            if (descriptionDefined && existingSetting.Description != setting.Description)
            {
                changedProperties.Add(nameof(existingSetting.Description));
            }

            // Only proceed if there are actual changes
            if (!changedProperties.Any())
            {
                _logger.LogDebug("No changes detected for global setting {Id} - skipping update", id);
                return true;
            }

            // Update the entity
            if (valueDefined)
                existingSetting.Value = setting.Value!;
            if (descriptionDefined)
                existingSetting.Description = setting.Description;
            existingSetting.UpdatedAt = DateTime.UtcNow;

            // Save changes
            var result = await _globalSettingRepository.UpdateAsync(existingSetting);

            if (result)
            {
                // Publish GlobalSettingChanged event for cache invalidation
                await PublishEventAsync(
                    new GlobalSettingChanged
                    {
                        SettingId = existingSetting.Id,
                        SettingKey = originalKey,
                        ChangeType = "Updated",
                        ChangedProperties = changedProperties.ToArray(),
                        CorrelationId = Guid.NewGuid().ToString()
                    },
                    $"update global setting {id}",
                    new { SettingKey = originalKey, ChangedProperties = string.Join(", ", changedProperties) });
            }

            return result;
        }

        public Task<bool> UpdateSettingAsync(UpdateGlobalSettingDto setting) =>
            UpdateSettingAsync(setting.Id, setting);

        /// <inheritdoc />
        public async Task<bool> UpdateSettingByKeyAsync(UpdateGlobalSettingByKeyDto setting)
        {
            _logger.LogDebug("Updating global setting with key: {Key}", LoggingSanitizer.S(setting.Key));

            // Get existing setting to determine if this is an update or create
            var existingSetting = await _globalSettingRepository.GetByKeyAsync(setting.Key);
            var isCreate = existingSetting == null;

            await ValidateValueAsync(setting.Key, setting.Value);

            // Upsert the setting
            var result = await _globalSettingRepository.UpsertAsync(setting.Key, setting.Value, setting.Description);

            if (result)
            {
                // Get the setting after upsert to get the ID
                var updatedSetting = await _globalSettingRepository.GetByKeyAsync(setting.Key);
                if (updatedSetting != null)
                {
                    // Publish GlobalSettingChanged event
                    await PublishEventAsync(
                        new GlobalSettingChanged
                        {
                            SettingId = updatedSetting.Id,
                            SettingKey = setting.Key,
                            ChangeType = isCreate ? "Created" : "Updated",
                            ChangedProperties = isCreate ? new[] { "Created" } : new[] { "Value", "Description" },
                            CorrelationId = Guid.NewGuid().ToString()
                        },
                        $"{(isCreate ? "create" : "update")} global setting by key {setting.Key}",
                        new { SettingKey = setting.Key, ChangeType = isCreate ? "Created" : "Updated" });
                }
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteSettingAsync(int id)
        {
            _logger.LogDebug("Deleting global setting with ID: {Id}", id);

            // Get the setting before deleting for event publishing
            var setting = await _globalSettingRepository.GetByIdAsync(id);
            if (setting == null)
            {
                _logger.LogWarning("Global setting with ID {Id} not found", id);
                return false;
            }

            if (setting.Key.Equals(
                GlobalSettingDefinitionRegistry.ProtectedWebAdminKey,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "WebAdmin_VirtualKey can only be managed through the explicit by-key bootstrap API.");
            }

            var result = await _globalSettingRepository.DeleteAsync(id);

            if (result)
            {
                // Publish GlobalSettingChanged event for cache invalidation
                await PublishEventAsync(
                    new GlobalSettingChanged
                    {
                        SettingId = setting.Id,
                        SettingKey = setting.Key,
                        ChangeType = "Deleted",
                        ChangedProperties = new[] { "Deleted" },
                        CorrelationId = Guid.NewGuid().ToString()
                    },
                    $"delete global setting {id}",
                    new { SettingKey = setting.Key });
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<bool> DeleteSettingByKeyAsync(string key)
        {
            _logger.LogDebug("Deleting global setting with key: {Key}", LoggingSanitizer.S(key));

            if (key.Equals(
                GlobalSettingDefinitionRegistry.ProtectedWebAdminKey,
                StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "WebAdmin_VirtualKey cannot be deleted through global settings APIs.");
            }

            // Get the setting before deleting for event publishing
            var setting = await _globalSettingRepository.GetByKeyAsync(key);
            if (setting == null)
            {
                _logger.LogWarning("Global setting with key {Key} not found", LoggingSanitizer.S(key));
                return false;
            }

            var result = await _globalSettingRepository.DeleteByKeyAsync(key);

            if (result)
            {
                // Publish GlobalSettingChanged event for cache invalidation
                await PublishEventAsync(
                    new GlobalSettingChanged
                    {
                        SettingId = setting.Id,
                        SettingKey = setting.Key,
                        ChangeType = "Deleted",
                        ChangedProperties = new[] { "Deleted" },
                        CorrelationId = Guid.NewGuid().ToString()
                    },
                    $"delete global setting by key {key}",
                    new { SettingKey = key });
            }

            return result;
        }

        private async Task ValidateValueAsync(string key, string value)
        {
            GlobalSettingDefinitionRegistry.ValidateValue(key, value);

            if (key is not ("Agentic.MinIterations" or "Agentic.MaxIterations"))
            {
                return;
            }

            var otherKey = key == "Agentic.MinIterations"
                ? "Agentic.MaxIterations"
                : "Agentic.MinIterations";
            var other = await _globalSettingRepository.GetByKeyAsync(otherKey);
            var otherDefault = otherKey == "Agentic.MaxIterations" ? 5 : 1;
            var otherValue = int.TryParse(other?.Value, out var parsedOther)
                ? parsedOther
                : otherDefault;
            var candidate = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            var minimum = key == "Agentic.MinIterations" ? candidate : otherValue;
            var maximum = key == "Agentic.MaxIterations" ? candidate : otherValue;

            if (minimum > maximum)
            {
                throw new ArgumentException(
                    "Agentic.MinIterations cannot be greater than Agentic.MaxIterations.",
                    nameof(value));
            }
        }
    }
}
