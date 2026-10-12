#nullable enable

using ConduitLLM.Admin.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using AdminProgram = ConduitLLM.Admin.Program;

namespace ConduitLLM.Tests.Admin.Middleware;

[Collection("AdminCompositionEnvironment")]
public sealed class NativeAdminRuntimeProfileRouteInventoryTests
{
    // Independent public-contract oracle, checked against production's actual map graph.
    private static readonly (string Name, string Pattern, string Method)[] Approved =
    [
        ("GlobalSettings_List", "/v1/admin/global-settings/", "GET"),
        ("GlobalSettings_GetDefinitions", "/v1/admin/global-settings/definitions", "GET"),
        ("GlobalSettings_GetById", "/v1/admin/global-settings/{id:int}", "GET"),
        ("GlobalSettings_GetByKey", "/v1/admin/global-settings/by-key/{key}", "GET"),
        ("GlobalSettings_Create", "/v1/admin/global-settings/", "POST"),
        ("GlobalSettings_Update", "/v1/admin/global-settings/{id:int}", "PATCH"),
        ("GlobalSettings_UpdateByKey", "/v1/admin/global-settings/by-key", "PUT"),
        ("GlobalSettings_Delete", "/v1/admin/global-settings/{id:int}", "DELETE"),
        ("GlobalSettings_DeleteByKey", "/v1/admin/global-settings/by-key/{key}", "DELETE"),
        ("GlobalSettings_GetCacheStats", "/v1/admin/global-settings/cache/stats", "GET"),
        ("GlobalSettings_ReloadCache", "/v1/admin/global-settings/cache/reload", "POST"),
        ("GlobalSettings_InvalidateCache", "/v1/admin/global-settings/cache/invalidate/{key}", "POST"),
        ("IpFilter_GetAll", "/v1/admin/ip-filters/", "GET"),
        ("IpFilter_GetEnabled", "/v1/admin/ip-filters/enabled", "GET"),
        ("IpFilter_GetByVirtualKey", "/v1/admin/ip-filters/by-virtual-key/{virtualKeyId}", "GET"),
        ("IpFilter_GetById", "/v1/admin/ip-filters/{id}", "GET"),
        ("IpFilter_Create", "/v1/admin/ip-filters/", "POST"),
        ("IpFilter_Update", "/v1/admin/ip-filters/{id}", "PATCH"),
        ("IpFilter_Delete", "/v1/admin/ip-filters/{id}", "DELETE"),
        ("IpFilter_GetSettings", "/v1/admin/ip-filters/settings", "GET"),
        ("IpFilter_UpdateSettings", "/v1/admin/ip-filters/settings", "PUT"),
        ("IpFilter_Check", "/v1/admin/ip-filters/check/{ipAddress}", "GET"),
        ("Auth_GenerateEphemeralMasterKey", "/v1/admin/auth-tokens/ephemeral-master-key", "POST")
    ];

