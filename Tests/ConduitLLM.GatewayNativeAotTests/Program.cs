using System.Net;
using System.Text.Json;
using StackExchange.Redis;

var settings = ProbeSettings.FromEnvironment();
var probe = new GatewayNativeParityProbe(settings);
await probe.RunAsync();

internal sealed record ProbeSettings(
    Uri Admin,
    Uri Gateway,
    Uri SecondaryGateway,
    string RedisConnectionString)
{
    public static ProbeSettings FromEnvironment() => new(
        RequiredUri("CONDUIT_NATIVE_ADMIN_URL"),
        RequiredUri("CONDUIT_NATIVE_GATEWAY_URL"),
        RequiredUri("CONDUIT_NATIVE_GATEWAY_SECONDARY_URL"),
        Required("REDIS_URL").Replace("redis://", string.Empty, StringComparison.OrdinalIgnoreCase));

    private static Uri RequiredUri(string name) =>
        new($"{Required(name).TrimEnd('/')}/", UriKind.Absolute);

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required.");
}

internal sealed class GatewayNativeParityProbe(ProbeSettings settings)
{
    private static readonly string[] HubPaths =
    [
        "/hubs/video-generation",
        "/hubs/public/video-generation",
        "/hubs/image-generation",
        "/hubs/tasks",
        "/hubs/notifications",
        "/hubs/spend",
        "/hubs/webhooks",
        "/hubs/virtual-key-management"
    ];

    private readonly HttpClient _admin = CreateClient(settings.Admin);
    private readonly HttpClient _gateway = CreateClient(settings.Gateway);
    private readonly HttpClient _secondaryGateway = CreateClient(settings.SecondaryGateway);

    public async Task RunAsync()
    {
        await AssertRuntimeAndOperationsAsync();
        await AssertSignalRAndRedisAsync();
        Console.WriteLine("Gateway NativeAOT supported protocol/infrastructure probe: PASS");
    }

    private async Task AssertRuntimeAndOperationsAsync()
    {
        using var capabilitiesResponse = await _gateway.GetAsync("health/runtime-capabilities");
        await ExpectAsync(capabilitiesResponse, HttpStatusCode.OK, "runtime capabilities");
        using var capabilities = JsonDocument.Parse(await capabilitiesResponse.Content.ReadAsStringAsync());

        Equal("native-aot", GetString(capabilities.RootElement, "runtime_mode"), "native runtime mode");
        var protocols = GetProperty(capabilities.RootElement, "signal_r_protocols")
            .EnumerateArray().Select(value => value.GetString()).ToArray();
        True(protocols.SequenceEqual(["json"]), "native SignalR protocol is JSON-only");

        var exclusions = GetProperty(capabilities.RootElement, "excluded_features")
            .EnumerateArray().Select(value => value.GetString()).ToHashSet();
        True(exclusions.Contains("signalr-messagepack"), "MessagePack exclusion is observable");
        True(exclusions.Contains("ef-core-query-data-plane"), "EF query data-plane exclusion is observable");
        True(exclusions.Contains("authenticated-signalr-connections"),
            "authenticated SignalR exclusion is observable");

        await ExpectAsync(await _admin.GetAsync("health/live"), HttpStatusCode.OK, "Admin liveness");
        await ExpectAsync(await _gateway.GetAsync("health/live"), HttpStatusCode.OK, "Gateway liveness");
        await ExpectAsync(await _secondaryGateway.GetAsync("health/runtime-capabilities"),
            HttpStatusCode.OK, "secondary native Gateway");

        using var metrics = await _gateway.GetAsync("metrics");
        await ExpectAsync(metrics, HttpStatusCode.OK, "Prometheus metrics");
        True((await metrics.Content.ReadAsStringAsync()).Contains("# HELP", StringComparison.Ordinal),
            "Prometheus exposition body");

        await ExpectAsync(await _gateway.GetAsync("v1/models"), HttpStatusCode.Unauthorized,
            "unauthenticated data-plane request is rejected before EF");
    }

    private async Task AssertSignalRAndRedisAsync()
    {
        foreach (var gateway in new[] { _gateway, _secondaryGateway })
        {
            foreach (var hubPath in HubPaths)
            {
                using var response = await gateway.PostAsync($"{hubPath}/negotiate?negotiateVersion=1", null);
                True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Unauthorized,
                    $"JSON SignalR negotiate route {hubPath} is active");
            }
        }

        await using var redis = await ConnectionMultiplexer.ConnectAsync(settings.RedisConnectionString);
        var endpoint = redis.GetEndPoints().Single();
        var server = redis.GetServer(endpoint);
        var channels = server.SubscriptionChannels(
            new RedisChannel("conduit_signalr:*", RedisChannel.PatternMode.Pattern));
        True(channels.Length > 0, "Redis SignalR backplane subscriptions");
        True(await redis.GetDatabase().KeyExistsAsync("Conduit-DataProtection-Keys"),
            "Redis data-protection key ring");
    }

    private static HttpClient CreateClient(Uri baseAddress) => new()
    {
        BaseAddress = baseAddress,
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static async Task ExpectAsync(HttpResponseMessage response, HttpStatusCode expected, string name)
    {
        if (response.StatusCode != expected)
        {
            throw new InvalidOperationException(
                $"{name}: expected {(int)expected}, received {(int)response.StatusCode}: " +
                await response.Content.ReadAsStringAsync());
        }
        Console.WriteLine($"  PASS  {name}");
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }
        throw new InvalidOperationException($"Response did not contain '{name}': {element}");
    }

    private static string GetString(JsonElement element, string name) =>
        GetProperty(element, name).GetString()
        ?? throw new InvalidOperationException($"'{name}' was null.");

    private static void True(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {name}");
        }
        Console.WriteLine($"  PASS  {name}");
    }

    private static void Equal(string expected, string actual, string name) =>
        True(string.Equals(expected, actual, StringComparison.Ordinal), $"{name} ({actual})");
}
