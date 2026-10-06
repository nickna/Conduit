using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ConduitLLM.Core.Serialization;

/// <summary>
/// Resolves runtime-selected Core contracts exclusively from generated metadata.
/// </summary>
internal static class CoreJsonTypeInfo
{
    public static JsonTypeInfo Require(Type type, JsonSerializerOptions options) =>
        TryGet(type, options)
        ?? throw new NotSupportedException(
            $"JSON contract '{type}' is not registered in a Conduit Core source-generation context.");

    public static JsonTypeInfo? TryGet(Type type, JsonSerializerOptions options)
    {
        JsonTypeInfo? typeInfo;

        typeInfo = new CoreInternalJsonContext(new JsonSerializerOptions(options)).GetTypeInfo(type);
        if (typeInfo is not null) return typeInfo;

        typeInfo = new CoreHttpJsonContext(new JsonSerializerOptions(options)).GetTypeInfo(type);
        if (typeInfo is not null) return typeInfo;

        typeInfo = new CoreMessagingJsonContext(new JsonSerializerOptions(options)).GetTypeInfo(type);
        if (typeInfo is not null) return typeInfo;

        typeInfo = new AsyncTaskJsonContext(new JsonSerializerOptions(options)).GetTypeInfo(type);
        if (typeInfo is not null) return typeInfo;

        typeInfo = new CorePricingJsonContext(new JsonSerializerOptions(options)).GetTypeInfo(type);
        if (typeInfo is not null) return typeInfo;

        return new CoreRedisJsonContext(new JsonSerializerOptions(options)).GetTypeInfo(type);
    }
}
