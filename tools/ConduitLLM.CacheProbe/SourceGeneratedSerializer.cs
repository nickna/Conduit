using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ZiggyCreatures.Caching.Fusion.Serialization;

namespace ConduitLLM.CacheProbe;

// The upstream SystemTextJson adapter calls the reflection-capable generic overloads,
// producing IL2026/IL3050 even with a generated resolver. Use the metadata overloads.
internal sealed class SourceGeneratedSerializer(JsonSerializerContext context) : IFusionCacheSerializer
{
    private JsonTypeInfo<T> Metadata<T>() => (JsonTypeInfo<T>)(context.GetTypeInfo(typeof(T))
        ?? throw new NotSupportedException($"Unregistered application cache contract: {typeof(T)}"));

    public byte[] Serialize<T>(T? obj) => JsonSerializer.SerializeToUtf8Bytes(obj!, Metadata<T>());
    public T? Deserialize<T>(byte[] data) => JsonSerializer.Deserialize(data, Metadata<T>());
    public ValueTask<byte[]> SerializeAsync<T>(T? obj, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Serialize(obj));
    }
    public ValueTask<T?> DeserializeAsync<T>(byte[] data, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Deserialize<T>(data));
    }
}
