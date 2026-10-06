using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Configuration.DTOs.VirtualKey;

namespace ConduitLLM.Admin.Interfaces;

/// <summary>
/// Service interface for managing virtual keys through the Admin API
/// </summary>
public interface IAdminVirtualKeyService
{
    /// <summary>
    /// Generates a new virtual key
    /// </summary>
    /// <param name="request">The key generation request</param>
    /// <returns>The generated key response with key string and info</returns>
    Task<CreateVirtualKeyResponseDto> GenerateVirtualKeyAsync(CreateVirtualKeyRequestDto request);

    /// <summary>
    /// Gets information about a specific virtual key
    /// </summary>
    /// <param name="id">The ID of the virtual key</param>
    /// <returns>The virtual key information, or null if not found</returns>
    Task<VirtualKeyDto?> GetVirtualKeyInfoAsync(int id);

    /// <summary>
    /// Gets a list of all virtual keys
    /// </summary>
    /// <param name="virtualKeyGroupId">Optional filter by virtual key group ID</param>
    /// <returns>List of virtual key information</returns>
    Task<List<VirtualKeyDto>> ListVirtualKeysAsync(int? virtualKeyGroupId = null);

    /// <summary>
    /// Updates an existing virtual key
    /// </summary>
    /// <param name="id">The ID of the virtual key to update</param>
    /// <param name="request">The update request with new properties</param>
    /// <returns>True if the update was successful, false otherwise</returns>
    Task<bool> UpdateVirtualKeyAsync(int id, UpdateVirtualKeyRequestDto request);

    /// <summary>
    /// Deletes a virtual key
    /// </summary>
    /// <param name="id">The ID of the virtual key to delete</param>
    /// <returns>True if the deletion was successful, false otherwise</returns>
    Task<bool> DeleteVirtualKeyAsync(int id);
    Task<bool> DeleteVirtualKeyAsync(int id, CancellationToken cancellationToken);


    /// <summary>
    /// Validates a virtual key
    /// </summary>
    /// <param name="key">The virtual key to validate</param>
    /// <param name="requestedModel">Optional model being requested</param>
    /// <returns>Validation result with information about the key</returns>
    Task<VirtualKeyValidationResult> ValidateVirtualKeyAsync(string key, string? requestedModel = null);




    /// <summary>
    /// Gets detailed information about a virtual key for validation purposes
    /// </summary>
    /// <param name="id">The ID of the virtual key</param>
    /// <returns>Virtual key validation information or null if not found</returns>
    Task<VirtualKeyValidationInfoDto?> GetValidationInfoAsync(int id);

    /// <summary>
    /// Performs maintenance tasks on all virtual keys
    /// </summary>
    /// <remarks>
    /// This includes disabling keys that have passed their expiration date.
    /// </remarks>
    /// <returns>A task representing the asynchronous operation</returns>
    Task PerformMaintenanceAsync();

    /// <summary>
    /// Previews what models and capabilities a virtual key would see when calling the discovery endpoint
    /// </summary>
    /// <param name="id">The ID of the virtual key</param>
    /// <param name="capability">Optional capability filter (e.g. "chat", "vision", "audio_transcription")</param>
    /// <returns>Discovery response as the virtual key would see it, or null if key not found</returns>
    Task<DiscoveryModelsResponse?> PreviewDiscoveryAsync(int id, string? capability = null);

    /// <summary>
    /// Gets the virtual key by ID
    /// </summary>
    /// <param name="id">The ID of the virtual key</param>
    /// <returns>The virtual key DTO or null if not found</returns>
    Task<VirtualKeyDto?> GetVirtualKeyByIdAsync(int id);

    /// <summary>
    /// Gets the virtual key group information for a key
    /// </summary>
    /// <param name="id">The ID of the virtual key</param>
    /// <returns>The virtual key group DTO or null if not found</returns>
    Task<VirtualKeyGroupDto?> GetKeyGroupAsync(int id);

    /// <summary>
    /// Gets usage information for a virtual key by its key value
    /// </summary>
    /// <param name="keyValue">The actual virtual key value (with prefix)</param>
    /// <returns>Usage information for the virtual key, or null if not found</returns>
    Task<VirtualKeyUsageDto?> GetUsageByKeyAsync(string keyValue);
}
