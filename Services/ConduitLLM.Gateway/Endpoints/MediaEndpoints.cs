using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Persistence.Interfaces;
using Microsoft.Net.Http.Headers;
using ConduitLLM.Gateway.DTOs;

namespace ConduitLLM.Gateway.Endpoints
{
    /// <summary>
    /// Handles media file upload, retrieval and serving.
    /// </summary>
    public class MediaEndpoints : GatewayEndpointHandlerBase
    {
        private readonly IMediaStorageService _storageService;
        private readonly IMediaRuntimeStore _mediaStore;

        public MediaEndpoints(
            IMediaStorageService storageService,
            IMediaRuntimeStore mediaStore,
            IHttpContextAccessor httpContextAccessor,
            ILogger<MediaEndpoints> logger)
            : base(null, httpContextAccessor, logger)
        {
            _storageService = storageService;
            _mediaStore = mediaStore;
        }

        /// <summary>
        /// Uploads a media file and returns the storage URL.
        /// </summary>
        /// <param name="file">The file to upload.</param>
        /// <param name="mediaType">Optional media type (image/video/audio).</param>
        /// <returns>The storage result with URL.</returns>
        public async Task<IResult> UploadMedia(
            IFormFile file,
            string? mediaType = null)
        {
            // Validate file
            if (file == null || file.Length == 0)
            {
                return OpenAIError(400, "No file provided or file is empty", "invalid_request");
            }

            // Validate file extension
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var allowedImageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg" };
            var allowedVideoExtensions = new[] { ".mp4", ".webm", ".mov", ".avi", ".mkv", ".flv", ".wmv", ".m4v" };
            var allowedAudioExtensions = new[] { ".mp3", ".wav", ".ogg", ".m4a", ".flac", ".aac" };

            // Determine media type from extension if not provided
            MediaType determinedMediaType;
            if (!string.IsNullOrEmpty(mediaType))
            {
                if (!Enum.TryParse<MediaType>(mediaType, true, out determinedMediaType))
                {
                    return OpenAIError(400, "Invalid media type. Must be Image, Video, or Audio", "invalid_parameter");
                }
            }
            else if (allowedImageExtensions.Contains(extension))
            {
                determinedMediaType = MediaType.Image;
            }
            else if (allowedVideoExtensions.Contains(extension))
            {
                determinedMediaType = MediaType.Video;
            }
            else if (allowedAudioExtensions.Contains(extension))
            {
                determinedMediaType = MediaType.Audio;
            }
            else
            {
                return OpenAIError(400, $"Unsupported file extension: {extension}", "invalid_parameter");
            }

            // Validate file size based on type
            var maxSizeBytes = determinedMediaType switch
            {
                MediaType.Image => 104857600L, // 100MB for images
                MediaType.Video => 524288000L, // 500MB for videos
                MediaType.Audio => 209715200L, // 200MB for audio
                _ => 104857600L // Default 100MB
            };

            if (file.Length > maxSizeBytes)
            {
                var maxSizeMB = maxSizeBytes / (1024 * 1024);
                return OpenAIError(400, $"File size exceeds maximum allowed size of {maxSizeMB}MB for {determinedMediaType}", "invalid_request");
            }

            // Create metadata
            var metadata = new MediaMetadata
            {
                MediaType = determinedMediaType,
                ContentType = file.ContentType
                    ?? ConduitLLM.Core.Utilities.MediaContentTypes.GetContentType(extension)
                    ?? "application/octet-stream",
                FileName = file.FileName,
                CreatedBy = CurrentVirtualKeyId?.ToString()
            };

            // Upload file using storage service
            using var stream = file.OpenReadStream();
            var result = await _storageService.StoreAsync(stream, metadata);

            Logger.LogInformation("Media uploaded successfully. Type: {MediaType}, Size: {Size} bytes, Key: {StorageKey}",
                determinedMediaType, file.Length, result.StorageKey);

            // Return result with full URL
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            return Ok(new MediaUploadResponse(
                true,
                result.StorageKey,
                result.Url,
                $"{baseUrl}/v1/conduit/media/{result.StorageKey}",
                metadata.ContentType,
                determinedMediaType.ToString(),
                file.FileName,
                file.Length));
        }

        /// <summary>
        /// Retrieves a media file by its storage key.
        /// </summary>
        /// <param name="storageKey">The unique storage key.</param>
        /// <returns>The media file.</returns>
        public async Task<IResult> GetMedia(string storageKey)
        {
            // Validate storage key
            if (string.IsNullOrWhiteSpace(storageKey))
            {
                return OpenAIError(400, "Invalid storage key", "invalid_parameter");
            }

            if (await IsTombstonedAsync(storageKey))
            {
                return NotFound();
            }

            // Get media info
            var mediaInfo = await _storageService.GetInfoAsync(storageKey);
            if (mediaInfo == null)
            {
                return NotFound();
            }

            // Check if this is a video and if range is requested
            if (mediaInfo.MediaType == MediaType.Video && Request.Headers.ContainsKey(HeaderNames.Range))
            {
                return await HandleVideoRangeRequest(storageKey, mediaInfo);
            }

            // Get media stream for non-video or non-range requests
            var stream = await _storageService.GetStreamAsync(storageKey);
            if (stream == null)
            {
                return NotFound();
            }

            // Set cache headers for performance
            Response.Headers["Cache-Control"] = "public, max-age=3600"; // 1 hour
            Response.Headers["ETag"] = $"\"{storageKey}\"";

            // Add CORS headers for video playback
            if (mediaInfo.MediaType == MediaType.Video)
            {
                Response.Headers["Accept-Ranges"] = "bytes";
                Response.Headers["Access-Control-Allow-Origin"] = "*";
                Response.Headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
                Response.Headers["Access-Control-Allow-Headers"] = "Range";
            }

            // Return file with proper content type
            return File(stream, mediaInfo.ContentType, enableRangeProcessing: true);
        }

