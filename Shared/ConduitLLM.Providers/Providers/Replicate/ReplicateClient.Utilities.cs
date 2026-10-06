using System.Text.Json;

using ConduitLLM.Providers.Helpers;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.Replicate
{
    public partial class ReplicateClient
    {
        private string ExtractTextFromPredictionOutput(object? output)
        {
            // Handle different output formats from different models
            if (output == null)
            {
                return string.Empty;
            }

            try
            {
                // String output (common for text generation models)
                if (output is string str)
                {
                    return str;
                }

                // List of strings (some models return this)
                if (output is JsonElement element)
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        return element.GetString() ?? string.Empty;
                    }
                    else if (element.ValueKind == JsonValueKind.Array)
                    {
                        // Try to read as array of strings
                        var result = new System.Text.StringBuilder();
                        foreach (var item in element.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                result.Append(item.GetString());
                            }
                        }
                        return result.ToString();
                    }
                }

                // Persisted provider output normally arrives as JsonElement; unknown typed
                // values use their non-reflective string representation.
                return output.ToString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error extracting text from prediction output");
                return string.Empty;
            }
        }

        private List<string> ExtractImageUrlsFromPredictionOutput(object? output)
        {
            var urls = new List<string>();

            // Handle different output formats from different models
            if (output == null)
            {
                return urls;
            }

            try
            {
                // String output (single image URL)
                if (output is string str)
                {
                    urls.Add(str);
                    return urls;
                }

                // Array of strings (multiple image URLs)
                if (output is JsonElement element)
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        urls.Add(element.GetString() ?? string.Empty);
                        return urls;
                    }
                    else if (element.ValueKind == JsonValueKind.Array)
                    {
                        // Try to read as array of strings
                        foreach (var item in element.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                string? url = item.GetString();
                                if (!string.IsNullOrEmpty(url))
                                {
                                    urls.Add(url);
                                }
                            }
                        }
                        return urls;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error extracting image URLs from prediction output");
            }

            return urls;
        }

        private List<string> ExtractVideoUrlsFromPredictionOutput(object? output)
        {
            // Video models typically return URLs in the same format as image models
            // This method is separate in case we need video-specific handling in the future
            
            Logger.LogDebug("Extracting video URLs from prediction output. Output type: {OutputType}", 
                output?.GetType().Name ?? "null");
            
            if (output != null)
            {
                // Log the raw output for debugging
                try
                {
                    var outputJson = output is JsonElement element
                        ? element.GetRawText()
                        : output.ToString();
                    Logger.LogDebug("Raw prediction output for video: {Output}", outputJson);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not serialize prediction output for logging");
                }
            }
            
            var urls = ExtractImageUrlsFromPredictionOutput(output);
            
            if (urls.Count == 0)
            {
                Logger.LogWarning("No video URLs found in prediction output. Output was: {@Output}", output);
            }
            else
            {
                Logger.LogInformation("Extracted {Count} video URL(s) from prediction output", urls.Count);
                foreach (var url in urls)
                {
                    Logger.LogDebug("Extracted video URL: {Url}", url);
                }
            }
            
            return urls;
        }

        private static object? ConvertJsonElement(JsonElement element) =>
            ConduitLLM.Functions.Utilities.JsonElementConverter.ConvertJsonElement(element);
    }
}
