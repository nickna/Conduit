using ConduitLLM.Core.Extensions;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Gateway.DTOs;
using ConduitLLM.Persistence.Interfaces;

namespace ConduitLLM.Gateway.Endpoints
{
    /// <summary>
    /// Controller for handling file downloads with enhanced features.
    /// </summary>
    public class DownloadsEndpoints : GatewayEndpointHandlerBase
    {
        private readonly IFileRetrievalService _fileRetrievalService;
        private readonly IMediaRuntimeStore _mediaStore;

        /// <summary>
        /// Initializes the Downloads endpoint handler.
        /// </summary>
        public DownloadsEndpoints(
            IFileRetrievalService fileRetrievalService,
            ILogger<DownloadsEndpoints> logger,
            IHttpContextAccessor httpContextAccessor,
            IMediaRuntimeStore mediaStore)
            : base(null, httpContextAccessor, logger)
        {
            _fileRetrievalService = fileRetrievalService ?? throw new ArgumentNullException(nameof(fileRetrievalService));
            _mediaStore = mediaStore ?? throw new ArgumentNullException(nameof(mediaStore));
        }

        /// <summary>
        /// Downloads a file by its identifier with support for range requests.
        /// </summary>
        /// <param name="fileId">The file identifier (storage key or URL).</param>
        /// <param name="inline">Whether to display inline (true) or force download (false).</param>
        /// <returns>The file content.</returns>
        public async Task<IResult> DownloadFile(string fileId, bool inline = false)
        {
            var virtualKeyId = CurrentVirtualKeyId ?? 0;
            Logger.LogDebug("File download requested by Virtual Key {VirtualKeyId}: {FileId}, inline: {Inline}",
                virtualKeyId, LoggingSanitizer.S(fileId), inline);

            // Validate ownership
            if (!await ValidateFileOwnership(fileId, virtualKeyId))
            {
                return OpenAIError(404, "File not found", "not_found", "not_found_error");
            }

            var result = await _fileRetrievalService.RetrieveFileAsync(fileId);
            if (result == null)
            {
                return OpenAIError(404, "File not found", "not_found", "not_found_error");
            }

            // Set appropriate headers. The ASP.NET file result owns the stream after
            // this method returns and disposes it after the response is written.
            if (!inline && !string.IsNullOrEmpty(result.Metadata.FileName))
            {
                Response.Headers["Content-Disposition"] = $"attachment; filename=\"{result.Metadata.FileName}\"";
            }

            // Set cache headers
            if (!string.IsNullOrEmpty(result.Metadata.ETag))
            {
                Response.Headers["ETag"] = result.Metadata.ETag;
                Response.Headers["Cache-Control"] = "private, max-age=3600";
            }

            // Do not dispose result before the framework has copied ContentStream.
            return File(
                result.ContentStream,
                result.Metadata.ContentType,
                result.Metadata.FileName,
                enableRangeProcessing: result.Metadata.SupportsRangeRequests);
        }

        /// <summary>
        /// Gets metadata information about a file.
        /// </summary>
        /// <param name="fileId">The file identifier.</param>
        /// <returns>File metadata.</returns>
        public async Task<IResult> GetFileMetadata(string fileId)
        {
            // Validate ownership
            var virtualKeyId = CurrentVirtualKeyId ?? 0;
            if (!await ValidateFileOwnership(fileId, virtualKeyId))
            {
                return OpenAIError(404, "File not found", "not_found", "not_found_error");
            }

            var metadata = await _fileRetrievalService.GetFileMetadataAsync(fileId);
            if (metadata == null)
            {
                return OpenAIError(404, "File not found", "not_found", "not_found_error");
            }

            return Ok(new FileMetadataResponse(
                metadata.FileName,
                metadata.ContentType,
                metadata.SizeBytes,
                metadata.CreatedAt,
                metadata.ModifiedAt,
                metadata.StorageProvider,
                metadata.ETag,
                metadata.SupportsRangeRequests,
                metadata.AdditionalMetadata));
        }

        /// <summary>
        /// Generates a temporary download URL for a file.
        /// </summary>
        /// <param name="request">The URL generation request.</param>
        /// <returns>A temporary download URL.</returns>
        public async Task<IResult> GenerateDownloadUrl(GenerateUrlRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.FileId))
            {
                return OpenAIError(400, "File ID is required", "invalid_request", "invalid_request_error");
            }

