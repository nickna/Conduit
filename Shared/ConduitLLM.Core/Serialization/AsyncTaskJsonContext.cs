using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using ConduitLLM.Core.Interfaces;
using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Serialization;

/// <summary>
/// Source-generated contracts for persisted async-task metadata, results, and cache entries.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AsyncTaskStatus))]
[JsonSerializable(typeof(TaskMetadata))]
[JsonSerializable(typeof(ImageGenerationRequest))]
[JsonSerializable(typeof(VideoGenerationRequest))]
[JsonSerializable(typeof(VideoGenerationResponse))]
[JsonSerializable(typeof(MediaGenerationTaskResult))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(Dictionary<string, object>))]
public partial class AsyncTaskJsonContext : JsonSerializerContext;
