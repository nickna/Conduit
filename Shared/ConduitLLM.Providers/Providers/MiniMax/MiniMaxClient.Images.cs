using ConduitLLM.Core.Models;
using ConduitLLM.Core.Exceptions;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.MiniMax
{
    /// <summary>
    /// MiniMaxClient partial class containing image generation functionality.
    /// </summary>
    public partial class MiniMaxClient
    {
        /// <inheritdoc />
        public override async Task<ImageGenerationResponse> CreateImageAsync(
            ImageGenerationRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            ValidateRequest(request, "CreateImage");

            return await ExecuteApiRequestAsync(async () =>
            {
                using var httpClient = CreateHttpClient(apiKey);
                
                var miniMaxRequest = new MiniMaxImageGenerationRequest
                {
                    Model = request.Model ?? "image-01",
                    Prompt = request.Prompt,
                    AspectRatio = MapSizeToAspectRatio(request.Size),
                    ResponseFormat = "url", // Always request URLs, we'll convert if needed
                    N = request.N,
                    PromptOptimizer = true
                };

                // Add subject reference if provided (for future use)
                if (!string.IsNullOrEmpty(request.User))
                {
                    // MiniMax uses this for tracking, not subject reference
                }

                var endpoint = $"{_baseUrl}/v1/image_generation";
                
                var response = await SendMiniMaxJsonAsync<
                    MiniMaxImageGenerationRequest,
                    MiniMaxImageGenerationResponse>(
                    httpClient,
                    endpoint,
                    miniMaxRequest,
                    MediaJsonOptions,
                    cancellationToken);
                
                // Check for MiniMax error response
                if (response.BaseResp is { } baseResp && baseResp.StatusCode != 0)
                {
                    Logger.LogError("MiniMax image generation error: {StatusCode} - {StatusMsg}", 
                        baseResp.StatusCode, baseResp.StatusMsg);
                    throw new LLMCommunicationException($"MiniMax error: {baseResp.StatusMsg}");
                }

                // Map MiniMax response to Core response
                var imageData = new List<ImageData>();
                
                // Handle URL response format
                if (response.Data?.ImageUrls != null)
                {
                    foreach (var imageUrl in response.Data.ImageUrls)
                    {
                        // If user requested b64_json, download and convert the image
                        if (request.ResponseFormat == "b64_json")
                        {
                            try
                            {
                                Logger.LogInformation("Downloading image from URL for base64 conversion: {Url}", imageUrl);
                                using var imageResponse = await httpClient.GetAsync(imageUrl, cancellationToken);
                                if (imageResponse.IsSuccessStatusCode)
                                {
                                    var imageBytes = await imageResponse.Content.ReadAsByteArrayAsync(cancellationToken);
                                    var base64String = Convert.ToBase64String(imageBytes);
                                    imageData.Add(new ImageData
                                    {
                                        Url = null,
                                        B64Json = base64String
                                    });
                                }
                                else
                                {
                                    Logger.LogWarning("Failed to download image from {Url}: {Status}", imageUrl, imageResponse.StatusCode);
                                    imageData.Add(new ImageData
                                    {
                                        Url = imageUrl,
                                        B64Json = null
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError(ex, "Error downloading image from {Url}", imageUrl);
                                imageData.Add(new ImageData
                                {
                                    Url = imageUrl,
                                    B64Json = null
                                });
                            }
                        }
                        else
                        {
                            imageData.Add(new ImageData
                            {
                                Url = imageUrl,
                                B64Json = null
                            });
                        }
                    }
                }
                
                // Handle base64 response format
                if (response.Data?.Images != null)
                {
                    foreach (var image in response.Data.Images)
                    {
                        imageData.Add(new ImageData
                        {
                            Url = null,
                            B64Json = image.B64
                        });
                    }
                }
                
                // Handle MiniMax base64 format (image_base64 field)
                if (response.Data?.ImageBase64 != null)
                {
                    foreach (var base64Image in response.Data.ImageBase64)
                    {
                        imageData.Add(new ImageData
                        {
                            Url = null,
                            B64Json = base64Image
                        });
                    }
                }

                return new ImageGenerationResponse
                {
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Data = imageData
                };
            }, "CreateImage", cancellationToken);
        }
    }
}