            var virtualKeyId = CurrentVirtualKeyId ?? 0;
            Logger.LogInformation("Download URL generation requested by Virtual Key {VirtualKeyId} for {FileId}, expiration: {ExpirationMinutes}m",
                virtualKeyId, LoggingSanitizer.S(request.FileId), request.ExpirationMinutes ?? 60);

            // Validate ownership
            if (!await ValidateFileOwnership(request.FileId, virtualKeyId))
            {
                return OpenAIError(404, "File not found", "not_found", "not_found_error");
            }

            var expirationMinutes = request.ExpirationMinutes ?? 60; // Default 1 hour
            if (expirationMinutes < 1 || expirationMinutes > 10080) // Max 1 week
            {
                return OpenAIError(400, "Expiration must be between 1 minute and 1 week", "invalid_request", "invalid_request_error");
            }

            var expiration = TimeSpan.FromMinutes(expirationMinutes);
            var url = await _fileRetrievalService.GetDownloadUrlAsync(request.FileId, expiration);

            if (string.IsNullOrEmpty(url))
            {
                return OpenAIError(404, "File not found or URL generation failed", "not_found", "not_found_error");
            }

            return Ok(new DownloadUrlResponse(
                url,
                DateTime.UtcNow.Add(expiration),
                expirationMinutes));
        }

        /// <summary>
        /// Checks if a file exists.
        /// </summary>
        /// <param name="fileId">The file identifier.</param>
        /// <returns>200 OK if exists, 404 if not.</returns>
        public async Task<IResult> CheckFileExists(string fileId)
        {
            // Validate ownership
            var virtualKeyId = CurrentVirtualKeyId ?? 0;
            if (!await ValidateFileOwnership(fileId, virtualKeyId))
            {
                return NotFound();
            }

            var exists = await _fileRetrievalService.FileExistsAsync(fileId);
            if (!exists)
            {
                return NotFound();
            }

            var metadata = await _fileRetrievalService.GetFileMetadataAsync(fileId);
            if (metadata != null)
            {
                Response.Headers["Content-Type"] = metadata.ContentType;
                Response.Headers["Content-Length"] = metadata.SizeBytes.ToString();
                if (!string.IsNullOrEmpty(metadata.ETag))
                {
                    Response.Headers["ETag"] = metadata.ETag;
                }
            }

            return Ok();
        }

        /// <summary>
        /// Validates that the file belongs to the requesting Virtual Key.
        /// </summary>
        /// <param name="fileId">The file identifier (storage key or URL).</param>
        /// <param name="virtualKeyId">The Virtual Key ID to validate against.</param>
        /// <returns>True if the file belongs to the Virtual Key, false otherwise.</returns>
        private async Task<bool> ValidateFileOwnership(string fileId, int virtualKeyId)
        {
            if (virtualKeyId <= 0)
            {
                Logger.LogWarning("Invalid Virtual Key ID: {VirtualKeyId}", virtualKeyId);
                return false;
            }

            // If it's a URL, we can't validate ownership
            if (fileId.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                fileId.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning("URL-based file access attempted by Virtual Key {VirtualKeyId}: {FileId}",
                    virtualKeyId, fileId);
                return false;
            }

            // Check if the file exists in our media records
            var mediaRecord = await _mediaStore.GetByStorageKeyAsync(fileId);

            if (mediaRecord == null)
            {
                Logger.LogWarning("Media record not found for storage key: {StorageKey}", fileId);
                return false;
            }

            if (mediaRecord.VirtualKeyId != virtualKeyId)
            {
                Logger.LogWarning("Virtual Key {RequestingKeyId} attempted to access file belonging to Virtual Key {OwnerKeyId}",
                    virtualKeyId, mediaRecord.VirtualKeyId);
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Request to generate a temporary download URL.
    /// </summary>
    public class GenerateUrlRequest
    {
        /// <summary>
        /// The file identifier.
        /// </summary>
        public required string FileId { get; set; }

        /// <summary>
        /// How many minutes the URL should be valid (1-10080).
        /// </summary>
        public int? ExpirationMinutes { get; set; }
    }
}
