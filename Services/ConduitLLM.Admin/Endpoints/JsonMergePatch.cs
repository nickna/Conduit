using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using ConduitLLM.Admin.Serialization;

using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace ConduitLLM.Admin.Endpoints;

/// <summary>
/// Runtime-only request wrapper that keeps merge-patch parsing separate from the normal JSON
/// options used by OpenAPI schema generation.
/// </summary>
internal sealed class JsonMergePatch<T>
    where T : class
{
    private JsonMergePatch(T value)
    {
        Value = value;
    }

    public T Value { get; }

    public static async ValueTask<JsonMergePatch<T>> BindAsync(
        HttpContext context,
        ParameterInfo parameter)
    {
        var contentType = context.Request.GetTypedHeaders().ContentType?.MediaType.Value;
        if (!string.Equals(
                contentType,
                JsonMergePatchRouteExtensions.MediaType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new BadHttpRequestException(
                $"PATCH requests require Content-Type: {JsonMergePatchRouteExtensions.MediaType}.",
                StatusCodes.Status415UnsupportedMediaType);
        }

        var applicationOptions = context.RequestServices
            .GetRequiredService<IOptions<HttpJsonOptions>>()
            .Value
            .SerializerOptions;
        try
        {
            using var document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: context.RequestAborted);
            var value = JsonMergePatchState.Parse<T>(document.RootElement, applicationOptions);
            return new JsonMergePatch<T>(
                value ?? throw new BadHttpRequestException(
                    "A JSON Merge Patch request body is required."));
        }
        catch (JsonException exception)
        {
            throw new BadHttpRequestException(
                "The JSON Merge Patch request body is invalid.",
                StatusCodes.Status400BadRequest,
                exception);
        }
    }
}

/// <summary>
/// Accessors for the original JSON Merge Patch document associated with a deserialized update DTO.
/// </summary>
internal static class JsonMergePatchState
{
    private static readonly ConditionalWeakTable<object, PatchState> States = new();

    public static T Parse<T>(JsonElement document, JsonSerializerOptions serializerOptions)
        where T : class
    {
        if (document.ValueKind != JsonValueKind.Object)
            throw new JsonException("A JSON Merge Patch request body must be a JSON object.");

        var typeInfo = GetTypeInfo<T>(serializerOptions);
        var comparer = serializerOptions.PropertyNameCaseInsensitive
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var writableNames = typeInfo.Properties
            .Where(property => property.Set is not null)
            .Select(property => property.Name)
            .ToHashSet(comparer);

        foreach (var property in document.EnumerateObject())
        {
            if (!writableNames.Contains(property.Name))
                throw new JsonException($"The merge-patch property '{property.Name}' is not writable.");
        }

        var request = (T?)JsonSerializer.Deserialize(document, typeInfo)
            ?? throw new JsonException("The JSON Merge Patch request body cannot be null.");
        Attach(request, document.Clone(), serializerOptions);
        return request;
    }

    public static void Attach(
        object request,
        JsonElement document,
        JsonSerializerOptions serializerOptions)
    {
        States.Remove(request);
        States.Add(request, new PatchState(document, serializerOptions));
    }

    public static bool IsDefined<TRequest>(this TRequest request, string clrPropertyName)
        where TRequest : class
    {
        if (States.TryGetValue(request, out var state))
        {
            return state.TryGetProperty(clrPropertyName, out _);
        }

        var options = AdminJsonOptions.Create();
        var element = JsonSerializer.SerializeToElement(request, GetTypeInfo<TRequest>(options));
        return TryGetJsonProperty(element, GetJsonName(clrPropertyName, options), false, out var value)
            && value.ValueKind != JsonValueKind.Null;
    }

