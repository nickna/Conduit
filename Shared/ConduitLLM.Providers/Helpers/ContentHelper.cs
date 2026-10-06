using System.Text;
using System.Text.Json;

using ConduitLLM.Core.Models;

namespace ConduitLLM.Providers.Helpers
{
    /// <summary>
    /// Helper class for handling multimodal message content, providing utilities for
    /// working with text and image content parts in messages.
    /// </summary>
    public static class ContentHelper
    {
        /// <summary>
        /// Extracts multimodal content as a list of text content parts,
        /// filtering out non-text content like images.
        /// </summary>
        /// <param name="content">The message content (can be string or content parts)</param>
        /// <returns>List of string content parts</returns>
        public static List<string> ExtractMultimodalContent(object? content)
        {
            var textParts = new List<string>();

            if (content == null)
                return textParts;

            if (content is string textContent)
            {
                textParts.Add(textContent);
                return textParts;
            }

            // Handle JSON Element or list of content parts
            if (content is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == JsonValueKind.String)
                {
                    textParts.Add(jsonElement.GetString() ?? string.Empty);
                    return textParts;
                }

                if (jsonElement.ValueKind == JsonValueKind.Array)
                {
                    // Extract all text content parts
                    foreach (var element in jsonElement.EnumerateArray())
                    {
                        if (element.TryGetProperty("type", out var typeElement) &&
                            typeElement.GetString() == "text" &&
                            element.TryGetProperty("text", out var textElement))
                        {
                            textParts.Add(textElement.GetString() ?? string.Empty);
                        }
                    }
                    return textParts;
                }
            }

            // Handle ContentParts from direct API usage
            if (content is IEnumerable<object> contentList)
            {
                foreach (var part in contentList)
                {
                    if (part is TextContentPart textPart)
                    {
                        textParts.Add(textPart.Text);
                    }
                }

                if (textParts.Any())
                {
                    return textParts;
                }
            }

            // Fallback: Just add the string representation
            textParts.Add(content.ToString() ?? string.Empty);
            return textParts;
        }

