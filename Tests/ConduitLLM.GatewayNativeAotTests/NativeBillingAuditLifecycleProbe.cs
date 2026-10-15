using System.Net;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Npgsql;
using NpgsqlTypes;

// This proof uses a third owned key and rejects before dispatch/accounting. It
// never writes audit rows itself: only each actual Gateway's local worker can.
internal static class NativeBillingAuditLifecycleProbe
{
    internal static async Task TimerAsync(Uri primary, Uri secondary, Uri provider, NpgsqlDataSource source,
        Guid owner, string protectedKey)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        var fixture = await NativeFunctionFixture.CreateAsync(source, owner, provider, protectedKey, token);
        using var first = Client(primary);
        using var second = Client(secondary);
        using var upstream = Client(provider);
        var providerBefore = await ProviderStateAsync(upstream, token);
        Require((await AuditRowsAsync(source, fixture, token)).Count == 0, "fresh lifecycle audit scope");
        var initial = await Task.WhenAll(MetricsAsync(first, token), MetricsAsync(second, token));
        Require(initial.All(sample => sample.Queued == 0 && sample.Flushed == 0 && sample.Depth == 0 && sample.Failures == 0),
            "quiet fresh Gateways before any protocol workload");
        await Task.WhenAll(RejectAsync(first, fixture, "timer", "primary", token),
            RejectAsync(second, fixture, "timer", "secondary", token));
        while (true)
        {
            var samples = await Task.WhenAll(MetricsAsync(first, token), MetricsAsync(second, token));
            Require(samples.All(sample => sample.Queued == 1 && sample.Queued < 100 && sample.Failures == 0),
                "one local event per Gateway cannot reach the batch threshold");
            if (samples.All(sample => sample.Depth == 0 && sample.Flushed == 1)) break;
            await Task.Delay(50, token);
        }
        await AssertAuditsAsync(source, fixture, completedHosts: 0, preparedHost: null, token);
        await AssertPristineAsync(source, fixture, token);
        Require(JsonElement.DeepEquals(providerBefore, await ProviderStateAsync(upstream, token)), "timer makes no provider requests");
        Console.WriteLine("Native BillingAudit quiet timer: PASS");
    }

    internal static async Task DrainAsync(Uri primary, Uri secondary)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var first = Client(primary);
        using var second = Client(secondary);
        while (true)
        {
            var samples = await Task.WhenAll(MetricsAsync(first, deadline.Token), MetricsAsync(second, deadline.Token));
            if (samples.All(sample => sample.Depth == 0 && sample.Flushed == sample.Queued && sample.Queued >= 1)) break;
            await Task.Delay(50, deadline.Token);
        }
        Console.WriteLine("Native BillingAudit completed timer wave: PASS");
    }

    internal static async Task PrepareAsync(Uri gateway, Uri provider, NpgsqlDataSource source, Guid owner, string host)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        var completedHosts = CompletedBefore(host);
        var fixture = await RequiredFixtureAsync(source, owner, token);
        using var client = Client(gateway);
        using var upstream = Client(provider);
        var providerBefore = await ProviderStateAsync(upstream, token);
        await AssertAuditsAsync(source, fixture, completedHosts, preparedHost: null, token);
        await AssertPristineAsync(source, fixture, token);
        var initial = await MetricsAsync(client, token);
        Require(initial.Depth == 0 && initial.Flushed == initial.Queued,
            "completed workload wave before a fresh per-host timer marker");
        await RejectAsync(client, fixture, "prepare", host, token);
        long observedAt;
        while (true)
        {
            // Start the clock before the successful metrics read so response
            // delivery time also consumes the harness's three-second budget.
            observedAt = Stopwatch.GetTimestamp();
            var sample = await MetricsAsync(client, token);
            Require(sample.Queued == initial.Queued + 1 && sample.Failures == initial.Failures &&
                sample.Depth is 0 or 1 && sample.Flushed >= initial.Flushed && sample.Flushed <= initial.Flushed + 1,
                "only one fresh marker is available to the periodic worker");
            if (sample.Depth == 0 && sample.Flushed == initial.Flushed + 1) break;
            await Task.Delay(50, token);
        }
        await AssertAuditsAsync(source, fixture, completedHosts, preparedHost: host, token);
        await AssertPristineAsync(source, fixture, token);
        Require(JsonElement.DeepEquals(providerBefore, await ProviderStateAsync(upstream, token)), "prepare makes no provider requests");
        Console.WriteLine($"Native BillingAudit prepared shutdown timer {host}: PASS " +
            observedAt.ToString(CultureInfo.InvariantCulture) + " " + Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture));
    }

    internal static async Task ArmTailAsync(Uri gateway, Uri provider, NpgsqlDataSource source, Guid owner, string host)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        var completedHosts = CompletedBefore(host);
        var fixture = await RequiredFixtureAsync(source, owner, token);
        using var client = Client(gateway);
        using var upstream = Client(provider);
        var providerBefore = await ProviderStateAsync(upstream, token);
        await AssertAuditsAsync(source, fixture, completedHosts, preparedHost: host, token);
        await AssertPristineAsync(source, fixture, token);
        var initial = await MetricsAsync(client, token);
        Require(initial.Depth == 0 && initial.Flushed == initial.Queued,
            "fresh timer marker completed before arming the host tail");
        await RejectAsync(client, fixture, "tail", host, token);
        var pending = await MetricsAsync(client, token);
        Require(pending.Queued == initial.Queued + 1 && pending.Depth == 1 &&
            pending.Flushed == initial.Flushed && pending.Failures == initial.Failures,
            "one newly queued tail remains pending on the selected actual host");
        await AssertAuditsAsync(source, fixture, completedHosts, preparedHost: host, token);
        await AssertPristineAsync(source, fixture, token);
        Require(JsonElement.DeepEquals(providerBefore, await ProviderStateAsync(upstream, token)), "tail makes no provider requests");
        Console.WriteLine($"Native BillingAudit pending shutdown tail {host}: PASS");
    }

    internal static async Task VerifyHostAsync(NpgsqlDataSource source, Guid owner, string host)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var fixture = await RequiredFixtureAsync(source, owner, deadline.Token);
        // The script checks this host's graceful exit and exclusive Final1
        // witness before allowing the next host's prepare marker.
        await AssertAuditsAsync(source, fixture, CompletedBefore(host) + 1, preparedHost: null, deadline.Token);
        await AssertPristineAsync(source, fixture, deadline.Token);
        Console.WriteLine($"Native BillingAudit graceful shutdown tail {host}: PASS");
    }

    internal static async Task VerifyTailAsync(NpgsqlDataSource source, Guid owner)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var fixture = await RequiredFixtureAsync(source, owner, deadline.Token);
        // The script requires both owned processes to exit gracefully and each
        // host's exact final-flush witness before this independent SQL check.
        await AssertAuditsAsync(source, fixture, completedHosts: 2, preparedHost: null, deadline.Token);
        await AssertPristineAsync(source, fixture, deadline.Token);
        Console.WriteLine("Native BillingAudit graceful shutdown tails: PASS");
    }

    internal static async Task CleanupAsync(NpgsqlDataSource source, Guid owner, string redis)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var fixture = await NativeFunctionFixture.ReadAsync(source, owner, deadline.Token);
        if (fixture is not null)
        {
            await NativeFunctionFixtureRedisCleanup.DeleteAsync(redis, fixture, deadline.Token);
            await NativeFunctionFixture.DeleteAsync(source, owner, deadline.Token);
        }
        Console.WriteLine("Owned BillingAudit lifecycle fixture cleanup: PASS");
    }

    private static async Task RejectAsync(HttpClient client, NativeFunctionFixtureSnapshot fixture, string phase,
        string host, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.VirtualKey);
        var requestId = RequestId(fixture.Owner, phase, host);
        request.Headers.Add("x-request-id", requestId);
        using var body = JsonDocument.Parse($$"""
            {"model":"{{NativeParityFixture.ModelAlias}}","messages":[{"role":"user","content":"Native BillingAudit lifecycle café 雪 🔎"}],
             "function_configuration_ids":[-2147483648],"max_agentic_iterations":0,"enable_agentic_mode":false,"stream":false}
            """);
        request.Content = JsonContent.Create(body.RootElement, NativeProbeJsonContext.Default.JsonElement);
        using var response = await client.SendAsync(request, token);
        Require(response.StatusCode == HttpStatusCode.BadRequest &&
            response.Headers.TryGetValues("x-request-id", out var ids) && ids.SequenceEqual([requestId]), "controlled preflight400 correlation");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var error = document.RootElement.GetProperty("error");
        Require(error.GetProperty("type").GetString() == "invalid_request_error" &&
            error.GetProperty("code").GetString() == "invalid_function_configuration_ids" &&
            error.GetProperty("message").GetString() == "Function configuration IDs not found: -2147483648",
            "missing configuration rejects before defaults, dispatch or accounting");
    }

    private static async Task AssertAuditsAsync(NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture,
        int completedHosts, string? preparedHost, CancellationToken token)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            RequestId(fixture.Owner, "timer", "primary"), RequestId(fixture.Owner, "timer", "secondary")
        };
        foreach (var host in new[] { "primary", "secondary" }.Take(completedHosts))
        {
            expected.Add(RequestId(fixture.Owner, "prepare", host));
            expected.Add(RequestId(fixture.Owner, "tail", host));
        }
        if (preparedHost is not null) expected.Add(RequestId(fixture.Owner, "prepare", preparedHost));
        var createdAt = await CreatedAtAsync(source, fixture, token);
        var rows = await AuditRowsAsync(source, fixture, token);
        Require(rows.Count == expected.Count, "exact owned audit count and pre-stop tail absence");
        var uniqueIds = new HashSet<Guid>();
        foreach (var row in rows)
        {
            Require(row.Id != Guid.Empty && uniqueIds.Add(row.Id) && row.Timestamp.Kind == DateTimeKind.Utc &&
                row.Timestamp >= createdAt && row.Timestamp <= DateTime.UtcNow.AddSeconds(5) && row.EventType == 7 &&
                row.Key == fixture.KeyId && row.Group is null && row.Model is null && row.RequestId is not null &&
                expected.Remove(row.RequestId) && row.Usage is null && row.Cost is null &&
                row.Reason == "HTTP 400 error response - no billing per policy" && row.Provider == "OpenAI" &&
                row.Status == 400 && row.Metadata is null && row.Path == "/v1/chat/completions" &&
                !row.Estimated && row.Tools is null && row.ToolCost is null, "independent exact17-column audit oracle");
        }
        Require(expected.Count == 0, "every host/phase request persisted exactly once");
    }

    private static async Task<List<Audit>> AuditRowsAsync(NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "Timestamp", "EventType", "VirtualKeyId", "VirtualKeyGroupId", "Model", "RequestId", "UsageJson",
                "CalculatedCost", "FailureReason", "ProviderType", "HttpStatusCode", "MetadataJson", "RequestPath", "IsEstimated", "ToolUsageJson", "ToolUsageCost"
            FROM "BillingAuditEvents" WHERE "VirtualKeyId" = @key OR "VirtualKeyGroupId" = @group
            """;
        Scope(command, fixture);
        await using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<Audit>();
        while (await reader.ReadAsync(token)) rows.Add(new(reader.GetGuid(0), reader.GetDateTime(1), reader.GetInt32(2),
            Integer(reader, 3), Integer(reader, 4), Text(reader, 5), Text(reader, 6), Text(reader, 7), Money(reader, 8),
            Text(reader, 9), Text(reader, 10), Integer(reader, 11), Text(reader, 12), Text(reader, 13), reader.GetBoolean(14),
            Text(reader, 15), Money(reader, 16)));
        return rows;
    }

    private static async Task AssertPristineAsync(NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Balance", "LifetimeSpent",
                (SELECT count(*) FROM "FunctionExecutions" WHERE "VirtualKeyId" = @key OR "FunctionConfigurationId" = @configuration),
                (SELECT count(*) FROM "RequestLogs" WHERE "VirtualKeyId" = @key),
                (SELECT count(*) FROM "VirtualKeySpendHistory" WHERE "VirtualKeyId" = @key),
                (SELECT count(*) FROM "VirtualKeyGroupTransactions" WHERE "VirtualKeyGroupId" = @group),
                (SELECT count(*) FROM "FunctionCallAudits" WHERE "VirtualKeyId" = @key OR "FunctionConfigurationId" = @configuration)
            FROM "VirtualKeyGroups" WHERE "Id" = @group
            """;
        Scope(command, fixture);
        command.Parameters.AddWithValue("configuration", NpgsqlDbType.Integer, fixture.ConfigurationId);
        await using var reader = await command.ExecuteReaderAsync(token);
        Require(await reader.ReadAsync(token) && reader.GetDecimal(0) == 100m && reader.GetDecimal(1) == 0m &&
            Enumerable.Range(2, 5).All(index => reader.GetInt64(index) == 0) && !await reader.ReadAsync(token),
            "separate lifecycle fixture has no execution or financial effects");
    }

    private static async Task<DateTime> CreatedAtAsync(NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT "CreatedAt" FROM "VirtualKeys" WHERE "Id" = @key""";
        command.Parameters.AddWithValue("key", NpgsqlDbType.Integer, fixture.KeyId);
        return (DateTime)(await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("Lifecycle key is missing."));
    }

    private static async Task<NativeFunctionFixtureSnapshot> RequiredFixtureAsync(NpgsqlDataSource source, Guid owner, CancellationToken token) =>
        await NativeFunctionFixture.ReadAsync(source, owner, token) ?? throw new InvalidOperationException("Lifecycle fixture is missing.");
    private static async Task<JsonElement> ProviderStateAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.GetAsync("probe/state", token);
        Require(response.StatusCode == HttpStatusCode.OK, "provider counter snapshot");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.Clone();
    }
    private static async Task<Metric> MetricsAsync(HttpClient client, CancellationToken token)
    {
        using var response = await client.GetAsync("metrics", token);
        Require(response.StatusCode == HttpStatusCode.OK, "local worker metrics");
        var text = await response.Content.ReadAsStringAsync(token);
        return new(Sample(text, "conduit_audit_events_queued_total{service=\"Billing\"}"),
            Sample(text, "conduit_audit_events_flushed_total{service=\"Billing\",status=\"success\"}"),
            Sample(text, "conduit_audit_queue_depth{service=\"Billing\"}"),
            Sample(text, "conduit_audit_events_flushed_total{service=\"Billing\",status=\"failure\"}"));
    }
    private static long Sample(string text, string name)
    {
        // Unregistered worker counters have always represented a fresh zero.
        return NativeMetricSamples.TryReadNonnegativeInteger(text, name, out var value) ? value : 0;
    }
    private static string RequestId(Guid owner, string phase, string host) =>
        new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"BillingAudit:{owner:N}:{phase}:{host}"))[..16]).ToString("N");
    private static int CompletedBefore(string host) => host switch
    {
        "primary" => 0,
        "secondary" => 1,
        _ => throw new ArgumentException("Unknown lifecycle host.", nameof(host))
    };
    private static void Scope(NpgsqlCommand command, NativeFunctionFixtureSnapshot fixture)
    {
        command.Parameters.AddWithValue("key", NpgsqlDbType.Integer, fixture.KeyId);
        command.Parameters.AddWithValue("group", NpgsqlDbType.Integer, fixture.GroupId);
    }
    private static HttpClient Client(Uri uri) => new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = uri, Timeout = Timeout.InfiniteTimeSpan };
    private static string? Text(NpgsqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static int? Integer(NpgsqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
    private static decimal? Money(NpgsqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetDecimal(index);
    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Native BillingAudit lifecycle failed: " + description + ".");
    }
    private sealed record Metric(long Queued, long Flushed, long Depth, long Failures);
    private sealed record Audit(Guid Id, DateTime Timestamp, int EventType, int? Key, int? Group, string? Model,
        string? RequestId, string? Usage, decimal? Cost, string? Reason, string? Provider, int? Status, string? Metadata,
        string? Path, bool Estimated, string? Tools, decimal? ToolCost);
}
