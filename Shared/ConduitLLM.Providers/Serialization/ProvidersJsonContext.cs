using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using ConduitLLM.Providers.Bedrock;
using ConduitLLM.Providers.OpenRouter;
using ConduitLLM.Providers.Cloudflare;
using ConduitLLM.Providers.Replicate;
using ConduitLLM.Providers.OpenAI;
using ConduitLLM.Core.Models;
using ConduitLLM.Core.Models.Rerank;

namespace ConduitLLM.Providers.Serialization;

/// <summary>
/// Source-generated metadata for selected high-volume provider request/response paths.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BedrockConverseRequest))]
[JsonSerializable(typeof(BedrockConverseResponse))]
[JsonSerializable(typeof(BedrockRawToolArguments))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(List<JsonElement>))]
[JsonSerializable(typeof(BedrockListFoundationModelsResponse))]
[JsonSerializable(typeof(BedrockStreamMessageStart))]
[JsonSerializable(typeof(BedrockStreamContentBlockStart))]
[JsonSerializable(typeof(BedrockStreamContentBlockDelta))]
[JsonSerializable(typeof(BedrockStreamMessageStop))]
[JsonSerializable(typeof(BedrockStreamMetadata))]
[JsonSerializable(typeof(OpenRouterCatalogResponse))]
[JsonSerializable(typeof(OpenAIChatCompletionResponse))]
[JsonSerializable(typeof(List<OpenAIMessage>))]
[JsonSerializable(typeof(ListModelsResponse))]
[JsonSerializable(typeof(ConduitLLM.Providers.OpenAI.AzureOpenAIModels.ListDeploymentsResponse))]
[JsonSerializable(typeof(ConduitLLM.Providers.OpenAI.EmbeddingRequest))]
[JsonSerializable(typeof(ConduitLLM.Providers.OpenAI.EmbeddingResponse))]
[JsonSerializable(typeof(ConduitLLM.Providers.OpenAI.ImageGenerationResponse))]
[JsonSerializable(typeof(RerankResponse))]
[JsonSerializable(typeof(OpenRouterClient.OpenRouterImageResponse))]
[JsonSerializable(typeof(OpenRouterClient.OpenRouterVideoSubmitResponse))]
[JsonSerializable(typeof(OpenRouterClient.OpenRouterVideoStatus))]
[JsonSerializable(typeof(CloudflareImageResponse))]
[JsonSerializable(typeof(CloudflareModelsSearchResponse))]
[JsonSerializable(typeof(ReplicatePredictionRequest))]
[JsonSerializable(typeof(ReplicatePredictionResponse))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
internal partial class ProvidersJsonContext : JsonSerializerContext;
