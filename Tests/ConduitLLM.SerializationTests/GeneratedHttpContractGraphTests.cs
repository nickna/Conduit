using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using ConduitLLM.Admin.Serialization;
using ConduitLLM.Configuration;
using ConduitLLM.Gateway.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using AdminProgram = ConduitLLM.Admin.Program;

namespace ConduitLLM.SerializationTests;

public sealed class GeneratedHttpContractGraphTests
{
    [Fact]
    public async Task AdminRuntimeEndpointsResolveEveryAdvertisedJsonContractWithoutReflection()
    {
        var builder = CreateBuilder(typeof(AdminProgram));
        AdminProgram.ConfigureHttpServices(builder);
        builder.Services.AddHealthChecks();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<ConduitLLM.Configuration.Interfaces.IModelAuthorRepository,
            ConduitLLM.Configuration.Repositories.ModelAuthorRepository>();
        builder.Services.AddScoped<ConduitLLM.Core.Interfaces.IWebhookRecovery,
            ConduitLLM.Messaging.Wolverine.WebhookDeliveryStore>();
        await using var app = builder.Build();
        AdminProgram.MapRuntimeEndpoints(app);

        AssertGeneratedContracts(app, minimumEndpointCount: 200);
    }

    [Fact]
    public async Task GatewayRuntimeEndpointsResolveEveryAdvertisedJsonContractWithoutReflection()
    {
        var builder = CreateBuilder(typeof(global::Program));
        global::Program.ConfigureBasicSettings(builder);
        builder.Services.AddHealthChecks();
        builder.Services.AddSignalR();
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        global::Program.ConfigureEndpoints(app);

        AssertGeneratedContracts(app, minimumEndpointCount: 40);
    }

    [Theory]
    [InlineData("\"openAI\"", ProviderType.OpenAI)]
    [InlineData("\"bedrock\"", ProviderType.Bedrock)]
    public void AdminProviderTypeAndNullableProviderTypeHaveUsableGeneratedMetadata(
        string json,
        ProviderType expected)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var options = AdminJsonOptions.Create();
        var enumMetadata = options.GetTypeInfo(typeof(ProviderType));
        var nullableMetadata = options.GetTypeInfo(typeof(ProviderType?));

        Assert.Equal(expected, JsonSerializer.Deserialize(json, enumMetadata));
        Assert.Equal(expected, JsonSerializer.Deserialize(json, nullableMetadata));
        Assert.Null(JsonSerializer.Deserialize("null", nullableMetadata));
        Assert.Equal(json, JsonSerializer.Serialize(expected, enumMetadata));
        Assert.Equal(json, JsonSerializer.Serialize(expected, nullableMetadata));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("1", enumMetadata));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("1", nullableMetadata));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HttpOptionsRejectAnUnregisteredRuntimeContract(bool gateway)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        if (gateway)
            GatewayJsonOptions.Configure(options);
        else
            AdminJsonOptions.Configure(options);

        Assert.Throws<NotSupportedException>(() =>
            JsonSerializer.Serialize(new UnregisteredContract("missing metadata"), options));
        Assert.All(options.TypeInfoResolverChain, resolver => Assert.IsAssignableFrom<JsonSerializerContext>(resolver));
        Assert.DoesNotContain(options.Converters, converter => converter.GetType() == typeof(JsonStringEnumConverter));
    }

    private static WebApplicationBuilder CreateBuilder(Type program)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = program.Assembly.GetName().Name,
            EnvironmentName = "Production"
        });
        builder.Logging.ClearProviders();
        return builder;
    }

    private static void AssertGeneratedContracts(WebApplication app, int minimumEndpointCount)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        // Enumerating the production data sources also materializes generated request
        // delegates. A missing query enum contract (the original ProviderType failure)
        // must therefore fail here even if no request happens to exercise that route.
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        Assert.True(endpoints.Length >= minimumEndpointCount,
            $"Expected the complete production map, got {endpoints.Length} endpoints.");
        var options = app.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value.SerializerOptions;
        Assert.DoesNotContain(options.TypeInfoResolverChain, resolver => resolver is DefaultJsonTypeInfoResolver);
        var contracts = endpoints.SelectMany(endpoint =>
            endpoint.Metadata.GetOrderedMetadata<IAcceptsMetadata>()
                .Where(metadata => metadata.RequestType is not null && IsJson(metadata.ContentTypes))
                .Select(metadata => (Endpoint: endpoint, Type: metadata.RequestType!))
                .Concat(endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>()
                    .Where(metadata => metadata.Type is not null && metadata.Type != typeof(void)
                        && IsJson(metadata.ContentTypes))
                    .Select(metadata => (Endpoint: endpoint, Type: metadata.Type!))))
            .ToArray();
        Assert.NotEmpty(contracts);
        var errors = new List<string>();
        var visited = new HashSet<Type>();
        foreach (var contract in contracts)
        {
            try
            {
                var pending = new Stack<Type>();
                pending.Push(contract.Type);
                while (pending.TryPop(out var type))
                {
                    if (!visited.Add(type))
                        continue;
                    var metadata = options.GetTypeInfo(type);
                    Assert.NotNull(metadata);
                    foreach (var property in metadata.Properties)
                        pending.Push(property.PropertyType);
                    if (metadata.Kind is JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary)
                    {
                        // Generated collection metadata must cover its element/key/value
                        // types even when a fixture happens to contain an empty collection.
                        foreach (var argument in type.GetGenericArguments())
                            pending.Push(argument);
                        if (type.GetElementType() is { } elementType)
                            pending.Push(elementType);
                    }
                    if (metadata.PolymorphismOptions is { } polymorphism)
                        foreach (var derived in polymorphism.DerivedTypes)
                            pending.Push(derived.DerivedType);
                }
            }
            catch (Exception exception)
            {
                errors.Add($"{contract.Endpoint.DisplayName}: {contract.Type}: {exception.Message}");
            }
        }
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    private static bool IsJson(IEnumerable<string> contentTypes) =>
        !contentTypes.Any() || contentTypes.Any(contentType =>
            contentType.Contains("json", StringComparison.OrdinalIgnoreCase));

    private sealed record UnregisteredContract(string Value);
}
