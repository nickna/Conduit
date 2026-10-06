using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ZiggyCreatures.Caching.Fusion.Serialization;

namespace ConduitLLM.Core.Caching;

/// <summary>Serializes only generated contracts, including FusionCache envelopes and tag markers.</summary>
public sealed class ApplicationCacheSerializer(JsonSerializerContext context) : IFusionCacheSerializer
{
    public ApplicationCacheSerializer() : this(ApplicationCacheJsonContext.Default) { }

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