        /// <summary>
        /// Gets metadata information about a media file.
        /// </summary>
        /// <param name="storageKey">The unique storage key.</param>
        /// <returns>Media metadata.</returns>
        public async Task<IResult> GetMediaInfo(string storageKey)
        {
            if (await IsTombstonedAsync(storageKey))
            {
                return NotFound();
            }

            var mediaInfo = await _storageService.GetInfoAsync(storageKey);
            if (mediaInfo == null)
            {
                return NotFound();
            }

            return Ok(mediaInfo);
        }

        /// <summary>
        /// Checks if a media file exists.
        /// </summary>
        /// <param name="storageKey">The unique storage key.</param>
        /// <returns>True if the media exists.</returns>
        public async Task<IResult> CheckMediaExists(string storageKey)
        {
            if (await IsTombstonedAsync(storageKey))
            {
                return NotFound();
            }

            var exists = await _storageService.ExistsAsync(storageKey);
            if (!exists)
            {
                return NotFound();
            }

            var mediaInfo = await _storageService.GetInfoAsync(storageKey);
            if (mediaInfo != null)
            {
                Response.Headers["Content-Type"] = mediaInfo.ContentType;
                Response.Headers["Content-Length"] = mediaInfo.SizeBytes.ToString();
            }

            return Ok();
        }

        private async Task<bool> IsTombstonedAsync(string storageKey)
        {
            var mediaRecord = await _mediaStore.GetByStorageKeyAsync(
                storageKey,
                includeDeleted: true);
            return mediaRecord?.DeletedAt != null;
        }

        /// <summary>
        /// Handles HTTP range requests for video streaming.
        /// </summary>
        private async Task<IResult> HandleVideoRangeRequest(string storageKey, MediaInfo mediaInfo)
        {
            var rangeHeader = Request.Headers[HeaderNames.Range].FirstOrDefault();
            if (string.IsNullOrEmpty(rangeHeader))
            {
                return OpenAIError(400, "Invalid range header", "invalid_request");
            }

            // Parse range header (e.g., "bytes=0-1023")
            var range = ParseRangeHeader(rangeHeader, mediaInfo.SizeBytes);
            if (range == null)
            {
                return StatusCode(416, "Requested Range Not Satisfiable"); // 416 Range Not Satisfiable
            }

            // Get video stream with range
            var rangedStream = await _storageService.GetVideoStreamAsync(
                storageKey,
                range.Value.Start,
                range.Value.End);

            if (rangedStream == null)
            {
                return NotFound();
            }

            // Set response headers for partial content
            Response.StatusCode = 206; // Partial Content
            Response.Headers["Accept-Ranges"] = "bytes";
            Response.Headers["Content-Range"] = $"bytes {rangedStream.RangeStart}-{rangedStream.RangeEnd}/{rangedStream.TotalSize}";
            Response.Headers["Content-Length"] = rangedStream.ContentLength.ToString();
            Response.Headers["Cache-Control"] = "public, max-age=3600";
            Response.Headers["ETag"] = $"\"{storageKey}\"";

            // CORS headers for video playback
            Response.Headers["Access-Control-Allow-Origin"] = "*";
            Response.Headers["Access-Control-Allow-Methods"] = "GET, HEAD, OPTIONS";
            Response.Headers["Access-Control-Allow-Headers"] = "Range";

            return File(rangedStream.Stream, rangedStream.ContentType);
        }

        /// <summary>
        /// Parses HTTP range header.
        /// </summary>
        private static (long Start, long End)? ParseRangeHeader(string rangeHeader, long totalSize)
        {
            try
            {
                // Remove "bytes=" prefix
                if (!rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var rangeValue = rangeHeader.Substring(6);
                var parts = rangeValue.Split('-');

                if (parts.Length != 2)
                {
                    return null;
                }

                long start = 0;
                long end = totalSize - 1;

                // Parse start
                if (!string.IsNullOrEmpty(parts[0]))
                {
                    if (!long.TryParse(parts[0], out start))
                    {
                        return null;
                    }
                }

                // Parse end
                if (!string.IsNullOrEmpty(parts[1]))
                {
                    if (!long.TryParse(parts[1], out end))
                    {
                        return null;
                    }
                }
                else if (!string.IsNullOrEmpty(parts[0]))
                {
                    // If no end specified, use a reasonable chunk size (1MB)
                    end = Math.Min(start + 1024 * 1024 - 1, totalSize - 1);
                }

                // Validate range
                if (start < 0 || start >= totalSize || end < start || end >= totalSize)
                {
                    return null;
                }

                return (start, end);
            }
            catch
            {
                return null;
            }
        }
    }
}
