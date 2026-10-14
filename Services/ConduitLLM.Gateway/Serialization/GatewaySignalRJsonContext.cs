using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ConduitLLM.Core.Models;

namespace ConduitLLM.Gateway.Serialization;

/// <summary>Generated contracts for the Gateway's dynamic task hub arguments.</summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TaskMetadata))]
[JsonSerializable(typeof(MediaGenerationTaskResult))]
[JsonSerializable(typeof(VideoGenerationResponse))]
[JsonSerializable(typeof(ImageGenerationFailureResult))]
[JsonSerializable(typeof(VideoGenerationFailureResult))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(List<object>))]
[JsonSerializable(typeof(object[]))]
public partial class GatewaySignalRJsonContext : JsonSerializerContext;