        /// <summary>
        /// Converts message content (which could be a string or content parts) to a simple string.
        /// Useful for providers that don't support multimodal inputs.
        /// </summary>
        /// <param name="content">The message content (can be string or content parts)</param>
        /// <returns>String representation of the content, omitting non-text parts</returns>
        public static string GetContentAsString(object? content)
        {
            if (content == null)
                return string.Empty;

            if (content is string textContent)
                return textContent;

            // Handle JSON Element or list of content parts
            if (content is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == JsonValueKind.String)
                    return jsonElement.GetString() ?? string.Empty;

                if (jsonElement.ValueKind == JsonValueKind.Array)
                {
                    // Combine all text content parts
                    var sb = new StringBuilder();
                    foreach (var element in jsonElement.EnumerateArray())
                    {
                        if (element.TryGetProperty("type", out var typeElement) &&
                            typeElement.GetString() == "text" &&
                            element.TryGetProperty("text", out var textElement))
                        {
                            string? text = textElement.GetString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                if (sb.Length > 0)
                                    sb.AppendLine();

                                sb.Append(text);
                            }
                        }
                        // Image content is omitted for providers that don't support them
                    }
                    return sb.ToString();
                }
            }

            // Handle ContentParts from direct API usage
            if (content is IEnumerable<object> contentList)
            {
                var sb = new StringBuilder();
                foreach (var part in contentList)
                {
                    if (part is TextContentPart textPart)
                    {
                        if (!string.IsNullOrEmpty(textPart.Text))
                        {
                            if (sb.Length > 0)
                                sb.AppendLine();

                            sb.Append(textPart.Text);
                        }
                    }
                }

                if (sb.Length > 0)
                {
                    return sb.ToString();
                }
            }

            // Fallback: Just return the string representation
            return content.ToString() ?? string.Empty;
        }

        /// <summary>
        /// Determines if the content contains only text (no images).
        /// </summary>
        /// <param name="content">The message content</param>
        /// <returns>True if the content is text-only, false if it contains images</returns>
        public static bool IsTextOnly(object? content)
        {
            if (content == null || content is string)
                return true;

            // Handle ContentParts from direct API usage
            if (content is IEnumerable<object> contentList)
            {
                foreach (var part in contentList)
                {
                    if (part is ImageUrlContentPart or VideoUrlContentPart)
                        return false;

                    if (part is ProviderContentPart
                        {
                            Type: "image_url" or "video_url"
                        })
                    {
                        return false;
                    }

                    if (part is JsonElement element &&
                        element.ValueKind == JsonValueKind.Object &&
                        element.TryGetProperty("type", out var typeElement) &&
                        typeElement.GetString() is "image_url" or "video_url")
                    {
                        return false;
                    }
                }

                return true;
            }

            // Handle JSON Element
            if (content is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == JsonValueKind.String)
                    return true;

                if (jsonElement.ValueKind == JsonValueKind.Array)
                {
                    // Check each element in the array
                    foreach (var element in jsonElement.EnumerateArray())
                    {
                        if (element.TryGetProperty("type", out var typeElement) &&
                            typeElement.GetString() is "image_url" or "video_url")
                        {
                            return false; // Found an image
                        }
                    }
                    return true; // No images found
                }
            }

            // Unknown object-shaped content has no statically identifiable media parts.
            return true;
        }

        /// <summary>
        /// Determines if the content should be preserved as a JSON array rather than collapsed to a string.
        /// Every structured content array is ordered and may contain provider extensions, so collapsing or
        /// reconstructing it is never safe.
        /// </summary>
        /// <param name="content">The message content</param>
        /// <returns>True if the content has structured metadata that would be lost by collapsing to a string</returns>
        public static bool ShouldPreserveAsArray(object? content)
        {
            if (content == null || content is string)
                return false;

            // Raw API requests deserialize object-typed message content as JsonElement.
            if (content is JsonElement jsonElement)
            {
                return jsonElement.ValueKind == JsonValueKind.Array;
            }

            // Typed callers and prompt-cache injection both use enumerable content parts.
            if (content is IEnumerable<object>)
                return true;

            // Handle non-generic and value-type collections without treating dictionaries as parts.
            return content is System.Collections.IEnumerable
                and not System.Collections.IDictionary;
        }

        /// <summary>
        /// Extracts image URLs from multimodal content.
        /// </summary>
        /// <param name="content">The message content (can be string or content parts)</param>
        /// <returns>List of image URLs found in the content</returns>
        public static List<ImageUrl> ExtractImageUrls(object? content)
        {
            var imageUrls = new List<ImageUrl>();

            if (content == null)
                return imageUrls;

            if (content is string)
                return imageUrls; // Plain strings don't contain images

            // Handle ContentParts from direct API usage
            if (content is IEnumerable<object> contentList)
            {
                foreach (var part in contentList)
                {
                    if (part is ImageUrlContentPart imagePart && imagePart.ImageUrl != null)
                    {
                        imageUrls.Add(imagePart.ImageUrl);
                    }
                }

                if (imageUrls.Any())
                {
                    return imageUrls;
                }
            }

            // Handle JSON Element
            if (content is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in jsonElement.EnumerateArray())
                {
                    if (element.TryGetProperty("type", out var typeElement) &&
                        typeElement.GetString() == "image_url" &&
                        element.TryGetProperty("image_url", out var imageUrlElement))
                    {
                        string? url = null;
                        string? detail = null;

                        if (imageUrlElement.TryGetProperty("url", out var urlElement))
                        {
                            url = urlElement.GetString();
                        }

                        if (imageUrlElement.TryGetProperty("detail", out var detailElement))
                        {
                            detail = detailElement.GetString();
                        }

                        if (!string.IsNullOrEmpty(url))
                        {
                            imageUrls.Add(new ImageUrl
                            {
                                Url = url,
                                Detail = detail
                            });
                        }
                    }
                }
            }

            return imageUrls;
        }

        /// <summary>Extracts video URLs from typed or JSON multimodal content.</summary>
        public static List<VideoUrl> ExtractVideoUrls(object? content)
        {
            var videoUrls = new List<VideoUrl>();
            if (content is null or string)
                return videoUrls;

            if (content is IEnumerable<object> contentList)
            {
                videoUrls.AddRange(contentList
                    .OfType<VideoUrlContentPart>()
                    .Select(part => part.VideoUrl));
                if (videoUrls.Count > 0)
                    return videoUrls;
            }

            if (content is JsonElement { ValueKind: JsonValueKind.Array } jsonElement)
            {
                foreach (var element in jsonElement.EnumerateArray())
                {
                    if (!element.TryGetProperty("type", out var type) ||
                        type.GetString() != "video_url" ||
                        !element.TryGetProperty("video_url", out var video))
                        continue;

                    var url = video.TryGetProperty("url", out var urlElement)
                        ? urlElement.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(url))
                        continue;

                    videoUrls.Add(new VideoUrl
                    {
                        Url = url,
                        Detail = video.TryGetProperty("detail", out var detail) ? detail.GetString() : null,
                        MaxFrames = video.TryGetProperty("max_frames", out var maxFrames) ? maxFrames.GetInt32() : null,
                        SampleRate = video.TryGetProperty("sample_rate", out var sampleRate) ? sampleRate.GetDouble() : null,
                        StartTime = video.TryGetProperty("start_time", out var startTime) ? startTime.GetDouble() : null,
                        EndTime = video.TryGetProperty("end_time", out var endTime) ? endTime.GetDouble() : null
                    });
                }
            }

            return videoUrls;
        }
    }
}
