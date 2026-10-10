using ConduitLLM.Configuration.DTOs.VirtualKey;
namespace ConduitLLM.Core.Interfaces
{
    /// <summary>
    /// Extended virtual key service interface for Core/Http layer with additional validation and DTO support
    /// </summary>
    public interface IVirtualKeyService : IVirtualKeyRuntimeService
    {
        /// <summary>
        /// Generates a new virtual key
        /// </summary>
        Task<CreateVirtualKeyResponseDto> GenerateVirtualKeyAsync(CreateVirtualKeyRequestDto request);

        /// <summary>
        /// Gets virtual key information by ID
        /// </summary>
        Task<VirtualKeyDto?> GetVirtualKeyInfoAsync(int id);

        /// <summary>
        /// Lists all virtual keys
        /// </summary>
        Task<List<VirtualKeyDto>> ListVirtualKeysAsync();

        /// <summary>
        /// Updates a virtual key
        /// </summary>
        Task<bool> UpdateVirtualKeyAsync(int id, UpdateVirtualKeyRequestDto request);

        /// <summary>
        /// Deletes a virtual key
        /// </summary>
        Task<bool> DeleteVirtualKeyAsync(int id);

        /// <summary>
        /// Resets the spend for a virtual key
        /// </summary>
        Task<bool> ResetSpendAsync(int id);

    }
}
