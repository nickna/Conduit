using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Npgsql;
using NpgsqlTypes;

using StackExchange.Redis;

if (args.Length > 0 && args[0] == "--benchmark-workload")
{
    try
    {
        if (args.Length != 2) { throw new InvalidOperationException(); }
        Environment.ExitCode = await NativeBenchmarkWorkloadProbe.RunAsync(
            args[1], Console.OpenStandardOutput());
    }
    catch (Exception error)
    {
        Console.Error.WriteLine("Benchmark workload failed. " + NativeFixtureDiagnostics.Describe(error));
        Environment.ExitCode = 1;
    }
    return;
}

if (args is ["--seed"])
{
    await NativeParityFixture.SeedAsync(
        ProbeSettings.DatabaseConnectionStringFromEnvironment(),
        ProbeSettings.ProviderUriFromEnvironment(),
        ProbeSettings.RedisConnectionStringFromEnvironment());
    if (Environment.GetEnvironmentVariable("CONDUIT_NATIVE_FUNCTION_FIXTURE_OWNER") is not null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var source = NpgsqlDataSource.Create(ProbeSettings.DatabaseConnectionStringFromEnvironment());
        await NativeFunctionFixture.CreateAsync(source, ProbeSettings.FunctionFixtureOwnerFromEnvironment(),
            ProbeSettings.ProviderUriFromEnvironment(),
            Environment.GetEnvironmentVariable("CONDUIT_NATIVE_FUNCTION_PROTECTED_KEY")
                ?? throw new InvalidOperationException("A protected function fixture credential is required."),
            deadline.Token);
    }
    return;
}

if (args is ["--cleanup-function-fixture"] or ["--cleanup-mcp-function-fixture"])
{
    var mcp = args[0] == "--cleanup-mcp-function-fixture";
    var profile = mcp ? NativeFunctionFixtureProfile.Mcp : NativeFunctionFixtureProfile.Exa;
    var label = mcp ? "Owned MCP function fixture cleanup" : "Owned function fixture cleanup";
    try
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var source = NpgsqlDataSource.Create(ProbeSettings.DatabaseConnectionStringFromEnvironment());
        var owner = mcp ? ProbeSettings.McpFixtureOwnerFromEnvironment() : ProbeSettings.FunctionFixtureOwnerFromEnvironment();
        var snapshot = await NativeFunctionFixture.ReadAsync(source, owner, profile, deadline.Token);
        if (snapshot is not null)
        {
            // Hosts have already stopped. Keep the ownership rows until Redis
            // cleanup succeeds, so a failed cleanup can be retried safely.
            await NativeFunctionFixtureRedisCleanup.DeleteAsync(
                ProbeSettings.RedisConnectionStringFromEnvironment(), snapshot, deadline.Token);
            await NativeFunctionFixture.DeleteAsync(source, owner, profile, deadline.Token);
        }
        Console.WriteLine(label + ": PASS");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(label + " failed. " + NativeFixtureDiagnostics.Describe(error));
        Environment.ExitCode = 1;
    }
    return;
}

if (args is ["--admin-ip-filters"] or ["--cleanup-admin-ip-filters"])
{
    try
    {
        await using var source = NpgsqlDataSource.Create(ProbeSettings.DatabaseConnectionStringFromEnvironment());
        if (args[0] == "--admin-ip-filters")
            await NativeAdminIpFilterProbe.RunAsync(ProbeSettings.RequiredUri("CONDUIT_NATIVE_ADMIN_URL"), source,
                ProbeSettings.AdminIpOwnerFromEnvironment(), ProbeSettings.FunctionFixtureOwnerFromEnvironment(),
                ProbeSettings.AdminMasterKeyFromEnvironment(), CancellationToken.None);
        else
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await NativeAdminIpFilterProbe.CleanupAsync(source, ProbeSettings.AdminIpOwnerFromEnvironment(), deadline.Token);
            Console.WriteLine("Owned Admin IP-filter fixture cleanup: PASS");
        }
    }
    catch (Exception error)
    {
        Console.Error.WriteLine("Native Admin IP-filter operation failed. " + NativeFixtureDiagnostics.Describe(error));
        Environment.ExitCode = 1;
    }
    return;
}

if (args is ["--global-settings-cache-convergence"] or ["--cleanup-global-settings-cache-convergence"])
{
    try
    {
        await using var source = NpgsqlDataSource.Create(ProbeSettings.DatabaseConnectionStringFromEnvironment());
        if (args[0] == "--global-settings-cache-convergence")
            await NativeGlobalSettingsCacheConvergenceProbe.RunAsync(ProbeSettings.RequiredUri("CONDUIT_NATIVE_ADMIN_URL"),
                ProbeSettings.RequiredUri("CONDUIT_NATIVE_GATEWAY_URL"), ProbeSettings.RequiredUri("CONDUIT_NATIVE_GATEWAY_SECONDARY_URL"),
                ProbeSettings.ProviderUriFromEnvironment(), source, ProbeSettings.AdminSettingsOwnerFromEnvironment(),
                ProbeSettings.FunctionFixtureOwnerFromEnvironment(), ProbeSettings.AdminMasterKeyFromEnvironment(), CancellationToken.None);
        else
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await NativeGlobalSettingsCacheConvergenceProbe.CleanupAsync(source,
                ProbeSettings.AdminSettingsOwnerFromEnvironment(), deadline.Token);
            Console.WriteLine("Owned global settings cache-convergence fixture cleanup: PASS");
        }
    }
    catch (InvalidOperationException error) when (error.Message.StartsWith(
        "Native global settings cache convergence failed at ", StringComparison.Ordinal))
    {
        Console.Error.WriteLine(error.Message);
        Environment.ExitCode = 1;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine("Native global settings cache-convergence operation failed. " + NativeFixtureDiagnostics.Describe(error));
        Environment.ExitCode = 1;
    }
    return;
}

if (args is ["--cleanup-admin-global-settings"])
{
    try
    {
        await using var source = NpgsqlDataSource.Create(ProbeSettings.DatabaseConnectionStringFromEnvironment());
        var owner = ProbeSettings.AdminSettingsOwnerFromEnvironment();
        var failures = new List<Exception>();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await NativeGlobalSettingsCacheConvergenceProbe.CleanupAsync(source, owner, deadline.Token);
        }
        catch (Exception error) { failures.Add(error); }
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await NativeAdminGlobalSettingProbe.CleanupAsync(source, owner, deadline.Token);
        }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Owned Admin setting cleanup failed.", failures);
        Console.WriteLine("Owned Admin global-setting fixture cleanup: PASS");
    }
    catch (Exception error)
    {
        Console.Error.WriteLine("Owned Admin global-setting fixture cleanup failed. " + NativeFixtureDiagnostics.Describe(error));
        Environment.ExitCode = 1;
    }
    return;
}

if (args is ["--admin-global-settings"])
{
    try
    {
        await using var source = NpgsqlDataSource.Create(ProbeSettings.DatabaseConnectionStringFromEnvironment());
        await NativeAdminGlobalSettingProbe.RunAsync(ProbeSettings.RequiredUri("CONDUIT_NATIVE_ADMIN_URL"), source,
            ProbeSettings.AdminSettingsOwnerFromEnvironment(), ProbeSettings.AdminMasterKeyFromEnvironment(), CancellationToken.None);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine("Admin global-settings HTTP proof failed. " + NativeFixtureDiagnostics.Describe(exception));
        Environment.ExitCode = 1;
    }
    return;
}

if (args is ["--proxy-routes"])
{
    var database = Environment.GetEnvironmentVariable("DATABASE_URL")
        ?? throw new InvalidOperationException("DATABASE_URL is required.");
    var redis = NativeDependencyRoutes.ParseRedis(Environment.GetEnvironmentVariable("REDIS_URL"),
        Environment.GetEnvironmentVariable("CONDUIT_REDIS_CONNECTION_STRING"));
    var routes = new JsonObject
    {
        ["database"] = NativeDependencyRoutes.RedirectDatabaseUrl(database,
            int.Parse(Environment.GetEnvironmentVariable("CONDUIT_NATIVE_PROXY_POSTGRES_PORT")
                ?? throw new InvalidOperationException("CONDUIT_NATIVE_PROXY_POSTGRES_PORT is required."))),
        ["redis"] = NativeDependencyRoutes.RedirectRedis(redis,
            int.Parse(Environment.GetEnvironmentVariable("CONDUIT_NATIVE_PROXY_REDIS_PORT")
                ?? throw new InvalidOperationException("CONDUIT_NATIVE_PROXY_REDIS_PORT is required."))),
        ["directDatabase"] = NativeDependencyRoutes.ParseDatabase(database).ConnectionString,
        ["directRedis"] = redis.ToString(includePassword: true)
    };
    Console.WriteLine(routes.ToJsonString());
    return;
}

if (args is ["--dependency-proxies"])
{
    await NativeDependencyProxyHost.RunAsync();
    return;
}

if (args is ["--mock-provider"])
{
    await NativeProviderStub.RunAsync(ProbeSettings.ProviderUriFromEnvironment());
    return;
}

if (args.Length > 0 && args[0] is "--billing-audit-timer" or "--billing-audit-drain" or "--billing-audit-prepare" or
    "--billing-audit-tail" or "--billing-audit-verify-host" or "--billing-audit-verify" or "--cleanup-billing-audit-fixture")
{
    try
    {
        var perHost = args[0] is "--billing-audit-prepare" or "--billing-audit-tail" or "--billing-audit-verify-host";
        if (perHost ? args is not [_, "primary" or "secondary"] : args.Length != 1)
            throw new ArgumentException("Invalid native BillingAudit lifecycle arguments.");
        var host = perHost ? args[1] : null;
        await using var source = NpgsqlDataSource.Create(ProbeSettings.DatabaseConnectionStringFromEnvironment());
        var owner = ProbeSettings.BillingAuditOwnerFromEnvironment();
        switch (args[0])
        {
            case "--billing-audit-timer":
                await NativeBillingAuditLifecycleProbe.TimerAsync(ProbeSettings.RequiredUri("CONDUIT_NATIVE_GATEWAY_URL"),
                    ProbeSettings.RequiredUri("CONDUIT_NATIVE_GATEWAY_SECONDARY_URL"), ProbeSettings.ProviderUriFromEnvironment(),
                    source, owner, ProbeSettings.ProtectedFunctionKeyFromEnvironment());
                break;
            case "--billing-audit-prepare":
                await NativeBillingAuditLifecycleProbe.PrepareAsync(ProbeSettings.RequiredUri(host == "primary"
                    ? "CONDUIT_NATIVE_GATEWAY_URL" : "CONDUIT_NATIVE_GATEWAY_SECONDARY_URL"),
                    ProbeSettings.ProviderUriFromEnvironment(), source, owner, host!);
                break;
            case "--billing-audit-tail":
                await NativeBillingAuditLifecycleProbe.ArmTailAsync(ProbeSettings.RequiredUri(host == "primary"
                    ? "CONDUIT_NATIVE_GATEWAY_URL" : "CONDUIT_NATIVE_GATEWAY_SECONDARY_URL"),
                    ProbeSettings.ProviderUriFromEnvironment(), source, owner, host!);
                break;
            case "--billing-audit-verify-host":
                await NativeBillingAuditLifecycleProbe.VerifyHostAsync(source, owner, host!);
                break;
            case "--billing-audit-drain":
                await NativeBillingAuditLifecycleProbe.DrainAsync(ProbeSettings.RequiredUri("CONDUIT_NATIVE_GATEWAY_URL"),
                    ProbeSettings.RequiredUri("CONDUIT_NATIVE_GATEWAY_SECONDARY_URL"));
                break;
            case "--billing-audit-verify":
                await NativeBillingAuditLifecycleProbe.VerifyTailAsync(source, owner);
                break;
            case "--cleanup-billing-audit-fixture":
                await NativeBillingAuditLifecycleProbe.CleanupAsync(source, owner, ProbeSettings.RedisConnectionStringFromEnvironment());
                break;
        }
    }
    catch (Exception error)
    {
        Console.Error.WriteLine("Native BillingAudit lifecycle operation failed. " + NativeFixtureDiagnostics.Describe(error));
        Environment.ExitCode = 1;
    }
    return;
}

if (args is ["--function-execution"])
{
    try
    {
        await using var source = NpgsqlDataSource.Create(ProbeSettings.DatabaseConnectionStringFromEnvironment());
        await NativeFunctionExecutionProbe.RunAsync(ProbeSettings.RequiredUri("CONDUIT_NATIVE_GATEWAY_URL"),
            ProbeSettings.RequiredUri("CONDUIT_NATIVE_GATEWAY_SECONDARY_URL"), ProbeSettings.ProviderUriFromEnvironment(),
            source, ProbeSettings.FunctionFixtureOwnerFromEnvironment(), NativeParityFixture.VirtualKey, CancellationToken.None);
        await NativeFunctionExecutionProbe.RunMcpAsync(ProbeSettings.RequiredUri("CONDUIT_NATIVE_GATEWAY_URL"),
            ProbeSettings.RequiredUri("CONDUIT_NATIVE_GATEWAY_SECONDARY_URL"), source,
            ProbeSettings.McpFixtureOwnerFromEnvironment(), ProbeSettings.ProtectedFunctionKeyFromEnvironment(),
            ProbeSettings.FunctionFixtureOwnerFromEnvironment(), NativeParityFixture.VirtualKey, CancellationToken.None);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine("Function HTTP proof failed. " + NativeFixtureDiagnostics.Describe(exception));
        Environment.ExitCode = 1;
    }
    return;
}

