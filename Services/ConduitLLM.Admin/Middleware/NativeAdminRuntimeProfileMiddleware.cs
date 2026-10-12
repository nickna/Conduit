using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Endpoints;

using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace ConduitLLM.Admin.Middleware;

/// <summary>Rejects excluded native management operations before endpoint binding or DI.</summary>
public sealed class NativeAdminRuntimeProfileMiddleware
{
    private readonly RequestDelegate _next;

    private static readonly IReadOnlyDictionary<string, (string Pattern, string Method)> SupportedOperations =
        new Dictionary<string, (string Pattern, string Method)>(StringComparer.Ordinal)
        {
            ["GlobalSettings_List"] = ("/v1/admin/global-settings/", "GET"),
            ["GlobalSettings_GetDefinitions"] = ("/v1/admin/global-settings/definitions", "GET"),
            ["GlobalSettings_GetById"] = ("/v1/admin/global-settings/{id:int}", "GET"),
            ["GlobalSettings_GetByKey"] = ("/v1/admin/global-settings/by-key/{key}", "GET"),
            ["GlobalSettings_Create"] = ("/v1/admin/global-settings/", "POST"),
            ["GlobalSettings_Update"] = ("/v1/admin/global-settings/{id:int}", "PATCH"),
            ["GlobalSettings_UpdateByKey"] = ("/v1/admin/global-settings/by-key", "PUT"),
            ["GlobalSettings_Delete"] = ("/v1/admin/global-settings/{id:int}", "DELETE"),
            ["GlobalSettings_DeleteByKey"] = ("/v1/admin/global-settings/by-key/{key}", "DELETE"),
            ["GlobalSettings_GetCacheStats"] = ("/v1/admin/global-settings/cache/stats", "GET"),
            ["GlobalSettings_ReloadCache"] = ("/v1/admin/global-settings/cache/reload", "POST"),
            ["GlobalSettings_InvalidateCache"] = ("/v1/admin/global-settings/cache/invalidate/{key}", "POST"),
            ["IpFilter_GetAll"] = ("/v1/admin/ip-filters/", "GET"),
            ["IpFilter_GetEnabled"] = ("/v1/admin/ip-filters/enabled", "GET"),
            ["IpFilter_GetByVirtualKey"] = ("/v1/admin/ip-filters/by-virtual-key/{virtualKeyId}", "GET"),
            ["IpFilter_GetById"] = ("/v1/admin/ip-filters/{id}", "GET"),
            ["IpFilter_Create"] = ("/v1/admin/ip-filters/", "POST"),
            ["IpFilter_Update"] = ("/v1/admin/ip-filters/{id}", "PATCH"),
            ["IpFilter_Delete"] = ("/v1/admin/ip-filters/{id}", "DELETE"),
            ["IpFilter_GetSettings"] = ("/v1/admin/ip-filters/settings", "GET"),
            ["IpFilter_UpdateSettings"] = ("/v1/admin/ip-filters/settings", "PUT"),
            ["IpFilter_Check"] = ("/v1/admin/ip-filters/check/{ipAddress}", "GET"),
            ["Auth_GenerateEphemeralMasterKey"] = ("/v1/admin/auth-tokens/ephemeral-master-key", "POST")
        };

    public NativeAdminRuntimeProfileMiddleware(RequestDelegate next) =>
        _next = next ?? throw new ArgumentNullException(nameof(next));

    public Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint() is not RouteEndpoint endpoint || !IsManagement(endpoint.RoutePattern))
            return _next(context);

        var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        if (name is not null && SupportedOperations.TryGetValue(name, out var operation) &&
            string.Equals(endpoint.RoutePattern.RawText, operation.Pattern, StringComparison.Ordinal) &&
            methods is { Count: 1 } && string.Equals(methods[0], operation.Method, StringComparison.Ordinal))
            return _next(context);

        return AdminResults.Problem(StatusCodes.Status501NotImplemented,
            "This management operation is not supported by the native-aot runtime profile.",
            AdminErrorCodes.NotImplemented).ExecuteAsync(context);
    }

    private static bool IsManagement(RoutePattern pattern) =>
        pattern.PathSegments.Count >= 2 &&
        IsLiteral(pattern.PathSegments[0], "v1") && IsLiteral(pattern.PathSegments[1], "admin");

    private static bool IsLiteral(RoutePatternPathSegment segment, string value) =>
        segment.Parts.Count == 1 && segment.Parts[0] is RoutePatternLiteralPart literal &&
        string.Equals(literal.Content, value, StringComparison.OrdinalIgnoreCase);
}