    public static bool TryGetPatchedProperty<TRequest, TValue>(
        this TRequest request,
        string clrPropertyName,
        TValue currentValue,
        out TValue patchedValue)
        where TRequest : class
    {
        if (!States.TryGetValue(request, out var state))
        {
            var fallbackOptions = AdminJsonOptions.Create();
            var element = JsonSerializer.SerializeToElement(request, GetTypeInfo<TRequest>(fallbackOptions));
            if (!TryGetJsonProperty(element, GetJsonName(clrPropertyName, fallbackOptions), false, out var value)
                || value.ValueKind == JsonValueKind.Null)
            {
                patchedValue = currentValue;
                return false;
            }

            patchedValue = (TValue)JsonSerializer.Deserialize(
                value,
                GetTypeInfo<TValue>(fallbackOptions))!;
            return true;
        }

        if (!state.TryGetProperty(clrPropertyName, out var patch))
        {
            patchedValue = currentValue;
            return false;
        }

        if (patch.ValueKind == JsonValueKind.Null &&
            typeof(TValue).IsValueType &&
            Nullable.GetUnderlyingType(typeof(TValue)) is null)
        {
            throw new BadHttpRequestException(
                $"Property '{state.GetJsonName(clrPropertyName)}' cannot be null.");
        }

        var valueTypeInfo = GetTypeInfo<TValue>(state.SerializerOptions);
        var targetNode = JsonSerializer.SerializeToNode(currentValue, valueTypeInfo);
        var patchNode = JsonNode.Parse(patch.GetRawText());
        var merged = Apply(targetNode, patchNode);
        patchedValue = merged is null
            ? default!
            : (TValue)JsonSerializer.Deserialize(merged, valueTypeInfo)!;
        return true;
    }

    private static JsonNode? Apply(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObject)
        {
            return patch?.DeepClone();
        }

        var targetObject = target as JsonObject ?? [];
        foreach (var property in patchObject)
        {
            if (property.Value is null)
            {
                targetObject.Remove(property.Key);
                continue;
            }

            targetObject[property.Key] = Apply(
                targetObject[property.Key]?.DeepClone(),
                property.Value);
        }

        return targetObject;
    }

    private static JsonTypeInfo GetTypeInfo<T>(JsonSerializerOptions options)
    {
        var typeInfo = options.GetTypeInfo(typeof(T));
        if (typeInfo is null)
            throw new JsonException($"No JSON contract is registered for {typeof(T).Name}.");
        return typeInfo;
    }

    private static string GetJsonName(string clrPropertyName, JsonSerializerOptions options) =>
        options.PropertyNamingPolicy?.ConvertName(clrPropertyName) ?? clrPropertyName;

    private static bool TryGetJsonProperty(
        JsonElement document,
        string jsonName,
        bool caseInsensitive,
        out JsonElement value)
    {
        foreach (var property in document.EnumerateObject())
        {
            if (string.Equals(property.Name, jsonName,
                    caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private sealed class PatchState
    {
        private readonly JsonElement _document;
        private readonly bool _propertyNameCaseInsensitive;

        public PatchState(
            JsonElement document,
            JsonSerializerOptions serializerOptions)
        {
            _document = document;
            _propertyNameCaseInsensitive = serializerOptions.PropertyNameCaseInsensitive;
            SerializerOptions = serializerOptions;
        }

        public JsonSerializerOptions SerializerOptions { get; }

        public bool TryGetProperty(string clrPropertyName, out JsonElement value)
        {
            var jsonName = GetJsonName(clrPropertyName);
            return TryGetJsonProperty(_document, jsonName, _propertyNameCaseInsensitive, out value);
        }

        public string GetJsonName(string clrPropertyName) =>
            JsonMergePatchState.GetJsonName(clrPropertyName, SerializerOptions);
    }
}

/// <summary>Requires the RFC 7386 media type for an Admin PATCH operation.</summary>
internal sealed class JsonMergePatchContentTypeEndpointFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var contentType = context.HttpContext.Request.GetTypedHeaders().ContentType?.MediaType.Value;
        if (string.Equals(
                contentType,
                JsonMergePatchRouteExtensions.MediaType,
                StringComparison.OrdinalIgnoreCase))
        {
            return next(context);
        }

        return ValueTask.FromResult<object?>(Results.Problem(
            statusCode: StatusCodes.Status415UnsupportedMediaType,
            title: "Unsupported Media Type",
            detail: $"PATCH requests require Content-Type: {JsonMergePatchRouteExtensions.MediaType}.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "unsupported_media_type",
                ["traceId"] = context.HttpContext.TraceIdentifier
            }));
    }
}

internal static class JsonMergePatchRouteExtensions
{
    public const string MediaType = "application/merge-patch+json";

    public static RouteHandlerBuilder AcceptsJsonMergePatch<TRequest>(this RouteHandlerBuilder builder)
        where TRequest : class =>
        builder
            .WithMetadata(new AcceptsMetadata([MediaType], typeof(TRequest), isOptional: false))
            .AddEndpointFilter<JsonMergePatchContentTypeEndpointFilter>();
}
