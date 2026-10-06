using System.Text.Json;
using System.Text.Json.Serialization;

using ConduitLLM.Core.Converters;
using ConduitLLM.Core.Serialization;

namespace ConduitLLM.Admin.Serialization;

/// <summary>
/// Owns the Admin HTTP wire-format serializer configuration.
/// </summary>
public static class AdminJsonOptions
{
    public static void Configure(JsonSerializerOptions options)
    {
        options.TypeInfoResolverChain.Insert(0, AdminHttpResponseJsonContext.Default);
        options.TypeInfoResolverChain.Insert(1, AdminHttpJsonContext.Default);
        options.TypeInfoResolverChain.Insert(2, CoreHttpJsonContext.Default);

        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        AddStringEnumConverter<ConduitLLM.Configuration.DTOs.ApiKeyTestResult>(options);
        AddStringEnumConverter<ConduitLLM.Configuration.DTOs.BulkModelMappingErrorType>(options);
        AddStringEnumConverter<ConduitLLM.Functions.Enums.ExecutionMode>(options);
        AddStringEnumConverter<ConduitLLM.Functions.Enums.ExecutionState>(options);
        AddStringEnumConverter<ConduitLLM.Configuration.Events.FlushPriority>(options);
        AddStringEnumConverter<ConduitLLM.Functions.Enums.FunctionPricingModel>(options);
        AddStringEnumConverter<ConduitLLM.Functions.Enums.FunctionProviderType>(options);
        AddStringEnumConverter<ConduitLLM.Functions.Enums.FunctionPurpose>(options);
        AddStringEnumConverter<ConduitLLM.Configuration.Entities.MediaQuotaExceededBehavior>(options);
        AddStringEnumConverter<ConduitLLM.Configuration.Models.ModelCapabilitySource>(options);
        AddStringEnumConverter<ConduitLLM.Configuration.Entities.NotificationSeverity>(options);
        AddStringEnumConverter<ConduitLLM.Configuration.Entities.NotificationType>(options);
        AddStringEnumConverter<ConduitLLM.Configuration.PricingModel>(options);
        AddStringEnumConverter<ConduitLLM.Core.Models.PromptCachingStrategy>(options);
        AddStringEnumConverter<ConduitLLM.Configuration.ProviderType>(options);
        AddStringEnumConverter<ConduitLLM.Configuration.Enums.ReferenceType>(options);
        AddStringEnumConverter<ConduitLLM.Configuration.Enums.RequestBillingMethod>(options);
        AddStringEnumConverter<TokenizerType>(options);
        AddStringEnumConverter<ConduitLLM.Configuration.Enums.TransactionType>(options);
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
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
}
