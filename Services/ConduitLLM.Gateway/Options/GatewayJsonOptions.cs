using System.Text.Json;
using System.Text.Json.Serialization;

using ConduitLLM.Core.Converters;
using ConduitLLM.Core.Serialization;
using ConduitLLM.Gateway.Serialization;

namespace ConduitLLM.Gateway.Options;

/// <summary>
/// Owns the Gateway wire-format serializer configuration.
/// </summary>
public static class GatewayJsonOptions
{
    public static void Configure(JsonSerializerOptions options)
    {
        options.TypeInfoResolverChain.Insert(0, GatewayHttpJsonContext.Default);
        options.TypeInfoResolverChain.Insert(1, CoreHttpJsonContext.Default);
        options.TypeInfoResolverChain.Insert(2, GatewayInternalJsonContext.Default);

        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        AddStringEnumConverter<ConduitLLM.Functions.Enums.ExecutionState>(options);
        AddStringEnumConverter<ConduitLLM.Core.Models.MediaType>(options);
        AddStringEnumConverter<ConduitLLM.Core.Interfaces.TaskState>(options);
        options.Converters.Add(new UtcDateTimeConverter());
        options.Converters.Add(new NullableUtcDateTimeConverter());
    }

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions();
        Configure(options);
        return options;
    }

    private static void AddStringEnumConverter<TEnum>(JsonSerializerOptions options)
        where TEnum : struct, Enum =>
        options.Converters.Add(
            new JsonStringEnumConverter<TEnum>(
                JsonNamingPolicy.SnakeCaseLower,
                allowIntegerValues: false));
}
