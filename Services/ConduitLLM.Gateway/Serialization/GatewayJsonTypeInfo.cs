using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ConduitLLM.Gateway.Serialization;

internal static class GatewayJsonTypeInfo
{
    public static JsonTypeInfo<T> Require<T>(JsonSerializerOptions options) =>
        options.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new InvalidOperationException(
            $"No source-generated JSON metadata is registered for {typeof(T).FullName}.");
}