    [Fact]
    public async Task AllTwentyThreeApprovedOperationsMatchActualProductionMetadataAndContinue()
    {
        using var environment = new InventoryEnvironment();
        await using var app = CreateProductionMap();
        var routes = Routes(app);
        Assert.Equal(23, Approved.Length);
        Assert.Equal(23, Approved.Select(operation => operation.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (var operation in Approved)
        {
            var endpoint = Assert.Single(routes, route =>
                route.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == operation.Name);
            Assert.Equal(operation.Pattern, endpoint.RoutePattern.RawText);
            Assert.Equal(operation.Method, Assert.Single(endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods));
            var nextCalls = 0;
            var boundary = new NativeAdminRuntimeProfileMiddleware(_ => { nextCalls++; return Task.CompletedTask; });
            var context = Context(app, endpoint, operation.Method);
            await boundary.InvokeAsync(context);
            Assert.Equal(1, nextCalls);
        }
    }

    [Fact]
    public async Task EveryOtherMatchedProductionManagementOperationRejectsWithoutInvokingItsHandler()
    {
        using var environment = new InventoryEnvironment();
        await using var app = CreateProductionMap();
        var routes = Routes(app);
        var names = routes.Select(route => route.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName)
            .Where(name => name is not null).Cast<string>().ToArray();
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        var infrastructure = new HashSet<string>(StringComparer.Ordinal)
        {
            "/health", "/health/live", "/health/ready", "/health/runtime-capabilities", "/metrics/"
        };
        // Materialized production routes cannot add an alternate management namespace
        // or root alias that escapes the literal /v1/admin boundary unnoticed.
        Assert.All(routes, route => Assert.True(route.RoutePattern.RawText is { } pattern &&
            (pattern.StartsWith("/v1/admin/", StringComparison.Ordinal) || infrastructure.Contains(pattern))));
        var management = routes.Where(route => route.RoutePattern.RawText?.StartsWith("/v1/admin/", StringComparison.Ordinal) == true).ToArray();
        Assert.True(management.Length > Approved.Length);
        var approved = Approved.Select(operation => operation.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var endpoint in management)
        {
            var name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
            if (name is not null && approved.Contains(name)) continue;
            var nextCalls = 0;
            var boundary = new NativeAdminRuntimeProfileMiddleware(_ => { nextCalls++; return Task.CompletedTask; });
            var context = Context(app, endpoint, "GET");
            await boundary.InvokeAsync(context);
            Assert.Equal(StatusCodes.Status501NotImplemented, context.Response.StatusCode);
            Assert.Equal(0, nextCalls);
        }
        // Concrete direct-context/version-filter/manual-media families are actual production maps.
        foreach (var prefix in new[] { "virtual-keys", "virtual-key-groups", "provider-tools", "routing-configurations", "media-retention-policies", "health-status" })
            Assert.Contains(management, route => route.RoutePattern.RawText!.StartsWith("/v1/admin/" + prefix + "/", StringComparison.Ordinal));
        Assert.DoesNotContain(routes, route => route.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task RuntimeMapsPreserveInfrastructureAndTheExistingPricingProfile()
    {
        using var environment = new InventoryEnvironment();
        await using var app = CreateProductionMap();
        var routes = Routes(app);
        foreach (var pattern in new[] { "/health", "/health/live", "/health/ready", "/health/runtime-capabilities", "/metrics/" })
            Assert.Contains(routes, route => route.RoutePattern.RawText == pattern);
        var pricing = routes.Where(route => route.RoutePattern.RawText?.StartsWith("/v1/admin/pricing", StringComparison.Ordinal) == true).ToArray();
#if CONDUIT_NATIVE_AOT
        Assert.Empty(pricing);
#else
        Assert.NotEmpty(pricing);
#endif
    }

    private static WebApplication CreateProductionMap()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(AdminProgram).Assembly.GetName().Name,
            EnvironmentName = Environments.Production
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        AdminProgram.ConfigureHttpServices(builder);
        AdminProgram.ConfigureRuntimeServices(builder, NullLogger.Instance);
        var app = builder.Build();
        AdminProgram.MapRuntimeEndpoints(app);
        return app; // Materialize endpoint metadata, never start hosted services.
    }

    private static RouteEndpoint[] Routes(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();

    private static DefaultHttpContext Context(WebApplication app, RouteEndpoint endpoint, string method)
    {
        var context = new DefaultHttpContext { RequestServices = app.Services, TraceIdentifier = "owned-route-inventory" };
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(endpoint);
        return context;
    }

    private sealed class InventoryEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> _originals;
        public InventoryEnvironment()
        {
            var values = new Dictionary<string, string?>
            {
                ["DATABASE_URL"] = "postgresql://owned:owned@127.0.0.1:1/owned_metadata_only",
                ["REDIS_URL"] = null,
                ["CONDUIT_REDIS_CONNECTION_STRING"] = null,
                ["ConduitLLM__Messaging__Wolverine__Transport"] = "InMemory",
                ["CONDUIT_MIGRATION_MODE"] = "Skip",
                ["ApplicationCache__Environment"] = $"route-inventory-{Guid.NewGuid():N}",
                ["Logging__EventLog__LogLevel__Default"] = "None"
            };
            _originals = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            foreach (var entry in values) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }
        public void Dispose()
        {
            foreach (var entry in _originals) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }
    }
}