var settings = ProbeSettings.FromEnvironment();
var probe = new GatewayNativeParityProbe(settings);
if (args is ["--verify-shutdown"] or ["--verify-shutdown", _])
{
    var measuredRequests = 5;
    if (args.Length == 2 && (!int.TryParse(args[1], out measuredRequests) || measuredRequests < 5))
        throw new ArgumentException("Shutdown accounting requires at least five measured requests.");
    await probe.AssertShutdownAccountingAsync(measuredRequests);
    return;
}
await probe.RunAsync();

internal sealed record ProbeSettings(
    Uri Admin,
    Uri Gateway,
    Uri SecondaryGateway,
    Uri Provider,
    string DatabaseConnectionString,
    string RedisConnectionString,
    Uri DependencyControl,
    string DependencyControlToken)
{
    public static ProbeSettings FromEnvironment() => new(
        RequiredUri("CONDUIT_NATIVE_ADMIN_URL"),
        RequiredUri("CONDUIT_NATIVE_GATEWAY_URL"),
        RequiredUri("CONDUIT_NATIVE_GATEWAY_SECONDARY_URL"),
        ProviderUriFromEnvironment(),
        DatabaseConnectionStringFromEnvironment(),
        RedisConnectionStringFromEnvironment(),
        RequiredUri("CONDUIT_NATIVE_PROXY_CONTROL_URL"),
        Required("CONDUIT_NATIVE_PROXY_TOKEN"));

    public static Uri ProviderUriFromEnvironment() =>
        RequiredUri("CONDUIT_NATIVE_PROVIDER_URL");

    public static Guid FunctionFixtureOwnerFromEnvironment() =>
        FixtureOwner("CONDUIT_NATIVE_FUNCTION_FIXTURE_OWNER");

    public static Guid McpFixtureOwnerFromEnvironment() => FixtureOwner("CONDUIT_NATIVE_MCP_FIXTURE_OWNER");

    public static Guid AdminSettingsOwnerFromEnvironment() => FixtureOwner("CONDUIT_NATIVE_ADMIN_SETTINGS_OWNER");

    public static Guid AdminIpOwnerFromEnvironment() => FixtureOwner("CONDUIT_NATIVE_ADMIN_IP_OWNER");

    public static Guid BillingAuditOwnerFromEnvironment() => FixtureOwner("CONDUIT_NATIVE_BILLING_AUDIT_FIXTURE_OWNER");

    public static string AdminMasterKeyFromEnvironment() => Required("CONDUIT_API_TO_API_BACKEND_AUTH_KEY");

    public static string ProtectedFunctionKeyFromEnvironment() => Required("CONDUIT_NATIVE_FUNCTION_PROTECTED_KEY");

    private static Guid FixtureOwner(string name) =>
        Guid.TryParseExact(Required(name), "N", out var owner)
            && owner != Guid.Empty
                ? owner
                : throw new InvalidOperationException("A nonempty function fixture owner GUID is required.");

    public static string RedisConnectionStringFromEnvironment() =>
        Environment.GetEnvironmentVariable("CONDUIT_NATIVE_DIRECT_REDIS_CONNECTION_STRING") is { Length: > 0 } direct
            ? direct
            : NativeDependencyRoutes.ParseRedis(Environment.GetEnvironmentVariable("REDIS_URL"),
                Environment.GetEnvironmentVariable("CONDUIT_REDIS_CONNECTION_STRING")).ToString(includePassword: true);

    public static string DatabaseConnectionStringFromEnvironment()
    {
        return Environment.GetEnvironmentVariable("CONDUIT_NATIVE_DIRECT_DATABASE_CONNECTION_STRING") is { Length: > 0 } direct
            ? direct : NativeDependencyRoutes.ParseDatabase(Required("DATABASE_URL")).ConnectionString;
    }

    public static Uri RequiredUri(string name) =>
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
    private readonly HttpClient _provider = CreateClient(settings.Provider);

    public async Task RunAsync()
    {
        await AssertRuntimeAndOperationsAsync();
        await AssertModelDiscoveryBoundaryAsync();
        await using (var source = NpgsqlDataSource.Create(settings.DatabaseConnectionString))
            await NativeAdminRuntimeProfileProbe.RunAsync(settings.Admin, settings.Provider, source,
                ProbeSettings.FunctionFixtureOwnerFromEnvironment(), settings.RedisConnectionString,
                ProbeSettings.AdminMasterKeyFromEnvironment(), CancellationToken.None);
        await AssertFunctionDiscoveryAsync();
        await AssertFunctionExecutionPreflightAsync();
        await AssertSchemaReadinessAsync();
        await AssertMetricAggregatesAsync();
        using (var control = CreateClient(settings.DependencyControl))
            await new NativeDependencyRecoveryProbe(_admin, _gateway, _secondaryGateway,
                control, settings.DependencyControlToken).RunAsync();
        await AssertAsyncTaskPersistenceAsync();
        await AssertProviderTransportAsync();
        await AssertChunkedProviderRequestAsync();
        await AssertS3MediaWorkflowAsync();
        await AssertSignalRAndRedisAsync();
        await using (var source = NpgsqlDataSource.Create(settings.DatabaseConnectionString))
        {
            await NativeGlobalSettingsCacheConvergenceProbe.RunAsync(settings.Admin, settings.Gateway, settings.SecondaryGateway,
                settings.Provider, source, ProbeSettings.AdminSettingsOwnerFromEnvironment(),
                ProbeSettings.FunctionFixtureOwnerFromEnvironment(), ProbeSettings.AdminMasterKeyFromEnvironment(), CancellationToken.None);
            await NativeAdminGlobalSettingProbe.RunAsync(settings.Admin, source,
                ProbeSettings.AdminSettingsOwnerFromEnvironment(), ProbeSettings.AdminMasterKeyFromEnvironment(), CancellationToken.None);
            await NativeFunctionExecutionProbe.RunAsync(settings.Gateway, settings.SecondaryGateway,
                settings.Provider, source, ProbeSettings.FunctionFixtureOwnerFromEnvironment(),
                NativeParityFixture.VirtualKey, CancellationToken.None);
            await NativeFunctionExecutionProbe.RunMcpAsync(settings.Gateway, settings.SecondaryGateway, source,
                ProbeSettings.McpFixtureOwnerFromEnvironment(), ProbeSettings.ProtectedFunctionKeyFromEnvironment(),
                ProbeSettings.FunctionFixtureOwnerFromEnvironment(), NativeParityFixture.VirtualKey, CancellationToken.None);
        }
        Console.WriteLine("Gateway NativeAOT supported protocol/infrastructure probe: PASS");
    }

    private async Task AssertMetricAggregatesAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var owner = Guid.NewGuid().ToString("N");
        var model = $"native_metric_model_{owner}";
        var provider = $"native_metrics_{owner}";
        Exception? assertionFailure = null;
        try
        {
            var inserted = await SetMetricRowsAsync(model, provider, present: true, deadline.Token);
            True(inserted == 2, $"two owned native metric request rows (inserted {inserted})");
            while (true)
            {
                var primary = await ReadMetricsAsync(_gateway, deadline.Token);
                var secondary = await ReadMetricsAsync(_secondaryGateway, deadline.Token);
                // Business collection is leader-elected; task collection runs on
                // every host. Never require one particular Gateway to win election.
                if ((NativeMetricSamples.HasBusinessAggregates(primary, model, provider) ||
                     NativeMetricSamples.HasBusinessAggregates(secondary, model, provider)) &&
                    NativeMetricSamples.HasPendingNotificationTasks(primary) &&
                    NativeMetricSamples.HasPendingNotificationTasks(secondary))
                    break;
                await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
            }
        }
        catch (Exception exception)
        {
            assertionFailure = exception;
            throw;
        }
        finally
        {
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await SetMetricRowsAsync(model, provider, present: false, cleanupDeadline.Token);
            }
            catch (Exception cleanupFailure)
            {
                var message = $"Could not remove owned metric fixture {owner}.";
                if (assertionFailure is not null)
                    throw new AggregateException(message, assertionFailure, cleanupFailure);
                throw new InvalidOperationException(message, cleanupFailure);
            }
        }
        Console.WriteLine("Native exact cost, latency, active-model and task-queue aggregates: PASS");
    }

    private static async Task<string> ReadMetricsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("metrics", cancellationToken);
        await ExpectAsync(response, HttpStatusCode.OK, "native metric aggregates");
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<int> SetMetricRowsAsync(
        string model, string provider, bool present, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(settings.DatabaseConnectionString);
        await using var command = dataSource.CreateCommand(present
            ? """
              INSERT INTO "RequestLogs" (
                  "VirtualKeyId", "ModelName", "ProviderType", "RequestType",
                  "InputTokens", "OutputTokens", "Cost", "ResponseTimeMs", "Timestamp", "BilledAtUtc",
                  "PromptCachingEligible", "PromptCachingPolicyApplied", "CachedReadSavings",
                  "CacheWritePremium", "RoutingAffinityUsed", "RoutingFailoverCount")
              SELECT key."Id", @model, @provider, 'native_metrics_probe',
                  0, 0, sample.cost, sample.latency, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP,
                  false, false, 0, 0, false, 0
              FROM "VirtualKeys" key
              JOIN "VirtualKeyGroups" group_row ON group_row."Id" = key."VirtualKeyGroupId"
              CROSS JOIN (VALUES (1.25, 100.0), (0.75, 300.0)) sample(cost, latency)
              WHERE key."KeyHash" = @keyHash AND group_row."ExternalGroupId" = @keyOwner
              """
            : """
              DELETE FROM "RequestLogs" log
              USING "VirtualKeys" key, "VirtualKeyGroups" group_row
              WHERE log."VirtualKeyId" = key."Id"
                  AND group_row."Id" = key."VirtualKeyGroupId"
                  AND key."KeyHash" = @keyHash AND group_row."ExternalGroupId" = @keyOwner
                  AND log."ModelName" = @model AND log."ProviderType" = @provider
                  AND log."RequestType" = 'native_metrics_probe'
              """);
        command.CommandTimeout = 10;
        command.Parameters.Add(new NpgsqlParameter<string>("model", NpgsqlDbType.Varchar) { TypedValue = model });
        command.Parameters.Add(new NpgsqlParameter<string>("provider", NpgsqlDbType.Varchar) { TypedValue = provider });
        command.Parameters.Add(new NpgsqlParameter<string>("keyHash", NpgsqlDbType.Varchar)
        { TypedValue = NativeParityFixture.VirtualKeyHash });
        command.Parameters.Add(new NpgsqlParameter<string>("keyOwner", NpgsqlDbType.Varchar)
        { TypedValue = NativeParityFixture.FixtureOwner });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task AssertSchemaReadinessAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await AssertHostHealthAsync(HttpStatusCode.OK, "current schema", deadline.Token);

        // This gate owns a disposable database. A committed, uniquely named future
        // version makes every host observe a mismatch without changing the schema.
        var marker = $"99991231235959_NativeAotReadiness_{Guid.NewGuid():N}";
        Exception? assertionFailure = null;
        try
        {
            await SetSchemaMarkerAsync(marker, present: true, deadline.Token);
            await AssertHostHealthAsync(HttpStatusCode.ServiceUnavailable,
                "future schema", deadline.Token);
        }
        catch (Exception exception)
        {
            assertionFailure = exception;
            throw;
        }
        finally
        {
            // Cleanup also runs after a timeout or a failed insert acknowledgement;
            // its fresh token allows removal even when the assertion was cancelled.
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await SetSchemaMarkerAsync(marker, present: false, cleanupDeadline.Token);
            }
            catch (Exception cleanupFailure)
            {
                var message = $"Could not remove owned readiness fixture {marker}.";
                if (assertionFailure is not null)
                    throw new AggregateException(message, assertionFailure, cleanupFailure);
                throw new InvalidOperationException(message, cleanupFailure);
            }
        }

        await AssertHostHealthAsync(HttpStatusCode.OK, "restored schema", deadline.Token);
        Console.WriteLine("Native live schema readiness and recovery: PASS");
    }

    private async Task SetSchemaMarkerAsync(string marker, bool present, CancellationToken cancellationToken)
    {
        await using var dataSource = NpgsqlDataSource.Create(settings.DatabaseConnectionString);
        await using var command = dataSource.CreateCommand(present
            ? """
              INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
              VALUES (@marker, 'native-readiness-probe')
              """
            : """
              DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = @marker
              """);
        command.CommandTimeout = 10;
        command.Parameters.Add(new NpgsqlParameter<string>("marker", NpgsqlDbType.Varchar)
        {
            TypedValue = marker
        });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task AssertHostHealthAsync(
        HttpStatusCode readiness, string stage, CancellationToken cancellationToken)
    {
        foreach (var (client, name) in new[]
                 { (_admin, "Admin"), (_gateway, "primary Gateway"), (_secondaryGateway, "secondary Gateway") })
        {
            using var ready = await client.GetAsync("health/ready", cancellationToken);
            await ExpectAsync(ready, readiness, $"{name} readiness with {stage}");
            using var live = await client.GetAsync("health/live", cancellationToken);
            await ExpectAsync(live, HttpStatusCode.OK, $"{name} liveness with {stage}");
        }
    }

    private async Task AssertRuntimeAndOperationsAsync()
    {
        string[] gatewayIncluded =
        [
            "postgresql-wolverine-persistence",
            "redis-signalr-backplane-and-rate-limits",
            "redis-data-protection",
            "json-signalr-negotiate-routing",
            "authenticated-signalr-json-connections",
            "redis-ephemeral-key-cache",
            "unauthenticated-request-rejection",
            "authenticated-model-discovery",
            "function-metadata-discovery",
            "inline-exa-search",
            "inline-stateless-mcp-tools",
            "nonstreaming-configured-exa-agentic-chat",
            "provider-http",
            "sse-streaming",
            "provider-error-translation",
            "provider-cancellation",
            "request-accounting-and-spend-settlement",
            "billing-audit-batch-writes",
            "async-task-persistence",
            "s3-media-api-workflows",
            "forwarded-headers",
            "readiness-and-database-health",
            "liveness-metrics-opentelemetry"
        ];
        string[] gatewayExcluded =
        [
            "signalr-messagepack",
            "ef-core-query-data-plane",
            "redis-virtual-key-authentication-cache",
            "inline-functions-beyond-exa-search-and-stateless-mcp",
            "agentic-workflows-beyond-nonstreaming-configured-exa-chat",
            "function-execution-list-http",
            "function-audit-and-retention",
            "billing-reconciliation",
            "request-log-reporting-and-retention",
            "pricing-rule-audit-and-retention",
            "model-discovery-catalog-and-warming",
            "billing-audit-reads-and-retention",
            "provider-hosted-tool-pricing"
        ];
        using var capabilityDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var (client, name) in new[]
                 { (_gateway, "primary Gateway"), (_secondaryGateway, "secondary Gateway") })
        {
            using var response = await client.GetAsync("health/runtime-capabilities", capabilityDeadline.Token);
            await ExpectAsync(response, HttpStatusCode.OK, $"{name} runtime capabilities");
            True(response.Content.Headers.ContentType?.MediaType == "application/json", $"{name} capability JSON content type");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(capabilityDeadline.Token));
            AssertCapabilityMetadata(body.RootElement, name, ["json"], gatewayIncluded, gatewayExcluded);
        }
        using (var response = await _admin.GetAsync("health/runtime-capabilities", capabilityDeadline.Token))
        {
            await ExpectAsync(response, HttpStatusCode.OK, "Admin runtime capabilities");
            True(response.Content.Headers.ContentType?.MediaType == "application/json", "Admin capability JSON content type");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(capabilityDeadline.Token));
            AssertCapabilityMetadata(body.RootElement, "Admin", [],
            [
                "postgresql-wolverine-persistence",
                "master-key-and-bearer-fallback-authentication",
                "typed-global-settings-http",
                "typed-ip-filter-http-and-enforcement",
                "typed-global-settings-cache",
                "typed-billing-audit-host",
                "release-owned-schema-wait",
                "readiness-and-database-health",
                "liveness-metrics-opentelemetry"
            ],
            [
                "ef-backed-admin-management",
                "pricing-tools-and-audit",
                "model-cost-canary",
                "startup-pricing-configuration-audit",
                "media-cleanup-and-reconciliation",
                "media-storage-startup-guard",
                "openrouter-metadata-sync",
                "database-count-operations-metrics",
                "billing-audit-reads-and-retention"
            ]);
        }

        await ExpectAsync(await _admin.GetAsync("health/live"), HttpStatusCode.OK, "Admin liveness");
        await ExpectAsync(await _gateway.GetAsync("health/live"), HttpStatusCode.OK, "Gateway liveness");

        using var metrics = await _gateway.GetAsync("metrics");
        await ExpectAsync(metrics, HttpStatusCode.OK, "Prometheus metrics");
        True((await metrics.Content.ReadAsStringAsync()).Contains("# HELP", StringComparison.Ordinal),
            "Prometheus exposition body");

        await ExpectAsync(await _gateway.GetAsync("v1/models"), HttpStatusCode.Unauthorized,
            "unauthenticated data-plane request is rejected before EF");

        foreach (var gateway in new[] { _gateway, _secondaryGateway })
        {
            using var listRequest = AuthorizedRequest(HttpMethod.Get, "v1/models");
            using var listResponse = await gateway.SendAsync(listRequest);
            await ExpectAsync(listResponse, HttpStatusCode.OK, "typed-store authenticated model list");
            var listBody = await listResponse.Content.ReadAsStringAsync();
            True(listBody.Contains(NativeParityFixture.ModelAlias, StringComparison.Ordinal),
                "model list contains seeded native alias");

            using var retrieveRequest = AuthorizedRequest(
                HttpMethod.Get,
                $"v1/models/{NativeParityFixture.ModelAlias}");
            using var retrieveResponse = await gateway.SendAsync(retrieveRequest);
            await ExpectAsync(retrieveResponse, HttpStatusCode.OK, "typed-store model retrieval");
        }

        using var metadataRequest = AuthorizedRequest(
            HttpMethod.Get,
            $"v1/conduit/models/{NativeParityFixture.ModelAlias}/metadata");
        using var metadataResponse = await _gateway.SendAsync(metadataRequest);
        await ExpectAsync(metadataResponse, HttpStatusCode.OK, "typed-store model capability metadata");
        using var metadataDocument = JsonDocument.Parse(
            await metadataResponse.Content.ReadAsStringAsync());
        var metadata = GetProperty(metadataDocument.RootElement, "metadata");
        var capabilitiesGraph = GetProperty(metadata, "capabilities");
        True(GetProperty(capabilitiesGraph, "chat").GetBoolean(),
            "model metadata contains effective chat capability");
    }

    private async Task AssertModelDiscoveryBoundaryAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var paths = new[]
        {
            "v1/conduit/discovery/models",
            $"v1/conduit/discovery/models/{Uri.EscapeDataString(NativeParityFixture.ModelAlias)}/parameters"
        };
        const string expectedBody = """
            {"error":{"message":"Model catalog discovery is not supported by the native-aot runtime profile.","type":"invalid_request_error","code":"not_implemented"}}
            """;
        var expectedBytes = Encoding.UTF8.GetBytes(expectedBody);
        var requests = 0;
        foreach (var gateway in new[] { _gateway, _secondaryGateway })
        {
            foreach (var path in paths)
            {
                using (var unauthenticated = new HttpRequestMessage(HttpMethod.Get, path))
                using (var response = await gateway.SendAsync(unauthenticated, deadline.Token))
                {
                    requests++;
                    await ExpectAsync(response, HttpStatusCode.Unauthorized,
                        "native model catalog rejects unauthenticated requests");
                }

                using var request = AuthorizedRequest(HttpMethod.Get, path);
                using var result = await gateway.SendAsync(request, deadline.Token);
                requests++;
                await ExpectAsync(result, HttpStatusCode.NotImplemented,
                    "native model catalog rejects the excluded EF resource");
                Equal("application/json", result.Content.Headers.ContentType?.MediaType ?? string.Empty,
                    "native model catalog generated error media type");
                Equal("utf-8", result.Content.Headers.ContentType?.CharSet ?? string.Empty,
                    "native model catalog generated error encoding");
                var bytes = await result.Content.ReadAsByteArrayAsync(deadline.Token);
                True(bytes.AsSpan().SequenceEqual(expectedBytes),
                    "native model catalog exact generated error bytes on both hosts and routes");
                using var document = JsonDocument.Parse(bytes);
                var body = document.RootElement;
                True(body.EnumerateObject().Select(property => property.Name).SequenceEqual(new[] { "error" }),
                    "native model catalog exact error root fields");
                var error = body.GetProperty("error");
                True(error.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                        .SequenceEqual(new[] { "code", "message", "type" }),
                    "native model catalog exact generated error fields");
                Equal("not_implemented", error.GetProperty("code").GetString()!,
                    "native model catalog error code");
                Equal("invalid_request_error", error.GetProperty("type").GetString()!,
                    "native model catalog error type");
                Equal("Model catalog discovery is not supported by the native-aot runtime profile.",
                    error.GetProperty("message").GetString()!, "native model catalog error message");
            }
        }
        True(requests == 8, "native model catalog exact eight-request boundary proof");
        Console.WriteLine("Native model catalog boundary on both Gateways: PASS");
    }

    private static void AssertCapabilityMetadata(JsonElement body, string host, string[] expectedProtocols,
        string[] expectedIncluded, string[] expectedExcluded)
    {
        True(body.ValueKind == JsonValueKind.Object, $"{host} capability object");
        True(body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(
                new[] { "excluded_features", "included_features", "persistence_status", "runtime_mode", "signal_r_protocols" }),
            $"{host} exact five snake_case capability fields");
        Equal("native-aot", body.GetProperty("runtime_mode").GetString()!, $"{host} native runtime mode");
        Equal("typed-npgsql-selected-runtime-slices", body.GetProperty("persistence_status").GetString()!,
            $"{host} selected typed persistence status");
        var protocols = CapabilityStrings(body, "signal_r_protocols", host);
        True(protocols.SequenceEqual(expectedProtocols), $"{host} exact SignalR protocols");
        var included = CapabilityStrings(body, "included_features", host).ToHashSet(StringComparer.Ordinal);
        var excluded = CapabilityStrings(body, "excluded_features", host).ToHashSet(StringComparer.Ordinal);
        True(included.SetEquals(expectedIncluded), $"{host} complete included capability set");
        True(excluded.SetEquals(expectedExcluded), $"{host} complete excluded capability set");
        True(!included.Overlaps(excluded), $"{host} capability sets do not overlap");
    }

    private static string[] CapabilityStrings(JsonElement body, string property, string host)
    {
        var values = body.GetProperty(property);
        True(values.ValueKind == JsonValueKind.Array, $"{host} {property} array");
        var entries = values.EnumerateArray().ToArray();
        True(entries.All(value => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())),
            $"{host} {property} contains only nonempty strings");
        var result = entries.Select(value => value.GetString()!).ToArray();
        True(result.Distinct(StringComparer.Ordinal).Count() == result.Length, $"{host} {property} contains no duplicates");
        return result;
    }

    private async Task AssertFunctionDiscoveryAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int[]? primaryIds = null;
        const string catalogPath = "v1/conduit/discovery/functions";
        foreach (var client in new[] { _gateway, _secondaryGateway })
        {
            using (var unauthenticated = await client.GetAsync(catalogPath, deadline.Token))
                await ExpectAsync(unauthenticated, HttpStatusCode.Unauthorized, "unauthenticated function catalog");
            using (var unauthenticated = await client.GetAsync(
                       $"{catalogPath}/{NativeParityFixture.SearchFunctionId}/parameters", deadline.Token))
                await ExpectAsync(unauthenticated, HttpStatusCode.Unauthorized, "unauthenticated function parameters");
            var (raw, catalog) = await ReadFunctionDiscoveryAsync(client, catalogPath, deadline.Token);
            var (repeated, _) = await ReadFunctionDiscoveryAsync(client, catalogPath, deadline.Token);
            Equal(raw, repeated, "function catalog repeated wire shape");
            var functions = catalog.GetProperty("functions").EnumerateArray().ToArray();
            True(catalog.GetProperty("count").GetInt32() == functions.Length, "function catalog count");
            var ids = functions.Select(function => function.GetProperty("id").GetInt32()).Order().ToArray();
            True(new[] { NativeParityFixture.SearchFunctionId, NativeParityFixture.AnswerFunctionId,
                NativeParityFixture.UnknownFunctionId }.All(ids.Contains), "enabled seeded functions discovered");
            True(!ids.Contains(NativeParityFixture.DisabledFunctionId) &&
                functions.All(function => function.GetProperty("is_enabled").GetBoolean()), "disabled functions omitted");
            True(!raw.Contains("native-private-settings", StringComparison.Ordinal) &&
                !raw.Contains("https://private.example.test", StringComparison.Ordinal), "private function fields omitted");
            var search = functions.Single(function => function.GetProperty("id").GetInt32() == NativeParityFixture.SearchFunctionId);
            True(search.GetProperty("configuration_name").GetString() == "Native Search 雪" &&
                search.GetProperty("provider_type").GetString() == "Exa" && search.GetProperty("purpose").GetString() == "Search" &&
                search.GetProperty("default_execution_mode").GetString() == "Synchronous" &&
                search.GetProperty("timeout_seconds").GetInt32() == 41 && search.GetProperty("description").GetString() == "" &&
                search.GetProperty("parameter_schema").GetProperty("example").GetProperty("query").GetString() == "雪",
                "catalog scalar and structured-schema wire contract");
            True(functions.Single(function => function.GetProperty("id").GetInt32() == NativeParityFixture.UnknownFunctionId)
                .GetProperty("default_execution_mode").GetString() == "123", "unknown execution-mode wire string");
            if (primaryIds is null) primaryIds = ids;
            else True(primaryIds.SequenceEqual(ids), "cross-host function catalog set");
            foreach (var filter in new[]
                     {
                         ("?purpose=search&providerType=exa", "Search", "Exa"),
                         ("?purpose=1&providerType=1", "Search", "Exa"),
                         ("?purpose=-9&providerType=197", "-9", "197"),
                         ("?purpose=999&providerType=999", "999", "999")
                     })
            {
                var (_, filtered) = await ReadFunctionDiscoveryAsync(client, catalogPath + filter.Item1, deadline.Token);
                var filteredFunctions = filtered.GetProperty("functions").EnumerateArray().ToArray();
                var expected = functions.Where(function => function.GetProperty("purpose").GetString() == filter.Item2 &&
                        function.GetProperty("provider_type").GetString() == filter.Item3)
                    .Select(function => function.GetProperty("id").GetInt32()).Order();
                True(expected.SequenceEqual(filteredFunctions.Select(function => function.GetProperty("id").GetInt32()).Order()),
                    "named/numeric function filters");
                True(filtered.GetProperty("count").GetInt32() == filteredFunctions.Length, "filtered catalog count");
            }
            var (_, ignored) = await ReadFunctionDiscoveryAsync(client,
                catalogPath + "?purpose=not-a-purpose&providerType=not-a-provider", deadline.Token);
            True(ids.SequenceEqual(ignored.GetProperty("functions").EnumerateArray()
                .Select(function => function.GetProperty("id").GetInt32()).Order()), "invalid textual filters ignored");
            var parameterPath = $"{catalogPath}/{NativeParityFixture.SearchFunctionId}/parameters";
            var (parameterRaw, parameters) = await ReadFunctionDiscoveryAsync(client, parameterPath, deadline.Token);
            var (parameterHit, _) = await ReadFunctionDiscoveryAsync(client, parameterPath, deadline.Token);
            Equal(parameterRaw, parameterHit, "function parameters repeated wire shape");
            True(parameters.GetProperty("function_id").GetInt32() == NativeParityFixture.SearchFunctionId &&
                parameters.GetProperty("parameter_schema").ValueKind == JsonValueKind.Object &&
                parameters.GetProperty("parameter_schema").GetProperty("properties").GetProperty("query")
                    .GetProperty("type").GetString() == "string" &&
                parameters.GetProperty("example_request").GetProperty("query").GetString() == "雪",
                "structured schema and example");
            var (_, arraySchema) = await ReadFunctionDiscoveryAsync(client,
                $"{catalogPath}/{NativeParityFixture.AnswerFunctionId}/parameters", deadline.Token);
            var value = arraySchema.GetProperty("parameter_schema").GetProperty("value");
            True(value.ValueKind == JsonValueKind.Array && value[0].GetInt32() == 0 &&
                !value[1].GetBoolean() && value[2].GetProperty("snow").GetString() == "雪", "non-object schema wrapping");
            var (_, unknown) = await ReadFunctionDiscoveryAsync(client,
                $"{catalogPath}/{NativeParityFixture.UnknownFunctionId}/parameters", deadline.Token);
            True(unknown.GetProperty("provider_type").GetString() == "197" &&
                unknown.GetProperty("purpose").GetString() == "-9" &&
                !unknown.GetProperty("parameter_schema").EnumerateObject().Any(), "unknown enum strings and null schema");
            foreach (var missing in new[] { NativeParityFixture.DisabledFunctionId, int.MinValue })
            {
                using var request = AuthorizedRequest(HttpMethod.Get, $"{catalogPath}/{missing}/parameters");
                using var response = await client.SendAsync(request, deadline.Token);
                await ExpectAsync(response, HttpStatusCode.NotFound, "disabled/missing function parameters");
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                Equal("not_found", error.RootElement.GetProperty("error").GetProperty("code").GetString()!,
                    "function parameter error contract");
            }
        }
        Console.WriteLine("Native authenticated function discovery: PASS");
    }

    private async Task AssertFunctionExecutionPreflightAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        const string path = "v1/conduit/functions/execute";
        foreach (var client in new[] { _gateway, _secondaryGateway })
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, path))
            {
                request.Content = new StringContent(
                    $$$"""{"function_configuration_id":{{{NativeParityFixture.SearchFunctionId}}},"parameters":{}}""",
                    Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, deadline.Token);
                await ExpectAsync(response, HttpStatusCode.Unauthorized, "unauthenticated function execution");
            }

            foreach (var (id, status, code, type, message) in new[]
                     {
                         (int.MinValue, HttpStatusCode.NotFound, "not_found", "not_found_error",
                             $"Function configuration {int.MinValue} not found"),
                         (NativeParityFixture.DisabledFunctionId, HttpStatusCode.BadRequest, "invalid_request",
                             "invalid_request_error", $"Function configuration {NativeParityFixture.DisabledFunctionId} is disabled"),
                         (NativeParityFixture.SearchFunctionId, HttpStatusCode.BadRequest, "invalid_request",
                             "invalid_request_error", "Parameter validation failed: Missing required parameter: 'query'")
                     })
            {
                using var request = AuthorizedRequest(HttpMethod.Post, path);
                request.Content = new StringContent(
                    $$$"""{"function_configuration_id":{{{id}}},"parameters":{}}""",
                    Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, deadline.Token);
                await ExpectAsync(response, status, $"native function execution preflight ({id})");
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                var error = document.RootElement.GetProperty("error");
                Equal(code, error.GetProperty("code").GetString()!, "function execution preflight error code");
                Equal(type, error.GetProperty("type").GetString()!, "function execution preflight error type");
                Equal(message, error.GetProperty("message").GetString()!, "function execution preflight error message");
            }
        }
        Console.WriteLine("Native function execution preflight on both Gateways: PASS");
    }

    private static async Task<(string Raw, JsonElement Payload)> ReadFunctionDiscoveryAsync(
        HttpClient client, string path, CancellationToken token)
    {
        using var request = AuthorizedRequest(HttpMethod.Get, path);
        using var response = await client.SendAsync(request, token);
        await ExpectAsync(response, HttpStatusCode.OK, "authenticated function discovery");
        var raw = await response.Content.ReadAsStringAsync(token);
        using var document = JsonDocument.Parse(raw);
        return (raw, document.RootElement.Clone());
    }

    private async Task AssertAsyncTaskPersistenceAsync()
    {
        using (var request = AuthorizedRequest(
                   HttpMethod.Get,
                   $"v1/conduit/tasks/{NativeParityFixture.TaskId}"))
        using (var response = await _gateway.SendAsync(request))
        {
            await ExpectAsync(response, HttpStatusCode.OK, "native async-task read");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Equal(
                NativeParityFixture.TaskId,
                GetString(document.RootElement, "task_id"),
                "native async-task identity");
            Equal("pending", GetString(document.RootElement, "state"),
                "native async-task initial state");
        }

        using (var request = AuthorizedRequest(
                   HttpMethod.Post,
                   $"v1/conduit/tasks/{NativeParityFixture.TaskId}/cancel"))
        using (var response = await _secondaryGateway.SendAsync(request))
        {
            await ExpectAsync(response, HttpStatusCode.NoContent, "native async-task cancellation");
        }

        using (var request = AuthorizedRequest(
                   HttpMethod.Get,
                   $"v1/conduit/tasks/{NativeParityFixture.TaskId}"))
        using (var response = await _gateway.SendAsync(request))
        {
            await ExpectAsync(response, HttpStatusCode.OK, "native async-task updated read");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Equal("cancelled", GetString(document.RootElement, "state"),
                "native async-task cancellation state");
        }

        await using var dataSource = NpgsqlDataSource.Create(settings.DatabaseConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "State", "CompletedAt" IS NOT NULL, "LeasedBy" IS NULL,
                "LeaseExpiryTime" IS NULL
            FROM "AsyncTasks" WHERE "Id" = @taskId
            """;
        command.Parameters.AddWithValue(
            "taskId",
            NpgsqlDbType.Varchar,
            NativeParityFixture.TaskId);
        await using var reader = await command.ExecuteReaderAsync();
        True(await reader.ReadAsync(), "native async-task row persists");
        True(reader.GetInt32(0) == 4 && reader.GetBoolean(1) &&
             reader.GetBoolean(2) && reader.GetBoolean(3),
            "native async-task cancellation is durable");
    }

    private async Task AssertProviderTransportAsync()
    {
        using (var request = AuthorizedChatRequest("native-nonstream", stream: false))
        using (var response = await _gateway.SendAsync(request))
        {
            await ExpectAsync(response, HttpStatusCode.OK, "native provider HTTP chat completion");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Equal(
                "chatcmpl-native-nonstream",
                GetString(document.RootElement, "id"),
                "provider response reaches the native Gateway");
            var usage = GetProperty(document.RootElement, "usage");
            True(GetProperty(usage, "prompt_tokens").GetInt32() == 10,
                "non-stream provider usage is preserved");
        }

        await AssertProviderStreamAsync(includeUsage: true, enhancedStream: false);
        await AssertProviderStreamAsync(includeUsage: false, enhancedStream: false);
        await AssertProviderStreamAsync(includeUsage: false, enhancedStream: true);

        await AssertDurableAccountingAsync();

        using (var request = AuthorizedChatRequest("native-error", stream: false))
        using (var response = await _gateway.SendAsync(request))
        {
            await ExpectAsync(response, HttpStatusCode.BadRequest, "native provider error translation");
            var body = await response.Content.ReadAsStringAsync();
            True(body.Contains("provider_request_error", StringComparison.Ordinal),
                "provider 400 is classified for the OpenAI-compatible client");
            True(!body.Contains("native upstream private diagnostic", StringComparison.Ordinal) &&
                 !body.Contains("native_bad_prompt", StringComparison.Ordinal),
                "external error mode does not expose provider diagnostics");
        }

        await AssertProviderCancellationAsync();

        var providerState = await GetProviderStateAsync();
        True(providerState.NonStreamingRequests == 1, "mock provider observed non-stream HTTP");
        True(providerState.StreamingRequests == 3, "mock provider observed all three SSE dialect/usage requests");
        True(providerState.ErrorRequests == 1, "mock provider observed translated error request");
        True(providerState.AuthorizationFailures == 0, "provider credential was supplied correctly");
        True(providerState.CancellationsObserved >= 1, "provider observed upstream cancellation");
    }

    private async Task AssertChunkedProviderRequestAsync()
    {
        // Run after the Gateway transport counters have been checked. This direct
        // fixture request adds an error request without generating Gateway usage.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var payload = JsonDocument.Parse("""
            {"model":"native-aot-provider-model","messages":[{"role":"user","content":"native-error chunked fixture"}],"stream":false}
            """);
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = JsonContent.Create(payload.RootElement, NativeProbeJsonContext.Default.JsonElement)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", NativeProviderStub.ApiKey);
        request.Headers.TransferEncodingChunked = true;
        using var response = await _provider.SendAsync(request, deadline.Token);
        await ExpectAsync(response, HttpStatusCode.BadRequest, "native provider fixture reads chunked HTTP/1.1 JSON");
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
        var error = GetProperty(result.RootElement, "error");
        Equal("native upstream private diagnostic", GetString(error, "message"),
            "chunked provider fixture preserves the request body");
        Equal("native_bad_prompt", GetString(error, "code"),
            "chunked provider fixture selects the expected error branch");
    }

    private async Task AssertProviderStreamAsync(bool includeUsage, bool enhancedStream)
    {
        var mode = enhancedStream ? "enhanced" : includeUsage ? "standard with usage" : "standard without usage";
        using var request = AuthorizedChatRequest(
            "native-stream", stream: true, includeUsage, enhancedStream);
        using var response = await _gateway.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await ExpectAsync(response, HttpStatusCode.OK, $"native provider SSE chat completion ({mode})");
        Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType ?? string.Empty,
            $"SSE content type ({mode})");
        var body = await response.Content.ReadAsStringAsync();
        True(body.Contains("native ", StringComparison.Ordinal) &&
             body.Contains("stream response", StringComparison.Ordinal),
            $"SSE content chunks are forwarded ({mode})");
        True(body.TrimEnd().EndsWith("data: [DONE]", StringComparison.Ordinal),
            $"SSE completion sentinel is last ({mode})");

        var chunks = new List<JsonElement>();
        foreach (var payload in body.Split('\n')
            .Where(line => line.StartsWith("data: {", StringComparison.Ordinal))
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line[6..]);
                return document.RootElement.Clone();
            }))
        {
            var isChunk = payload.TryGetProperty("object", out var discriminator) &&
                discriminator.GetString() == "chat.completion.chunk";
            if (!enhancedStream)
                True(isChunk, $"standard SSE payload is an OpenAI chat chunk ({mode})");
            if (isChunk) chunks.Add(payload);
        }

        var usageChunks = chunks.Where(chunk =>
            chunk.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object).ToArray();
        if (includeUsage || enhancedStream)
        {
            True(usageChunks.Length == 1, $"provider usage appears in exactly one chat chunk ({mode})");
            var usageChunk = usageChunks.Single();
            var usage = GetProperty(usageChunk, "usage");
            True(GetProperty(usage, "prompt_tokens").GetInt32() == 10 &&
                 GetProperty(usage, "completion_tokens").GetInt32() == 5 &&
                 GetProperty(usage, "total_tokens").GetInt32() == 15,
                $"stream provider usage is preserved ({mode})");
            True(GetProperty(usageChunk, "choices").GetArrayLength() == 0,
                $"stream usage chunk has empty choices ({mode})");
            True(GetProperty(chunks[^1], "usage").ValueKind == JsonValueKind.Object,
                $"usage is the final chat chunk ({mode})");
        }
        else
        {
            True(usageChunks.Length == 0, "standard SSE omits usage when not requested");
        }

        if (enhancedStream)
            True(body.Contains("event: metrics-final", StringComparison.Ordinal),
                "Conduit SSE retains final metrics");
        else
            True(!body.Contains("event:", StringComparison.Ordinal),
                $"standard SSE excludes Conduit named events ({mode})");
    }

    private async Task AssertProviderCancellationAsync()
    {
        using var request = AuthorizedChatRequest("native-cancel", stream: true);
        using var response = await _gateway.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);
        await ExpectAsync(response, HttpStatusCode.OK, "cancellable native provider stream starts");

        await using (var body = await response.Content.ReadAsStreamAsync())
        {
            var buffer = new byte[1024];
            var bytesRead = await body.ReadAsync(buffer);
            True(bytesRead > 0, "cancellable stream reaches the downstream client");
            await WaitForProviderStateAsync(
                state => state.ActiveCancellationRequests == 1,
                "provider cancellation request becomes active");
        }

        response.Dispose();
        await WaitForProviderStateAsync(
            state => state.CancellationsObserved >= 1,
            "downstream disconnect cancels the provider HTTP stream");
    }

    private async Task AssertS3MediaWorkflowAsync()
    {
        using (var request = AuthorizedRequest(HttpMethod.Post, "v1/images/generations"))
        {
            request.Content = new StringContent(
                $$"""
                  {"model":"{{NativeParityFixture.ModelAlias}}","prompt":"{{NativeParityFixture.MediaPrompt}}","n":1,"size":"1024x1024","response_format":"url"}
                  """,
                Encoding.UTF8,
                "application/json");
            using var response = await _gateway.SendAsync(request);
            await ExpectAsync(response, HttpStatusCode.OK,
                "native provider image generation and S3 storage");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var image = GetProperty(document.RootElement, "data").EnumerateArray().Single();
            True(!string.IsNullOrWhiteSpace(GetString(image, "url")),
                "stored image response contains an S3 URL");
        }

        var media = await ReadMediaSnapshotAsync();
        True(media.StorageKey.Contains('/', StringComparison.Ordinal),
            "native media record contains the generated S3 key");
        Equal("image/png", media.ContentType, "native media content type persists");
        Equal("native-aot-provider-model", media.Model, "native media model persists");
        True(media.SizeBytes == NativeProviderStub.ImageBytes.Length,
            "native media byte count persists");
        True(media.IsActive, "native media row is active");

        using (var request = AuthorizedRequest(
                   HttpMethod.Get,
                   $"v1/conduit/downloads/metadata/{media.StorageKey}"))
        using (var response = await _secondaryGateway.SendAsync(request))
        {
            await ExpectAsync(response, HttpStatusCode.OK,
                "cross-host authenticated S3 metadata with typed ownership");
            True((await response.Content.ReadAsStringAsync()).Contains(
                    "image/png",
                    StringComparison.OrdinalIgnoreCase),
                "S3 metadata preserves the image content type");
        }

        using (var request = AuthorizedRequest(
                   HttpMethod.Get,
                   $"v1/conduit/downloads/{media.StorageKey}"))
        using (var response = await _secondaryGateway.SendAsync(request))
        {
            await ExpectAsync(response, HttpStatusCode.OK,
                "cross-host authenticated S3 download with typed ownership");
            True((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(
                    NativeProviderStub.ImageBytes),
                "authenticated S3 download preserves provider image bytes");
        }

        using (var response = await _gateway.GetAsync($"v1/conduit/media/{media.StorageKey}"))
        {
            await ExpectAsync(response, HttpStatusCode.OK, "native S3 media serving endpoint");
            True((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(
                    NativeProviderStub.ImageBytes),
                "media serving endpoint preserves provider image bytes");
        }

        var providerState = await GetProviderStateAsync();
        True(providerState.ImageRequests == 1,
            "mock provider observed image-generation HTTP with credentials");
    }

    private async Task<MediaSnapshot> ReadMediaSnapshotAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(settings.DatabaseConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT media."StorageKey", media."ContentType", media."SizeBytes",
                media."Model", media."DeletedAt" IS NULL
            FROM "MediaRecords" media
            JOIN "VirtualKeys" key ON key."Id" = media."VirtualKeyId"
            WHERE key."KeyHash" = @keyHash AND media."Prompt" = @prompt
            ORDER BY media."CreatedAt" DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("keyHash", NpgsqlDbType.Varchar, NativeParityFixture.VirtualKeyHash);
        command.Parameters.AddWithValue("prompt", NpgsqlDbType.Text, NativeParityFixture.MediaPrompt);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Native image workflow did not persist a media record.");
        }

        return new MediaSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetBoolean(4));
    }

    private async Task AssertDurableAccountingAsync(int measuredRequests = 4)
    {
        const decimal singleRequestCost = 0.000075m;
        var expectedCost = singleRequestCost * measuredRequests;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        AccountingSnapshot? latest = null;

        while (DateTime.UtcNow < deadline)
        {
            latest = await ReadAccountingSnapshotAsync();
            if (latest.MatchingRequestLogs == measuredRequests &&
                latest.RequestLogCost == expectedCost &&
                latest.LifetimeSpent == expectedCost &&
                latest.LedgerDebits == expectedCost &&
                latest.Balance == 100m - expectedCost)
            {
                True(true, "native request logs persist provider usage and calculated cost");
                True(true, "native batch spend settles balance, lifetime spend, and ledger atomically");
                return;
            }

            await Task.Delay(250);
        }

        throw new InvalidOperationException(
            "Native accounting did not settle before the timeout. " +
            $"Latest state: {latest}");
    }

    public async Task AssertShutdownAccountingAsync(int measuredRequests = 5)
    {
        // This mode runs after both Gateway processes have gracefully exited.
        // An exit code alone cannot establish that accepted spend was preserved.
        // The four completed requests and canceled stream each report the same
        // deterministic raw usage. Interrupted usage must survive cancellation
        // and shutdown as a fifth measured debit, without estimating this fixture.
        // The harness also counts completed benchmark requests and their warmups.
        await AssertDurableAccountingAsync(measuredRequests);
        Console.WriteLine("Gateway NativeAOT accounting after graceful shutdown: PASS");
    }

    private async Task<AccountingSnapshot> ReadAccountingSnapshotAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(settings.DatabaseConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*) FILTER (
                    WHERE rl."ModelName" = @alias
                      AND rl."InputTokens" = 10
                      AND rl."OutputTokens" = 5),
                COALESCE(SUM(rl."Cost") FILTER (
                    WHERE rl."ModelName" = @alias
                      AND rl."InputTokens" = 10
                      AND rl."OutputTokens" = 5), 0),
                g."Balance",
                g."LifetimeSpent",
                COALESCE((
                    SELECT SUM(t."Amount")
                    FROM "VirtualKeyGroupTransactions" t
                    WHERE t."VirtualKeyGroupId" = g."Id"
                      AND t."TransactionType" = 2
                      AND t."IsDeleted" = false
                ), 0)
            FROM "VirtualKeyGroups" g
            JOIN "VirtualKeys" k ON k."VirtualKeyGroupId" = g."Id"
            LEFT JOIN "RequestLogs" rl ON rl."VirtualKeyId" = k."Id"
            WHERE g."ExternalGroupId" = @owner
            GROUP BY g."Id", g."Balance", g."LifetimeSpent"
            """;
        command.Parameters.AddWithValue("alias", NpgsqlDbType.Varchar, NativeParityFixture.ModelAlias);
        command.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, NativeParityFixture.FixtureOwner);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Native accounting fixture group was not found.");
        }

        return new AccountingSnapshot(
            reader.GetInt64(0),
            reader.GetDecimal(1),
            reader.GetDecimal(2),
            reader.GetDecimal(3),
            reader.GetDecimal(4));
    }

    private async Task WaitForProviderStateAsync(
        Func<ProviderState, bool> predicate,
        string name)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        ProviderState latest = default;
        while (DateTime.UtcNow < deadline)
        {
            latest = await GetProviderStateAsync();
            if (predicate(latest))
            {
                True(true, name);
                return;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException($"Assertion timed out: {name}. Latest provider state: {latest}");
    }

    private async Task<ProviderState> GetProviderStateAsync()
    {
        using var response = await _provider.GetAsync("probe/state");
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                "Mock provider state endpoint returned " +
                $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new ProviderState(
            GetProperty(document.RootElement, "non_stream_requests").GetInt32(),
            GetProperty(document.RootElement, "stream_requests").GetInt32(),
            GetProperty(document.RootElement, "image_requests").GetInt32(),
            GetProperty(document.RootElement, "error_requests").GetInt32(),
            GetProperty(document.RootElement, "authorization_failures").GetInt32(),
            GetProperty(document.RootElement, "active_cancellation_requests").GetInt32(),
            GetProperty(document.RootElement, "cancellations_observed").GetInt32());
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

                using var authenticatedRequest = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{hubPath}/negotiate?negotiateVersion=1");
                authenticatedRequest.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer",
                    NativeParityFixture.VirtualKey);
                using var authenticatedResponse = await gateway.SendAsync(authenticatedRequest);
                await ExpectAsync(
                    authenticatedResponse,
                    HttpStatusCode.OK,
                    $"typed-store authentication and IP policy for {hubPath}");
            }
        }

        await AssertAuthenticatedSignalRConnectionsAsync();
        await AssertPublicSignalRConnectionAsync();
        await AssertWolverineMediaNotificationsAsync();

        await using var redis = await ConnectionMultiplexer.ConnectAsync(settings.RedisConnectionString);
        var database = redis.GetDatabase();
        var endpoint = redis.GetEndPoints().Single();
        var server = redis.GetServer(endpoint);
        var channels = server.SubscriptionChannels(
            new RedisChannel("conduit_signalr:*", RedisChannel.PatternMode.Pattern));
        True(channels.Length > 0, "Redis SignalR backplane subscriptions");
        True(await database.KeyExistsAsync("Conduit-DataProtection-Keys"),
            "Redis data-protection key ring");
        True(await database.KeyExistsAsync($"signalr:vk:{NativeParityFixture.VirtualKeyHash}:rpm"),
            "Redis SignalR per-minute method rate limit");
        True(await database.KeyExistsAsync($"signalr:vk:{NativeParityFixture.VirtualKeyHash}:rpd"),
            "Redis SignalR per-day method rate limit");
        await WaitForSignalRConnectionCountAsync(
            database,
            expected: 0,
            "Redis SignalR connection count returns to zero after disconnects");
    }

    private async Task AssertAuthenticatedSignalRConnectionsAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using (var primary = await NativeSignalRClient.ConnectAsync(
                         settings.Gateway,
                         "/hubs/notifications",
                         NativeParityFixture.VirtualKey,
                         timeout.Token))
        await using (var secondary = await NativeSignalRClient.ConnectAsync(
                         settings.SecondaryGateway,
                         "/hubs/notifications",
                         NativeParityFixture.VirtualKey,
                         timeout.Token))
        {
            True(true, "authenticated JSON SignalR clients connect to both native Gateways");
            await using var redis = await ConnectionMultiplexer.ConnectAsync(
                settings.RedisConnectionString);
            await WaitForSignalRConnectionCountAsync(
                redis.GetDatabase(),
                expected: 2,
                "Redis observes both live native SignalR connections");
            await AssertDistributedSignalRConnectionLimitAsync(timeout.Token);

            await primary.InvokeAsync(
                "SystemAnnouncement",
                """["native cross-host notification",1]""",
                timeout.Token);
            var notification = await secondary.WaitForTargetAsync(
                "Onsystem_announcement",
                timeout.Token);
            var announcement = GetHubArguments(notification, "Onsystem_announcement", 1)[0];
            AssertJsonFields(announcement, "id", "timestamp", "type", "priority", "message", "category");
            Equal("native cross-host notification", announcement.GetProperty("message").GetString()!,
                "Redis backplane delivers a JSON hub message across native Gateways");
            True(announcement.GetProperty("priority").ValueKind == JsonValueKind.Number &&
                 announcement.GetProperty("priority").GetInt32() == 1,
                "native SignalR priority preserves its numeric enum contract");
            Equal("system_announcement", announcement.GetProperty("type").GetString()!, "native announcement type");
            Equal("general", announcement.GetProperty("category").GetString()!, "native announcement category");
        }

        await using (var video = await NativeSignalRClient.ConnectAsync(
                         settings.Gateway,
                         "/hubs/video-generation",
                         NativeParityFixture.VirtualKey,
                         timeout.Token))
        {
            await video.InvokeAsync("UnsubscribeFromTask", """["native-video-task"]""", timeout.Token);
            True(true, "authenticated VideoGenerationHub JSON invocation");
        }

        await using (var image = await NativeSignalRClient.ConnectAsync(
                         settings.Gateway,
                         "/hubs/image-generation",
                         NativeParityFixture.VirtualKey,
                         timeout.Token))
        {
            await image.InvokeAsync("UnsubscribeFromTask", """["native-image-task"]""", timeout.Token);
            True(true, "authenticated ImageGenerationHub JSON invocation");
        }

        await using (var tasks = await NativeSignalRClient.ConnectAsync(
                         settings.Gateway,
                         "/hubs/tasks",
                         NativeParityFixture.VirtualKey,
                         timeout.Token))
        {
            await tasks.InvokeAsync("SubscribeToTaskType", """["native-probe"]""", timeout.Token);
            await tasks.InvokeAsync("UnsubscribeFromTaskType", """["native-probe"]""", timeout.Token);
            True(true, "authenticated TaskHub JSON group invocations");
            await tasks.InvokeAsync("SubscribeToTask", $$"""["{{NativeParityFixture.TaskId}}"]""", timeout.Token);
            (string Target, string Arguments)[] serverOnlyInvocations =
            [
                ("TaskStarted", $$$"""["{{{NativeParityFixture.TaskId}}}","native-probe",{"virtualKeyId":1,"Mixed_KEY":null,"zero":0,"done":false,"nested":{"UPPER_key":"preserved"}}]"""),
                ("TaskProgress", $$"""["{{NativeParityFixture.TaskId}}",99,"forged progress"]"""),
                ("TaskCompleted", $$$"""["{{{NativeParityFixture.TaskId}}}",{"Mixed_KEY":null,"zero":0,"done":false,"values":["fixture",0,null,false],"nested":{"UPPER_key":"preserved"}}]"""),
                ("TaskFailed", $$"""["{{NativeParityFixture.TaskId}}","forged failure",false]"""),
                ("TaskCancelled", $$"""["{{NativeParityFixture.TaskId}}","forged cancellation"]"""),
                ("TaskTimedOut", $$"""["{{NativeParityFixture.TaskId}}",30]"""),
            ];
            foreach (var (target, arguments) in serverOnlyInvocations)
            {
                var rejected = false;
                try
                {
                    await tasks.InvokeAsync(target, arguments, timeout.Token);
                }
                catch (InvalidOperationException exception) when (
                    exception.Message == $"SignalR invocation {target} failed: Failed to invoke '{target}' due to an error on the server. HubException: Method does not exist.")
                {
                    rejected = true;
                }
                True(rejected, $"native TaskHub rejects client invocation of server-only {target}");
            }
            await tasks.InvokeAsync("UnsubscribeFromTask", $$"""["{{NativeParityFixture.TaskId}}"]""", timeout.Token);
            True(true, "authenticated TaskHub JSON task subscription and unsubscription");
        }

        await using (await NativeSignalRClient.ConnectAsync(
                         settings.Gateway,
                         "/hubs/spend",
                         NativeParityFixture.VirtualKey,
                         timeout.Token))
        {
            True(true, "authenticated SpendNotificationHub JSON connection");
        }

        await using (var webhooks = await NativeSignalRClient.ConnectAsync(
                         settings.Gateway,
                         "/hubs/webhooks",
                         NativeParityFixture.VirtualKey,
                         timeout.Token))
        {
            const string webhookArguments = """[["https://native.invalid/hook"]]""";
            await webhooks.InvokeAsync("SubscribeToWebhooks", webhookArguments, timeout.Token);
            await webhooks.InvokeAsync("BroadcastDeliveryAttempt",
                $$"""["https://native.invalid/hook",{"WEBHOOKID":"native-webhook","DELIVERYID":"native-delivery","URL":"https://native.invalid/hook","EVENTTYPE":"fixture","ATTEMPTNUMBER":0,"TIMESTAMP":"2026-10-10T01:02:03Z","TASKID":"{{NativeParityFixture.TaskId}}","TASKTYPE":"native-probe"}]""",
                timeout.Token);
            var attempt = GetHubArguments(await webhooks.WaitForTargetAsync("DeliveryAttempted", timeout.Token), "DeliveryAttempted", 1)[0];
            AssertJsonFields(attempt, "webhookId", "deliveryId", "url", "eventType", "attemptNumber", "timestamp", "taskId", "taskType");
            Equal("native-webhook", attempt.GetProperty("webhookId").GetString()!, "native webhook DTO case-insensitive binding");
            Equal("native-delivery", attempt.GetProperty("deliveryId").GetString()!, "native webhook delivery ID");
            Equal("https://native.invalid/hook", attempt.GetProperty("url").GetString()!, "native webhook URL");
            Equal("fixture", attempt.GetProperty("eventType").GetString()!, "native webhook event type");
            True(attempt.GetProperty("attemptNumber").GetInt32() == 0, "native webhook DTO retains zero attempt count");
            True(attempt.GetProperty("timestamp").GetDateTime() == new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc),
                "native webhook DTO retains supplied UTC timestamp");
            Equal(NativeParityFixture.TaskId, attempt.GetProperty("taskId").GetString()!, "native webhook task ID");
            Equal("native-probe", attempt.GetProperty("taskType").GetString()!, "native webhook task type");
            await webhooks.InvokeAsync("UnsubscribeFromWebhooks", webhookArguments, timeout.Token);
            True(true, "authenticated WebhookDeliveryHub Redis-backed invocations");
        }

        await using (var management = await NativeSignalRClient.ConnectAsync(
                         settings.Gateway,
                         "/hubs/virtual-key-management",
                         NativeParityFixture.VirtualKey,
                         timeout.Token))
        {
            var status = await management.WaitForTargetAsync("VirtualKeyStatus", timeout.Token);
            True(status.Contains("Native AOT parity key", StringComparison.Ordinal),
                "VirtualKeyManagementHub sends typed-store key and group status");
        }
    }

    private async Task AssertDistributedSignalRConnectionLimitAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var rejected = await NativeSignalRClient.ConnectAsync(
                settings.Gateway,
                "/hubs/notifications",
                NativeParityFixture.VirtualKey,
                cancellationToken);
            var close = await rejected.WaitForServerCloseAsync(cancellationToken);
            True(close.Contains("close", StringComparison.OrdinalIgnoreCase) ||
                 close.Contains("limit", StringComparison.OrdinalIgnoreCase),
                "Redis rejects a third cross-host SignalR connection at the distributed ceiling");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("closed", StringComparison.OrdinalIgnoreCase))
        {
            True(true, "Redis rejects a third cross-host SignalR connection at the distributed ceiling");
        }
    }

    private async Task AssertPublicSignalRConnectionAsync()
    {
        using var request = AuthorizedRequest(HttpMethod.Post, "v1/conduit/auth/ephemeral-key");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await _gateway.SendAsync(request);
        await ExpectAsync(response, HttpStatusCode.OK, "native Redis ephemeral key creation");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var ephemeralKey = GetString(document.RootElement, "ephemeral_key");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var publicVideo = await NativeSignalRClient.ConnectAsync(
            settings.Gateway,
            "/hubs/public/video-generation",
            bearerToken: null,
            timeout.Token);
        await publicVideo.InvokeAsync(
            "SubscribeToTask",
            $$"""["{{NativeParityFixture.VideoNotificationTaskId}}","{{ephemeralKey}}"]""",
            timeout.Token);
        var subscribed = GetHubArguments(await publicVideo.WaitForTargetAsync("taskSubscribed", timeout.Token),
            "taskSubscribed", 1)[0];
        Equal(NativeParityFixture.VideoNotificationTaskId, subscribed.GetProperty("taskId").GetString()!,
            "public JSON SignalR subscription validates its Redis ephemeral key and persisted task ownership");
        await publicVideo.InvokeAsync(
            "UnsubscribeFromTask",
            $$"""["{{NativeParityFixture.VideoNotificationTaskId}}"]""",
            timeout.Token);
    }

    private async Task AssertWolverineMediaNotificationsAsync()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(settings.RedisConnectionString);
        await WaitForSignalRConnectionCountAsync(redis.GetDatabase(), 0,
            "previous native hub connections are released before media subscriptions");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var image = await NativeSignalRClient.ConnectAsync(settings.Gateway,
            "/hubs/image-generation", NativeParityFixture.VirtualKey, timeout.Token);
        await using var video = await NativeSignalRClient.ConnectAsync(settings.SecondaryGateway,
            "/hubs/video-generation", NativeParityFixture.VirtualKey, timeout.Token);
        await image.InvokeAsync("SubscribeToTask", $$"""["{{NativeParityFixture.ImageNotificationTaskId}}"]""", timeout.Token);
        await video.InvokeAsync("SubscribeToTask", $$"""["{{NativeParityFixture.VideoNotificationTaskId}}"]""", timeout.Token);

        using var request = AuthorizedRequest(HttpMethod.Post, "v1/conduit/auth/ephemeral-key");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await _gateway.SendAsync(request, timeout.Token);
        await ExpectAsync(response, HttpStatusCode.OK, "media subscription ephemeral key creation");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var ephemeralKey = GetString(document.RootElement, "ephemeral_key");
        await using var publicVideo = await NativeSignalRClient.ConnectAsync(settings.Gateway,
            "/hubs/public/video-generation", bearerToken: null, timeout.Token);
        await publicVideo.InvokeAsync("SubscribeToTask",
            $$"""["{{NativeParityFixture.VideoNotificationTaskId}}","{{ephemeralKey}}"]""", timeout.Token);
        await publicVideo.WaitForTargetAsync("taskSubscribed", timeout.Token);

        var probeId = Guid.NewGuid().ToString("N");
        var publishedAt = DateTime.UtcNow;
        await PublishMediaNotificationsAsync(probeId, timeout.Token);
        var progress = GetHubArguments(await image.WaitForTargetAsync("ImageGenerationProgress", timeout.Token),
            "ImageGenerationProgress", 1)[0];
        AssertJsonFields(progress, "taskId", "progressPercentage", "status", "imagesCompleted", "totalImages", "message", "timestamp");
        Equal(NativeParityFixture.ImageNotificationTaskId, progress.GetProperty("taskId").GetString()!, "native image progress task");
        Equal($"native-progress-{probeId}", progress.GetProperty("message").GetString()!, "Wolverine native image progress delivery");
        Equal("processing", progress.GetProperty("status").GetString()!, "native image progress status");
        True(progress.GetProperty("progressPercentage").GetInt32() == 50 &&
             progress.GetProperty("imagesCompleted").GetInt32() == 1 && progress.GetProperty("totalImages").GetInt32() == 2,
            "native image progress preserves computed percentage and counts");
        var progressTimestamp = progress.GetProperty("timestamp").GetDateTime();
        True(progressTimestamp.Kind == DateTimeKind.Utc && progressTimestamp >= publishedAt && progressTimestamp <= DateTime.UtcNow,
            "native image progress includes the handler UTC timestamp");

        var timestamp = new DateTime(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc);
        var started = GetHubArguments(await video.WaitForTargetAsync("VideoGenerationStarted", timeout.Token), "VideoGenerationStarted", 1)[0];
        AssertJsonFields(started, "taskId", "provider", "startedAt", "estimatedSeconds");
        Equal(NativeParityFixture.VideoNotificationTaskId, started.GetProperty("taskId").GetString()!, "native video started task");
        Equal($"native-provider-{probeId}", started.GetProperty("provider").GetString()!, "Wolverine native video started delivery");
        True(started.GetProperty("startedAt").GetDateTime() is { Kind: DateTimeKind.Utc } startedAt && startedAt == timestamp &&
             started.GetProperty("estimatedSeconds").ValueKind == JsonValueKind.Null,
            "native private video started preserves timestamp and null estimate");
        var publicStarted = GetHubArguments(await publicVideo.WaitForTargetAsync("TaskProgress", timeout.Token), "TaskProgress", 1)[0];
        AssertJsonFields(publicStarted, "taskId", "status", "progress", "message", "startedAt", "estimatedSeconds");
        Equal(NativeParityFixture.VideoNotificationTaskId, publicStarted.GetProperty("taskId").GetString()!, "native public started task");
        Equal("started", publicStarted.GetProperty("status").GetString()!, "native public started status");
        Equal($"Video generation started with native-provider-{probeId}", publicStarted.GetProperty("message").GetString()!,
            "Wolverine native public started delivery");
        True(publicStarted.GetProperty("progress").GetInt32() == 0 &&
             publicStarted.GetProperty("startedAt").GetDateTime() is { Kind: DateTimeKind.Utc } publicStartedAt && publicStartedAt == timestamp &&
             publicStarted.GetProperty("estimatedSeconds").ValueKind == JsonValueKind.Null,
            "native public video started preserves zero, timestamp and null estimate");

        var failed = GetHubArguments(await video.WaitForTargetAsync("VideoGenerationFailed", timeout.Token), "VideoGenerationFailed", 1)[0];
        AssertJsonFields(failed, "taskId", "status", "error", "errorCode", "isRetryable", "retryCount", "maxRetries", "nextRetryAt", "failedAt");
        Equal(NativeParityFixture.VideoNotificationTaskId, failed.GetProperty("taskId").GetString()!, "native video failure task");
        Equal("failed", failed.GetProperty("status").GetString()!, "native video failure status");
        Equal($"native-failure-{probeId}", failed.GetProperty("error").GetString()!, "Wolverine native video failure delivery");
        True(!failed.GetProperty("isRetryable").GetBoolean() && failed.GetProperty("retryCount").GetInt32() == 0 &&
             failed.GetProperty("maxRetries").GetInt32() == 3 && failed.GetProperty("errorCode").ValueKind == JsonValueKind.Null &&
             failed.GetProperty("nextRetryAt").ValueKind == JsonValueKind.Null &&
             failed.GetProperty("failedAt").GetDateTime() is { Kind: DateTimeKind.Utc } failedAt && failedAt == timestamp,
            "native private video failure preserves false, zero, nulls and UTC timestamp");
        var publicFailed = GetHubArguments(await publicVideo.WaitForTargetAsync("TaskFailed", timeout.Token), "TaskFailed", 1)[0];
        AssertJsonFields(publicFailed, "taskId", "error");
        Equal(NativeParityFixture.VideoNotificationTaskId, publicFailed.GetProperty("taskId").GetString()!, "native public failure task");
        Equal($"native-failure-{probeId}", publicFailed.GetProperty("error").GetString()!, "Wolverine native public video failure delivery");
    }

    private static async Task PublishMediaNotificationsAsync(string probeId, CancellationToken cancellationToken)
    {
        var publisherPath = Environment.GetEnvironmentVariable("CONDUIT_NATIVE_MESSAGE_PUBLISHER_PATH");
        if (string.IsNullOrWhiteSpace(publisherPath) || !File.Exists(publisherPath))
        {
            throw new InvalidOperationException("CONDUIT_NATIVE_MESSAGE_PUBLISHER_PATH must identify the built WolverineSmokePublisher DLL.");
        }
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { publisherPath, probeId, "--media-notifications",
                     NativeParityFixture.ImageNotificationTaskId, NativeParityFixture.VideoNotificationTaskId })
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the Wolverine media publisher.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            var output = await stdout;
            var errors = await stderr;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Wolverine media publisher exited with {process.ExitCode}: {output}\n{errors}");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static JsonElement GetHubArguments(string frame, string target, int count)
    {
        using var document = JsonDocument.Parse(frame);
        var root = document.RootElement;
        True(root.GetProperty("type").GetInt32() == 1, $"native {target} invocation frame");
        Equal(target, root.GetProperty("target").GetString()!, $"native {target} target");
        var arguments = root.GetProperty("arguments");
        True(arguments.GetArrayLength() == count, $"native {target} argument count");
        return arguments.Clone();
    }

    private static void AssertJsonFields(JsonElement payload, params string[] fields) =>
        True(fields.Order(StringComparer.Ordinal).SequenceEqual(
                payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)),
            "native SignalR payload preserves exact field set and casing");

    private static async Task WaitForSignalRConnectionCountAsync(
        IDatabase database,
        long expected,
        string name)
    {
        var key = $"signalr:vk:{NativeParityFixture.VirtualKeyHash}:connections";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        long latest = -1;
        while (DateTime.UtcNow < deadline)
        {
            var value = await database.HashGetAsync(key, "count");
            latest = value.HasValue && long.TryParse(value.ToString(), out var count)
                ? count
                : 0;
            if (latest == expected)
            {
                True(true, name);
                return;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            $"Redis SignalR connection count did not reach {expected}; latest value was {latest}.");
    }

    private static HttpClient CreateClient(Uri baseAddress) => new()
    {
        BaseAddress = baseAddress,
        Timeout = TimeSpan.FromSeconds(30)
    };

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            NativeParityFixture.VirtualKey);
        return request;
    }

    private static HttpRequestMessage AuthorizedChatRequest(
        string prompt, bool stream, bool includeUsage = false, bool enhancedStream = false)
    {
        var request = AuthorizedRequest(HttpMethod.Post,
            enhancedStream ? "v1/conduit/chat/completions" : "v1/chat/completions");
        var streamOptions = includeUsage ? ",\"stream_options\":{\"include_usage\":true}" : string.Empty;
        request.Content = new StringContent(
            $$"""
              {"model":"{{NativeParityFixture.ModelAlias}}","messages":[{"role":"user","content":"{{prompt}}"}],"stream":{{(stream ? "true" : "false")}}{{streamOptions}}}
              """,
            Encoding.UTF8,
            "application/json");
        return request;
    }

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

    private readonly record struct ProviderState(
        int NonStreamingRequests,
        int StreamingRequests,
        int ImageRequests,
        int ErrorRequests,
        int AuthorizationFailures,
        int ActiveCancellationRequests,
        int CancellationsObserved);

    private sealed record MediaSnapshot(
        string StorageKey,
        string ContentType,
        long SizeBytes,
        string Model,
        bool IsActive);

    private sealed record AccountingSnapshot(
        long MatchingRequestLogs,
        decimal RequestLogCost,
        decimal Balance,
        decimal LifetimeSpent,
        decimal LedgerDebits);
}

