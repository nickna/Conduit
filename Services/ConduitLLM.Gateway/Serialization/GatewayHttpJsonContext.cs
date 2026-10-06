using System.Text.Json.Serialization;

using ConduitLLM.Configuration.DTOs;
using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Audio;
using ConduitLLM.Core.Models.Rerank;
using ConduitLLM.Core.Models.Responses;
using ConduitLLM.Functions.DTOs;
using ConduitLLM.Gateway.DTOs;
using ConduitLLM.Gateway.Endpoints;
using ConduitLLM.Gateway.Models;
using ConduitLLM.Gateway.Services;

namespace ConduitLLM.Gateway.Serialization;

/// <summary>
/// Source-generated metadata for Gateway-owned HTTP response contracts.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ModelListResponse))]
[JsonSerializable(typeof(ModelMetadataResponse))]
[JsonSerializable(typeof(DiscoveryCapabilitiesResponse))]
[JsonSerializable(typeof(ModelParametersResponse))]
[JsonSerializable(typeof(TaskCancellationResponse))]
[JsonSerializable(typeof(FileMetadataResponse))]
[JsonSerializable(typeof(DownloadUrlResponse))]
[JsonSerializable(typeof(MediaUploadResponse))]
[JsonSerializable(typeof(DiscoveryModelsResponse))]
[JsonSerializable(typeof(global::ConduitLLM.Gateway.Endpoints.FunctionsEndpoints.FunctionExecutionRequest))]
[JsonSerializable(typeof(global::ConduitLLM.Gateway.Endpoints.GenerateEphemeralKeyRequest))]
[JsonSerializable(typeof(global::ConduitLLM.Gateway.Endpoints.GenerateUrlRequest))]
[JsonSerializable(typeof(global::Microsoft.AspNetCore.Http.IFormFile))]
[JsonSerializable(typeof(AsyncTaskResponse))]
[JsonSerializable(typeof(AsyncTaskStatus))]
[JsonSerializable(typeof(AsyncTaskStatusResponse))]
[JsonSerializable(typeof(AudioTranscriptionResponse))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(ChatCompletionResponse))]
[JsonSerializable(typeof(EmbeddingResponse))]
[JsonSerializable(typeof(EphemeralKeyResponse))]
[JsonSerializable(typeof(FunctionDiscoveryResponse))]
[JsonSerializable(typeof(FunctionExecutionDto))]
[JsonSerializable(typeof(FunctionParametersResponseDto))]
[JsonSerializable(typeof(ImageGenerationResponse))]
[JsonSerializable(typeof(MediaInfo))]
[JsonSerializable(typeof(ModelListItemDto))]
[JsonSerializable(typeof(OpenAIErrorResponse))]
[JsonSerializable(typeof(RerankResponse))]
[JsonSerializable(typeof(RuntimeCapabilitiesResponse))]
[JsonSerializable(typeof(ResponseObject))]
[JsonSerializable(typeof(ResponseStreamEvent))]
[JsonSerializable(typeof(SseErrorEvent))]
[JsonSerializable(typeof(SseReasoningEvent))]
[JsonSerializable(typeof(VideoGenerationTaskStatus))]
public partial class GatewayHttpJsonContext : JsonSerializerContext;
