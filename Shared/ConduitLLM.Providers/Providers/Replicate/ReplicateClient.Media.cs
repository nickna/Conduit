using ConduitLLM.Core.Exceptions;
using ConduitLLM.Core.Models;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Providers.Replicate
{
    public partial class ReplicateClient
    {
        /// <inheritdoc/>
        public override async Task<ImageGenerationResponse> CreateImageAsync(
            ImageGenerationRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            ValidateRequest(request, "CreateImageAsync");

            Logger.LogInformation("Creating image with Replicate for model '{ModelId}'", ProviderModelId);

            return await ExecuteApiRequestAsync(async () =>
            {
                var predictionRequest = MapToImageGenerationRequest(request);
                var predictionResponse = await StartPredictionAsync(predictionRequest, apiKey, cancellationToken);
                await RecordMediaOperationAsync(predictionResponse.Id, cancellationToken);

                using var pollScope = BeginPollingScope("CreateImage");
                var finalPrediction = await PollPredictionUntilCompletedAsync(
                    predictionResponse.Id, apiKey, cancellationToken, pollScope);

                return MapToImageGenerationResponse(finalPrediction, request.Model ?? ProviderModelId);
            }, "CreateImage", cancellationToken);
        }

        /// <summary>
        /// Creates a video using Replicate's prediction API.
        /// </summary>
        /// <param name="request">The video generation request containing the prompt and generation parameters.</param>
        /// <param name="apiKey">Optional API key override to use instead of the client's configured key.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        /// <returns>The video generation response containing URLs to the generated video(s).</returns>
        public async Task<VideoGenerationResponse> CreateVideoAsync(
            VideoGenerationRequest request,
            string? apiKey = null,
            CancellationToken cancellationToken = default)
        {
            ValidateRequest(request, "CreateVideoAsync");

            Logger.LogInformation("Creating video with Replicate for model '{ModelId}' with prompt: '{Prompt}'",
                ProviderModelId, request.Prompt);

            return await ExecuteApiRequestAsync(async () =>
            {
                var predictionRequest = MapToVideoGenerationRequest(request);

                Logger.LogDebug("Video generation request mapped. Input parameters: {@InputParams}", predictionRequest.Input);

                var predictionResponse = await StartPredictionAsync(predictionRequest, apiKey, cancellationToken);
                await RecordMediaOperationAsync(predictionResponse.Id, cancellationToken);

                Logger.LogInformation("Video generation prediction started with ID: {PredictionId}, Status: {Status}",
                    predictionResponse.Id, predictionResponse.Status);

                using var pollScope = BeginPollingScope("CreateVideo");
                var finalPrediction = await PollPredictionUntilCompletedAsync(
                    predictionResponse.Id, apiKey, cancellationToken, pollScope);

                Logger.LogInformation("Video generation completed for prediction {PredictionId}. Final status: {Status}, Output: {@Output}",
                    finalPrediction.Id, finalPrediction.Status, finalPrediction.Output);

                return MapToVideoGenerationResponse(finalPrediction, request.Model);
            }, "CreateVideo", cancellationToken);
        }

        private ReplicatePredictionRequest MapToImageGenerationRequest(ImageGenerationRequest request)
        {
            // Prepare the input based on the model
            var input = new Dictionary<string, object>
            {
                ["prompt"] = request.Prompt
            };

            // Add optional parameters if provided
            if (request.Size != null)
            {
                var dimensions = request.Size.Split('x');
                if (dimensions.Length == 2 && int.TryParse(dimensions[0], out int width) && int.TryParse(dimensions[1], out int height))
                {
                    input["width"] = width;
                    input["height"] = height;
                }
            }

            if (request.Quality != null)
            {
                input["quality"] = request.Quality;
            }

            if (request.Style != null)
            {
                input["style"] = request.Style;
            }

            if (request.N > 1)
            {
                input["num_outputs"] = request.N;
            }

            // Add any additional model-specific parameters from ExtensionData
            if (request.ExtensionData != null)
            {
                foreach (var kvp in request.ExtensionData)
                {
                    // Don't override values that were already set from explicit properties
                    var value = ConvertJsonElement(kvp.Value);
                    if (value is not null && !input.ContainsKey(kvp.Key))
                    {
                        input[kvp.Key] = value;
                    }
                }
            }

            // Also handle the Image and Mask properties for image-to-image generation
            if (!string.IsNullOrEmpty(request.Image))
            {
                input["image"] = request.Image;
            }
            
            if (!string.IsNullOrEmpty(request.Mask))
            {
                input["mask"] = request.Mask;
            }

            // Log parameters being sent (excluding prompt content and sensitive data)
            var logParams = new Dictionary<string, object>(input);
            logParams["prompt"] = $"[REDACTED: {request.Prompt.Length} chars]";
            // Redact image data if present
            if (logParams.ContainsKey("image") && logParams["image"] is string image && image.Length > 100)
            {
                logParams["image"] = $"[REDACTED: {image.Length} chars]";
            }
            if (logParams.ContainsKey("mask") && logParams["mask"] is string mask && mask.Length > 100)
            {
                logParams["mask"] = $"[REDACTED: {mask.Length} chars]";
            }
            
            Logger.LogInformation("Image generation parameters for Replicate: Model={Model}, Parameters={@Parameters}", 
                ProviderModelId, logParams);

            return new ReplicatePredictionRequest
            {
                Version = ProviderModelId,
                Input = input
            };
        }

        private ReplicatePredictionRequest MapToVideoGenerationRequest(VideoGenerationRequest request)
        {
            // Prepare the input based on the model
            var input = new Dictionary<string, object>
            {
                ["prompt"] = request.Prompt
            };

            // Add optional parameters if provided
            if (request.Duration.HasValue)
            {
                // Most video models use "duration" or "num_seconds"
                input["duration"] = request.Duration.Value;
                input["num_seconds"] = request.Duration.Value;
            }

            if (request.Size != null)
            {
                // Parse size like "1280x720" into width and height
                var dimensions = request.Size.Split('x');
                if (dimensions.Length == 2 && int.TryParse(dimensions[0], out int width) && int.TryParse(dimensions[1], out int height))
                {
                    input["width"] = width;
                    input["height"] = height;
                }
                else
                {
                    // Some models use resolution directly
                    input["resolution"] = request.Size;
                }
            }

            if (request.Fps.HasValue)
            {
                input["fps"] = request.Fps.Value;
            }

            if (request.Seed.HasValue)
            {
                input["seed"] = request.Seed.Value;
            }

            if (request.N > 1)
            {
                input["num_outputs"] = request.N;
            }

            // Add any additional model-specific parameters from ExtensionData
            if (request.ExtensionData != null)
            {
                foreach (var kvp in request.ExtensionData)
                {
                    // Don't override values that were already set from explicit properties
                    var value = ConvertJsonElement(kvp.Value);
                    if (value is not null && !input.ContainsKey(kvp.Key))
                    {
                        input[kvp.Key] = value;
                    }
                }
            }

            // Log parameters being sent (excluding prompt content and sensitive data)
            var logParams = new Dictionary<string, object>(input);
            logParams["prompt"] = $"[REDACTED: {request.Prompt.Length} chars]";
            // Also redact any image data parameters
            if (logParams.ContainsKey("start_image") && logParams["start_image"] is string startImage && startImage.Length > 100)
            {
                logParams["start_image"] = $"[REDACTED: {startImage.Length} chars]";
            }
            if (logParams.ContainsKey("end_image") && logParams["end_image"] is string endImage && endImage.Length > 100)
            {
                logParams["end_image"] = $"[REDACTED: {endImage.Length} chars]";
            }
            
            Logger.LogInformation("Video generation parameters for Replicate: Model={Model}, Parameters={@Parameters}", 
                ProviderModelId, logParams);

            return new ReplicatePredictionRequest
            {
                Version = ProviderModelId,
                Input = input
            };
        }

        private ImageGenerationResponse MapToImageGenerationResponse(ReplicatePredictionResponse prediction, string originalModelAlias)
        {
            // Extract image URLs from the prediction output
            var imageUrls = ExtractImageUrlsFromPredictionOutput(prediction.Output);

            return new ImageGenerationResponse
            {
                Created = ((DateTimeOffset)prediction.CreatedAt).ToUnixTimeSeconds(),
                Data = imageUrls.Select(url => new Core.Models.ImageData
                {
                    Url = url
                }).ToList()
            };
        }

        private VideoGenerationResponse MapToVideoGenerationResponse(ReplicatePredictionResponse prediction, string originalModelAlias)
        {
            Logger.LogDebug("Mapping prediction response to VideoGenerationResponse. Prediction ID: {Id}, Status: {Status}", 
                prediction.Id, prediction.Status);
            
            // Extract video URLs from the prediction output
            var videoUrls = ExtractVideoUrlsFromPredictionOutput(prediction.Output);
            
            if (videoUrls.Count == 0)
            {
                Logger.LogError("Failed to extract any video URLs from prediction {Id} with status {Status}. Output: {@Output}", 
                    prediction.Id, prediction.Status, prediction.Output);
                throw new LLMCommunicationException($"No video URLs found in Replicate prediction output for prediction {prediction.Id}");
            }

            var response = new VideoGenerationResponse
            {
                Created = ((DateTimeOffset)prediction.CreatedAt).ToUnixTimeSeconds(),
                Data = videoUrls.Select(url => new VideoData
                {
                    Url = url
                }).ToList()
            };
            
            Logger.LogInformation("Successfully mapped video generation response with {Count} video(s) for prediction {Id}", 
                response.Data.Count, prediction.Id);
            
            return response;
        }
    }
}