internal static class NativeParityFixture
{
    public const string VirtualKey = "condt_native_aot_parity";
    public const string ModelAlias = "native-aot-model";
    public const string TaskId = "task_native_aot_parity";
    public const string ImageNotificationTaskId = "task_native_aot_image_notification";
    public const string VideoNotificationTaskId = "task_native_aot_video_notification";
    public const string MediaPrompt = "native-s3-media-persistence";
    public const string FixtureOwner = "native-aot-parity";
    private const int MediaPolicyId = -9010;
    public const int SearchFunctionId = -9020;
    public const int AnswerFunctionId = -9021;
    public const int DisabledFunctionId = -9022;
    public const int UnknownFunctionId = -9023;
    public static string VirtualKeyHash => Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(VirtualKey)))
        .ToLowerInvariant();

    public static async Task SeedAsync(
        string connectionString,
        Uri providerUri,
        string redisConnectionString)
    {
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var now = DateTime.UtcNow;

        await UpsertSettingAsync(connection, transaction, "IpFilter:Enabled", "true", now);
        await UpsertSettingAsync(connection, transaction, "IpFilter:DefaultAllow", "false", now);
        await UpsertMediaPolicyAsync(connection, transaction, now);
        var groupId = await UpsertGroupAsync(connection, transaction, now);
        var keyId = await UpsertVirtualKeyAsync(connection, transaction, groupId, now);
        await ReplaceIpFiltersAsync(connection, transaction, keyId, now);
        await UpsertModelRoutingAsync(
            connection,
            transaction,
            providerUri.GetLeftPart(UriPartial.Authority).TrimEnd('/'),
            now);
        await ResetAccountingAsync(connection, transaction, groupId, keyId);
        await UpsertAsyncTaskAsync(connection, transaction, keyId, now);
        await UpsertAsyncTaskAsync(connection, transaction, keyId, now, ImageNotificationTaskId, "native_signalr_probe");
        await UpsertAsyncTaskAsync(connection, transaction, keyId, now, VideoNotificationTaskId, "native_signalr_probe");
        await UpsertFunctionDiscoveryAsync(connection, transaction, now);

        await transaction.CommitAsync();
        await ResetRedisAsync(redisConnectionString, groupId);
        Console.WriteLine(
            "Seeded native Gateway virtual-key, IP-filter, model-routing, function-discovery, provider, accounting, async-task, and media parity fixture.");
    }

    private static async Task UpsertFunctionDiscoveryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateTime now)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO "FunctionConfigurations" (
                "Id", "ProviderType", "ConfigurationName", "Purpose", "DefaultExecutionMode",
                "BaseUrl", "IsEnabled", "TimeoutSeconds", "CacheTtlMinutes", "MaxRetries",
                "ProviderSettings", "ParameterSchema", "Description", "CreatedAt", "UpdatedAt")
            VALUES
                (@searchId, 1, 'Native Search 雪', 1, 1, 'https://private.example.test', TRUE, 41, 7, 0,
                    '{"private":"native-private-settings"}', @schema, '', @now, @now),
                (@answerId, 4, 'Native Array Schema', 2, 2, NULL, TRUE, NULL, NULL, NULL,
                    NULL, '[0,false,{"snow":"雪"}]', NULL, @now, @now),
                (@disabledId, 1, 'Native Disabled', 1, 1, NULL, FALSE, NULL, NULL, NULL,
                    NULL, NULL, NULL, @now, @now),
                (@unknownId, 197, 'Native Unknown Enums', -9, 123, NULL, TRUE, NULL, NULL, NULL,
                    NULL, NULL, NULL, @now, @now)
            ON CONFLICT ("Id") DO UPDATE SET
                "ProviderType" = EXCLUDED."ProviderType", "ConfigurationName" = EXCLUDED."ConfigurationName",
                "Purpose" = EXCLUDED."Purpose", "DefaultExecutionMode" = EXCLUDED."DefaultExecutionMode",
                "BaseUrl" = EXCLUDED."BaseUrl", "IsEnabled" = EXCLUDED."IsEnabled",
                "TimeoutSeconds" = EXCLUDED."TimeoutSeconds", "CacheTtlMinutes" = EXCLUDED."CacheTtlMinutes",
                "MaxRetries" = EXCLUDED."MaxRetries", "ProviderSettings" = EXCLUDED."ProviderSettings",
                "ParameterSchema" = EXCLUDED."ParameterSchema", "Description" = EXCLUDED."Description",
                "UpdatedAt" = EXCLUDED."UpdatedAt";
            """;
        command.Parameters.AddWithValue("searchId", NpgsqlDbType.Integer, SearchFunctionId);
        command.Parameters.AddWithValue("answerId", NpgsqlDbType.Integer, AnswerFunctionId);
        command.Parameters.AddWithValue("disabledId", NpgsqlDbType.Integer, DisabledFunctionId);
        command.Parameters.AddWithValue("unknownId", NpgsqlDbType.Integer, UnknownFunctionId);
        command.Parameters.AddWithValue("schema", NpgsqlDbType.Jsonb,
            """{"properties":{"query":{"type":"string"}},"required":["query"],"example":{"query":"雪"}}""");
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpsertModelRoutingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string providerBaseUrl,
        DateTime now)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO "Providers" (
                "Id", "ProviderType", "ProviderName", "BaseUrl", "Settings", "IsEnabled",
                "TrustProviderReportedCosts", "ProviderCostMarkupMultiplier", "CreatedAt", "UpdatedAt")
            VALUES (-9001, 1, 'Native AOT parity provider', @providerBaseUrl, '{"region":"native"}'::jsonb,
                true, true, 1.125, @now, @now)
            ON CONFLICT ("Id") DO UPDATE SET
                "ProviderType" = EXCLUDED."ProviderType",
                "ProviderName" = EXCLUDED."ProviderName",
                "BaseUrl" = EXCLUDED."BaseUrl",
                "Settings" = EXCLUDED."Settings",
                "IsEnabled" = true,
                "UpdatedAt" = EXCLUDED."UpdatedAt";

            UPDATE "ProviderKeyCredentials"
            SET "IsPrimary" = false,
                "UpdatedAt" = @now
            WHERE "ProviderId" = -9001
              AND "Id" <> -9009;

            INSERT INTO "ProviderKeyCredentials" (
                "Id", "ProviderId", "ProviderAccountGroup", "ApiKey", "BaseUrl",
                "SecretSettings", "KeyName", "IsPrimary", "IsEnabled", "CreatedAt", "UpdatedAt")
            VALUES (-9009, -9001, 0, @providerApiKey, @providerBaseUrl,
                NULL, 'Native AOT parity credential', true, true, @now, @now)
            ON CONFLICT ("Id") DO UPDATE SET
                "ProviderId" = EXCLUDED."ProviderId",
                "ProviderAccountGroup" = EXCLUDED."ProviderAccountGroup",
                "ApiKey" = EXCLUDED."ApiKey",
                "BaseUrl" = EXCLUDED."BaseUrl",
                "SecretSettings" = NULL,
                "KeyName" = EXCLUDED."KeyName",
                "IsPrimary" = true,
                "IsEnabled" = true,
                "UpdatedAt" = EXCLUDED."UpdatedAt";

            INSERT INTO "ModelAuthors" ("Id", "Name", "Description", "WebsiteUrl")
            VALUES (-9002, 'Native AOT parity author', 'Native process fixture', NULL)
            ON CONFLICT ("Id") DO UPDATE SET "Name" = EXCLUDED."Name";

            INSERT INTO "ModelSeries" (
                "Id", "AuthorId", "Name", "Description", "TokenizerType", "Parameters")
            VALUES (-9003, -9002, 'Native AOT parity series', NULL, 0, '{"temperature":{}}')
            ON CONFLICT ("Id") DO UPDATE SET
                "AuthorId" = EXCLUDED."AuthorId",
                "Name" = EXCLUDED."Name",
                "Parameters" = EXCLUDED."Parameters";

            INSERT INTO "Models" (
                "Id", "Name", "Version", "Description", "ModelCardUrl", "ModelSeriesId",
                "SupportsVision", "SupportsImageGeneration", "SupportsVideoGeneration",
                "SupportsEmbeddings", "SupportsSpeechToText", "SupportsTextToSpeech",
                "SupportsRerank", "SupportsChat", "SupportsFunctionCalling", "SupportsStreaming",
                "InputModalities", "OutputModalities", "CapabilitySource",
                "CapabilitiesLastVerifiedAt", "TokenizerType", "MaxInputTokens", "MaxOutputTokens",
                "IsActive", "Parameters", "CreatedAt", "UpdatedAt")
            VALUES (-9004, 'Native AOT parity model', 'v1', 'Native process routing fixture', NULL, -9003,
                true, true, false, false, false, false, false, true, true, true,
                '["text","image"]'::jsonb, '["text","image"]'::jsonb, 2, @now, 0, 64000, 8192,
                true, NULL, @now, @now)
            ON CONFLICT ("Id") DO UPDATE SET
                "Name" = EXCLUDED."Name",
                "ModelSeriesId" = EXCLUDED."ModelSeriesId",
                "SupportsVision" = true,
                "SupportsImageGeneration" = true,
                "SupportsChat" = true,
                "SupportsFunctionCalling" = true,
                "SupportsStreaming" = true,
                "InputModalities" = EXCLUDED."InputModalities",
                "OutputModalities" = EXCLUDED."OutputModalities",
                "UpdatedAt" = EXCLUDED."UpdatedAt";

            INSERT INTO "ModelCosts" (
                "Id", "CostName", "PricingModel", "PricingConfiguration",
                "InputCostPerMillionTokens", "OutputCostPerMillionTokens",
                "EmbeddingCostPerMillionTokens", "CreatedAt", "UpdatedAt", "ModelType",
                "IsActive", "EffectiveDate", "ExpiryDate", "Description", "Priority",
                "BatchProcessingMultiplier", "SupportsBatchProcessing",
                "CachedInputCostPerMillionTokens", "CachedInputWriteCostPerMillionTokens",
                "CostPerSearchUnit", "AudioCostPerMinute", "AudioCostPerThousandCharacters",
                "ReasoningCostPerMillionTokens")
            VALUES (-9005, 'Native AOT parity cost', 0, NULL, 2.5, 10, NULL,
                @now, @now, 'chat', true, @now, NULL, NULL, 0, 0.5, true,
                0.25, NULL, NULL, NULL, NULL, NULL)
            ON CONFLICT ("Id") DO UPDATE SET
                "InputCostPerMillionTokens" = EXCLUDED."InputCostPerMillionTokens",
                "OutputCostPerMillionTokens" = EXCLUDED."OutputCostPerMillionTokens",
                "UpdatedAt" = EXCLUDED."UpdatedAt";

            INSERT INTO "ModelIdentifiers" (
                "Id", "ModelId", "IsEnabled", "MaxInputTokens", "MaxOutputTokens",
                "InputModalities", "OutputModalities", "OperationalCapabilities",
                "CapabilitySource", "CapabilitiesLastVerifiedAt", "ProviderVariation",
                "QualityScore", "SpeedScore", "Identifier", "Provider", "ModelCostId",
                "IsPrimary", "Metadata")
            VALUES (-9006, -9004, true, 32000, 4096, '["text","image"]'::jsonb,
                '["text","image"]'::jsonb, '{"supports_json_schema":true}'::jsonb, 2, @now,
                NULL, 0.95, 1.5, 'native-aot-provider-model', 1, -9005, true, NULL)
            ON CONFLICT ("Id") DO UPDATE SET
                "ModelId" = EXCLUDED."ModelId",
                "IsEnabled" = true,
                "InputModalities" = EXCLUDED."InputModalities",
                "OutputModalities" = EXCLUDED."OutputModalities",
                "OperationalCapabilities" = EXCLUDED."OperationalCapabilities",
                "ModelCostId" = EXCLUDED."ModelCostId";

            INSERT INTO "ModelProviderMappings" (
                "Id", "ModelAlias", "ProviderModelId", "ProviderId", "IsEnabled",
                "RoutingPriority", "RoutingWeight", "ProviderOptions", "CreatedAt", "UpdatedAt",
                "ModelProviderTypeAssociationId")
            VALUES (-9007, @alias, 'native-aot-provider-model', -9001, true,
                10, 1.25, '{"route":"fallback"}', @now, @now, -9006)
            ON CONFLICT ("Id") DO UPDATE SET
                "ModelAlias" = EXCLUDED."ModelAlias",
                "ProviderId" = EXCLUDED."ProviderId",
                "IsEnabled" = true,
                "ModelProviderTypeAssociationId" = EXCLUDED."ModelProviderTypeAssociationId",
                "UpdatedAt" = EXCLUDED."UpdatedAt";

            INSERT INTO "ModelRoutePolicies" (
                "Id", "ModelAlias", "Strategy", "CostWeight", "SpeedWeight", "QualityWeight",
                "CacheAffinityEnabled", "AffinityTtlSeconds", "MaxAffinityScorePenalty",
                "IsEnabled", "CreatedAt", "UpdatedAt")
            VALUES (-9008, @alias, 'Balanced', 0.5, 0.3, 0.2, true, 900, 0.075,
                true, @now, @now)
            ON CONFLICT ("Id") DO UPDATE SET
                "ModelAlias" = EXCLUDED."ModelAlias",
                "IsEnabled" = true,
                "UpdatedAt" = EXCLUDED."UpdatedAt";
            """;
        command.Parameters.AddWithValue("alias", NpgsqlDbType.Varchar, ModelAlias);
        command.Parameters.AddWithValue("providerBaseUrl", NpgsqlDbType.Text, providerBaseUrl);
        command.Parameters.AddWithValue("providerApiKey", NpgsqlDbType.Text, NativeProviderStub.ApiKey);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ResetAccountingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int groupId,
        int keyId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM "MediaRecords" WHERE "VirtualKeyId" = @keyId;
            DELETE FROM "RequestLogs" WHERE "VirtualKeyId" = @keyId;
            DELETE FROM "VirtualKeyGroupTransactions" WHERE "VirtualKeyGroupId" = @groupId;
            """;
        command.Parameters.AddWithValue("keyId", NpgsqlDbType.Integer, keyId);
        command.Parameters.AddWithValue("groupId", NpgsqlDbType.Integer, groupId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpsertMediaPolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTime now)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO "MediaRetentionPolicies" (
                "Id", "Name", "Description", "PositiveBalanceRetentionDays",
                "ZeroBalanceRetentionDays", "NegativeBalanceRetentionDays",
                "SoftDeleteGracePeriodDays", "RespectRecentAccess",
                "RecentAccessWindowDays", "IsDefault", "MaxStorageSizeBytes",
                "MaxFileCount", "QuotaExceededBehavior", "CreatedAt", "UpdatedAt",
                "IsActive")
            VALUES (
                @id, 'Native AOT parity media', 'Native process media quota fixture',
                60, 14, 3, 7, true, 7, false, 1048576, 1, 0, @now, @now, true)
            ON CONFLICT ("Id") DO UPDATE SET
                "Name" = EXCLUDED."Name",
                "Description" = EXCLUDED."Description",
                "MaxStorageSizeBytes" = EXCLUDED."MaxStorageSizeBytes",
                "MaxFileCount" = EXCLUDED."MaxFileCount",
                "QuotaExceededBehavior" = EXCLUDED."QuotaExceededBehavior",
                "UpdatedAt" = EXCLUDED."UpdatedAt",
                "IsActive" = true
            """;
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, MediaPolicyId);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task UpsertAsyncTaskAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int keyId,
        DateTime now,
        string taskId = TaskId,
        string type = "image_generation")
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO "AsyncTasks" (
                "Id", "Type", "State", "Progress", "CreatedAt", "UpdatedAt",
                "VirtualKeyId", "Metadata", "IsArchived", "Version", "RetryCount",
                "MaxRetries", "IsRetryable")
            VALUES (
                @taskId, @type, 0, 0, @now, @now,
                @keyId, @metadata, false, 0, 0, 3, true)
            ON CONFLICT ("Id") DO UPDATE SET
                "Type" = EXCLUDED."Type",
                "State" = 0,
                "Payload" = NULL,
                "Progress" = 0,
                "ProgressMessage" = NULL,
                "Result" = NULL,
                "Error" = NULL,
                "UpdatedAt" = EXCLUDED."UpdatedAt",
                "CompletedAt" = NULL,
                "VirtualKeyId" = EXCLUDED."VirtualKeyId",
                "Metadata" = EXCLUDED."Metadata",
                "IsArchived" = false,
                "ArchivedAt" = NULL,
                "LeasedBy" = NULL,
                "LeaseExpiryTime" = NULL,
                "ProviderInvocationStartedAt" = NULL,
                "ProviderInvocationCompletedAt" = NULL,
                "ProviderOperationId" = NULL,
                "RetryDispatchId" = NULL,
                "Version" = 0,
                "RetryCount" = 0,
                "MaxRetries" = 3,
                "IsRetryable" = true,
                "NextRetryAt" = NULL
            """;
        command.Parameters.AddWithValue("taskId", NpgsqlDbType.Varchar, taskId);
        command.Parameters.AddWithValue("type", NpgsqlDbType.Varchar, type);
        command.Parameters.AddWithValue("keyId", NpgsqlDbType.Integer, keyId);
        command.Parameters.AddWithValue(
            "metadata",
            NpgsqlDbType.Text,
            $$"""{"VirtualKeyId":{{keyId}},"Model":"{{ModelAlias}}"}""");
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ResetRedisAsync(string connectionString, int groupId)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString);
        var database = redis.GetDatabase();
        var endpoint = redis.GetEndPoints().Single();
        var server = redis.GetServer(endpoint);
        var keys = new[]
            {
                $"*group:{groupId}*",
                $"signalr:vk:{VirtualKeyHash}:*",
                $"async:task:{TaskId}",
                $"async:task:{ImageNotificationTaskId}",
                $"async:task:{VideoNotificationTaskId}"
            }
            .SelectMany(pattern => server.Keys(database.Database, pattern: pattern))
            .Distinct()
            .ToArray();
        if (keys.Length > 0)
        {
            await database.KeyDeleteAsync(keys);
        }
    }

    private static async Task UpsertSettingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        string value,
        DateTime now)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO "GlobalSettings" ("Key", "Value", "Description", "CreatedAt", "UpdatedAt")
            VALUES (@key, @value, @description, @now, @now)
            ON CONFLICT ("Key") DO UPDATE
            SET "Value" = EXCLUDED."Value",
                "Description" = EXCLUDED."Description",
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            """;
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, key);
        command.Parameters.AddWithValue("value", NpgsqlDbType.Text, value);
        command.Parameters.AddWithValue(
            "description",
            NpgsqlDbType.Text,
            "Native Gateway parity fixture");
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> UpsertGroupAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTime now)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH updated AS (
                UPDATE "VirtualKeyGroups"
                SET "GroupName" = @name,
                    "Balance" = 100.0,
                    "LifetimeCreditsAdded" = 100.0,
                    "LifetimeSpent" = 0.0,
                    "MediaRetentionPolicyId" = @mediaPolicyId,
                    "UpdatedAt" = @now
                WHERE "ExternalGroupId" = @externalId
                RETURNING "Id"
            ), inserted AS (
                INSERT INTO "VirtualKeyGroups" (
                    "ExternalGroupId", "GroupName", "Balance", "LifetimeCreditsAdded",
                    "LifetimeSpent", "MediaRetentionPolicyId", "CreatedAt", "UpdatedAt")
                SELECT @externalId, @name, 100.0, 100.0, 0.0, @mediaPolicyId, @now, @now
                WHERE NOT EXISTS (SELECT 1 FROM updated)
                RETURNING "Id"
            )
            SELECT "Id" FROM updated
            UNION ALL
            SELECT "Id" FROM inserted
            LIMIT 1
            """;
        command.Parameters.AddWithValue("externalId", NpgsqlDbType.Varchar, FixtureOwner);
        command.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, "Native AOT parity group");
        command.Parameters.AddWithValue("mediaPolicyId", NpgsqlDbType.Integer, MediaPolicyId);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        return (int)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Native parity group upsert returned no ID."));
    }

    private static async Task<int> UpsertVirtualKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int groupId,
        DateTime now)
    {
        var keyHash = VirtualKeyHash;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO "VirtualKeys" (
                "KeyName", "KeyHash", "Description", "IsEnabled", "VirtualKeyGroupId",
                "RateLimitRpm", "RateLimitRpd", "CreatedAt", "UpdatedAt")
            VALUES (@name, @hash, @description, true, @groupId, 100, 1000, @now, @now)
            ON CONFLICT ("KeyHash") DO UPDATE
            SET "KeyName" = EXCLUDED."KeyName",
                "Description" = EXCLUDED."Description",
                "IsEnabled" = true,
                "VirtualKeyGroupId" = EXCLUDED."VirtualKeyGroupId",
                "RateLimitRpm" = EXCLUDED."RateLimitRpm",
                "RateLimitRpd" = EXCLUDED."RateLimitRpd",
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            RETURNING "Id"
            """;
        command.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, "Native AOT parity key");
        command.Parameters.AddWithValue("hash", NpgsqlDbType.Varchar, keyHash);
        command.Parameters.AddWithValue("description", NpgsqlDbType.Varchar, "Native Gateway parity fixture");
        command.Parameters.AddWithValue("groupId", NpgsqlDbType.Integer, groupId);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        return (int)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Native parity key upsert returned no ID."));
    }

    private static async Task ReplaceIpFiltersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int keyId,
        DateTime now)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM \"IpFilters\" WHERE \"CreatedBy\" = @owner";
            delete.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, FixtureOwner);
            await delete.ExecuteNonQueryAsync();
        }

        await InsertIpFilterAsync(connection, transaction, virtualKeyId: null, "global", now);
        await InsertIpFilterAsync(connection, transaction, keyId, "virtual key", now);
    }

    private static async Task InsertIpFilterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int? virtualKeyId,
        string scope,
        DateTime now)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO "IpFilters" (
                "FilterType", "IpAddressOrCidr", "Name", "Description", "IsEnabled",
                "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "VirtualKeyId")
            VALUES (
                'whitelist', '127.0.0.1', @name, @description, true,
                @now, @now, @owner, @owner, @virtualKeyId)
            """;
        command.Parameters.AddWithValue("name", NpgsqlDbType.Varchar, $"Native parity {scope} allowlist");
        command.Parameters.AddWithValue("description", NpgsqlDbType.Varchar, "Allows the local native parity client");
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        command.Parameters.AddWithValue("owner", NpgsqlDbType.Varchar, FixtureOwner);
        command.Parameters.Add(new NpgsqlParameter("virtualKeyId", NpgsqlDbType.Integer)
        {
            Value = virtualKeyId.HasValue ? virtualKeyId.Value : DBNull.Value
        });
        await command.ExecuteNonQueryAsync();
    }
}
