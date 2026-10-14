using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Npgsql;
using NpgsqlTypes;

using StackExchange.Redis;

/// <summary>Proves excluded Admin management boundaries and sequential ephemeral authentication.</summary>
internal static class NativeAdminRuntimeProfileProbe
{
    private const int MissingId = int.MaxValue;
    private const string Detail = "This management operation is not supported by the native-aot runtime profile.";
    private static readonly string[] ProviderCounters =
    [
        "non_stream_requests", "stream_requests", "image_requests", "error_requests", "authorization_failures",
        "active_cancellation_requests", "cancellations_observed", "function_requests", "function_error_requests",
        "function_authorization_failures", "function_chunked_requests", "agentic_tool_call_requests",
        "agentic_final_requests", "agentic_contract_failures"
    ];

    internal static async Task RunAsync(Uri admin, Uri provider, NpgsqlDataSource source, Guid functionOwner,
        string redisConnectionString, string masterKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(redisConnectionString);
        Require(functionOwner != Guid.Empty, "a nonempty owned function fixture is required");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        var token = deadline.Token;
        using var control = AdminClient(admin, IPAddress.Loopback);
        using var candidate = AdminClient(admin, IPAddress.Parse("127.0.0.3"));
        using var upstream = Client(provider);
        ConnectionMultiplexer? redis = null;
        RedisKey? ownedTokenKey = null;
        Exception? primary = null;
        var stage = "redis-connection";
        var budget = new RequestBudget();
        try
        {
            var options = ConfigurationOptions.Parse(redisConnectionString);
            options.ConnectTimeout = options.SyncTimeout = options.AsyncTimeout = 5000;
            options.ConnectRetry = 0;
            options.AbortOnConnectFail = true;
            options.ClientName = $"native-admin-runtime-profile:{functionOwner:N}";
            // A bounded synchronous acquisition cannot abandon an eventual undisposed ConnectAsync result.
            redis = ConnectionMultiplexer.Connect(options);
            var cache = redis.GetDatabase();
            token.ThrowIfCancellationRequested();
            stage = "preflight";
            var fixture = await NativeFunctionFixture.ReadAsync(source, functionOwner, token)
                ?? throw new InvalidOperationException("The owned function fixture is missing.");
            var before = await SnapshotAsync(source, fixture, token);
            var providerBefore = await ProviderStateAsync(upstream, token);

            stage = "excluded-management";
            await SendAsync(control, budget, HttpMethod.Patch, $"v1/admin/virtual-keys/{MissingId}", masterKey,
                HttpStatusCode.NotImplemented, token, "{}", "application/merge-patch+json");
            await SendAsync(control, budget, HttpMethod.Delete, $"v1/admin/virtual-key-groups/{MissingId}", masterKey,
                HttpStatusCode.NotImplemented, token);
            await SendAsync(control, budget, HttpMethod.Get, "v1/admin/provider-tools/", masterKey,
                HttpStatusCode.NotImplemented, token);
            await SendAsync(control, budget, HttpMethod.Put, "v1/admin/routing-configurations/routing/defaults", masterKey,
                HttpStatusCode.NotImplemented, token, "{not-json", "application/json");
            await SendAsync(control, budget, HttpMethod.Delete, $"v1/admin/media-retention-policies/{MissingId}", masterKey,
                HttpStatusCode.NotImplemented, token);
            await SendAsync(control, budget, HttpMethod.Get, "v1/admin/health-status/services", masterKey,
                HttpStatusCode.NotImplemented, token);

            stage = "anonymous-security";
            await SendAsync(candidate, budget, HttpMethod.Put, "v1/admin/routing-configurations/routing/defaults", null,
                HttpStatusCode.Unauthorized, token, "{not-json", "application/json");

            stage = "ephemeral-issuance";
            var started = DateTimeOffset.UtcNow;
            var issued = await SendAsync(control, budget, HttpMethod.Post,
                "v1/admin/auth-tokens/ephemeral-master-key", masterKey, HttpStatusCode.OK, token);
            var completed = DateTimeOffset.UtcNow;
            Properties(issued, "ephemeralMasterKey", "expiresAt", "expiresInSeconds");
            var ephemeral = issued.GetProperty("ephemeralMasterKey").GetString();
            Require(ephemeral is { Length: 47 } && ephemeral.StartsWith("emk_", StringComparison.Ordinal) &&
                ephemeral.AsSpan(4).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan()) < 0,
                "the private issued credential has the exact URL-safe token format");
            ownedTokenKey = (RedisKey)("conduit:ephemeral:master:" + ephemeral);
            var expiresAt = issued.GetProperty("expiresAt").GetDateTimeOffset();
            Require(issued.GetProperty("expiresInSeconds").GetInt32() == 300 && expiresAt.Offset == TimeSpan.Zero &&
                expiresAt >= started.AddSeconds(300) && expiresAt <= completed.AddSeconds(300),
                "the generated credential has the actual five-minute UTC expiry");
            Require(await cache.KeyExistsAsync(ownedTokenKey.Value).WaitAsync(token),
                "the exact newly issued credential exists in the configured Admin Redis cache");

            stage = "ephemeral-consumption";
            var definitions = await SendAsync(control, budget, HttpMethod.Get, "v1/admin/global-settings/definitions",
                ephemeral, HttpStatusCode.OK, token, bearer: true);
            AssertDefinitions(definitions);
            await WaitForTokenRemovalAsync(cache, ownedTokenKey.Value, token);

            stage = "ephemeral-sequential-replay";
            await SendAsync(candidate, budget, HttpMethod.Get, "v1/admin/global-settings/definitions",
                ephemeral, HttpStatusCode.Unauthorized, token);
            Require(!await cache.KeyExistsAsync(ownedTokenKey.Value).WaitAsync(token),
                "sequential replay leaves the exact consumed credential absent");

            stage = "unchanged-oracles";
            Require(budget.Count == 10, "exactly ten Admin requests exercised this boundary proof");
            Require(await SnapshotAsync(source, fixture, token) == before,
                "Admin boundary requests preserve management counts, settings, IP rows and owned accounting");
            Require(JsonElement.DeepEquals(providerBefore, await ProviderStateAsync(upstream, token)),
                "all fourteen actual provider counters remain unchanged");
        }
        catch
        {
            // Only a closed stage is retained; credential-bearing bodies and infrastructure exceptions are private.
            primary = new InvalidOperationException($"Native Admin runtime profile proof failed; stage={stage}.");
            throw primary;
        }
        finally
        {
            var cleanupFailed = false;
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (redis is not null && ownedTokenKey is { } key)
                {
                    var cache = redis.GetDatabase();
                    await cache.KeyDeleteAsync(key).WaitAsync(cleanupDeadline.Token);
                    Require(!await cache.KeyExistsAsync(key).WaitAsync(cleanupDeadline.Token),
                        "cleanup leaves only the exact privately owned credential absent");
                }
            }
            catch { cleanupFailed = true; }
            if (redis is not null)
            {
                // Connection shutdown is attempted independently even when exact-key cleanup failed.
                try { await redis.CloseAsync(allowCommandsToComplete: false).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { cleanupFailed = true; }
                finally
                {
                    try { redis.Dispose(); }
                    catch { cleanupFailed = true; }
                }
            }
            if (cleanupFailed)
            {
                var failure = new InvalidOperationException("Native Admin runtime profile owned credential cleanup failed.");
                if (primary is not null)
                    throw new AggregateException("Native Admin runtime profile proof and cleanup failed.", primary, failure);
                throw failure;
            }
        }
        Console.WriteLine("Native Admin runtime profile boundary and ephemeral authentication: PASS");
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, RequestBudget budget, HttpMethod method,
        string path, string? credential, HttpStatusCode expected, CancellationToken ct,
        string? body = null, string? mediaType = null, bool bearer = false)
    {
        budget.Take();
        var correlation = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("x-request-id", correlation);
        if (credential is not null)
        {
            if (bearer) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            else request.Headers.Add("X-Master-Key", credential);
        }
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, mediaType!);
        // No Accept, If-Match, proxy or forged source-address header is supplied.
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Require(response.StatusCode == expected, "the controlled Admin request returns its exact expected status");
        Require(response.Headers.TryGetValues("x-request-id", out var ids) && ids.SequenceEqual([correlation]),
            "one response request identifier preserves the supplied correlation");
        var value = await JsonAsync(response, ct);
        if (expected == HttpStatusCode.NotImplemented)
        {
            Require(response.Content.Headers.ContentType?.MediaType == "application/problem+json",
                "the explicit generated Admin problem media type is preserved");
            Properties(value, "type", "title", "status", "detail", "code", "traceId");
            Require(value.GetProperty("type").GetString() == "https://httpstatuses.com/501" &&
                value.GetProperty("title").GetString() == "Not Implemented" && value.GetProperty("status").GetInt32() == 501 &&
                value.GetProperty("detail").GetString() == Detail && value.GetProperty("code").GetString() == "not_implemented" &&
                value.GetProperty("traceId").GetString() == correlation,
                "all six generated problem fields classify the native boundary and retain correlation");
        }
        else
        {
            Require(response.Content.Headers.ContentType?.MediaType == "application/json", "Admin JSON media type is preserved");
            if (expected == HttpStatusCode.Unauthorized)
            {
                Properties(value, "error", "code");
                Require(value.GetProperty("error").GetString() == "Invalid or missing API key" &&
                    value.GetProperty("code").GetInt32() == 401, "security rejects before management/body handling");
            }
        }
        return value;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await input.ReadAsync(chunk, ct)) != 0)
        {
            Require(buffer.Length + read <= 65536, "the controlled JSON response remains within its finite bound");
            buffer.Write(chunk, 0, read);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static void AssertDefinitions(JsonElement value)
    {
        Properties(value, "data", "pagination");
        var pagination = value.GetProperty("pagination");
        Properties(pagination, "page", "pageSize", "totalItems", "totalPages");
        var data = value.GetProperty("data");
        Require(data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 &&
            pagination.GetProperty("page").GetInt32() == 1 && pagination.GetProperty("pageSize").GetInt32() == 50 &&
            pagination.GetProperty("totalItems").GetInt32() == data.GetArrayLength() &&
            pagination.GetProperty("totalPages").GetInt32() == 1, "the selected definition GET retains its actual pagination envelope");
        var maximum = data.EnumerateArray().Where(item => item.GetProperty("key").GetString() == "Agentic.MaxIterations").ToArray();
        Require(maximum.Length == 1 && maximum[0].GetProperty("type").GetString() == "integer" &&
            maximum[0].GetProperty("defaultValue").GetString() == "5" && maximum[0].GetProperty("minimum").GetDecimal() == 1m &&
            maximum[0].GetProperty("maximum").GetDecimal() == 100m,
            "ephemeral authentication reaches the unchanged generated setting-definition contract");
    }

    private static async Task WaitForTokenRemovalAsync(IDatabase cache, RedisKey key, CancellationToken ct)
    {
        using var removalDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        removalDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (await cache.KeyExistsAsync(key).WaitAsync(removalDeadline.Token))
            await Task.Delay(50, removalDeadline.Token);
    }

    private static async Task<JsonElement> ProviderStateAsync(HttpClient provider, CancellationToken ct)
    {
        using var response = await provider.GetAsync("probe/state", HttpCompletionOption.ResponseHeadersRead, ct);
        Require(response.StatusCode == HttpStatusCode.OK, "the owned provider exposes its actual counters");
        var value = await JsonAsync(response, ct);
        Properties(value, ProviderCounters);
        foreach (var name in ProviderCounters) Require(value.GetProperty(name).GetInt64() >= 0, "provider counters are nonnegative integers");
        Require(value.GetProperty("active_cancellation_requests").GetInt64() == 0, "no unrelated provider request is active during the boundary proof");
        return value;
    }

    private static async Task<Snapshot> SnapshotAsync(NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT count(*) FROM "FunctionExecutions" WHERE "VirtualKeyId" = @keyId),
                (SELECT count(*) FROM "RequestLogs" WHERE "VirtualKeyId" = @keyId),
                (SELECT count(*) FROM "VirtualKeySpendHistory" WHERE "VirtualKeyId" = @keyId),
                (SELECT count(*) FROM "VirtualKeyGroupTransactions" WHERE "VirtualKeyGroupId" = @groupId),
                "Balance", "LifetimeSpent", "LifetimeCreditsAdded",
                (SELECT count(*) FROM "Providers"), (SELECT count(*) FROM "ProviderTools"),
                (SELECT count(*) FROM "VirtualKeys"), (SELECT count(*) FROM "VirtualKeyGroups"),
                (SELECT count(*) FROM "MediaRetentionPolicies"),
                (SELECT md5(COALESCE(jsonb_agg(to_jsonb(s) ORDER BY s."Id")::text, '[]')) FROM "GlobalSettings" s),
                (SELECT md5(COALESCE(jsonb_agg(to_jsonb(i) ORDER BY i."Id")::text, '[]')) FROM "IpFilters" i),
                NOT EXISTS(SELECT 1 FROM "VirtualKeys" WHERE "Id" = @missing),
                NOT EXISTS(SELECT 1 FROM "VirtualKeyGroups" WHERE "Id" = @missing),
                NOT EXISTS(SELECT 1 FROM "MediaRetentionPolicies" WHERE "Id" = @missing)
            FROM "VirtualKeyGroups" WHERE "Id" = @groupId
            """;
        command.Parameters.AddWithValue("keyId", NpgsqlDbType.Integer, fixture.KeyId);
        command.Parameters.AddWithValue("groupId", NpgsqlDbType.Integer, fixture.GroupId);
        command.Parameters.AddWithValue("missing", NpgsqlDbType.Integer, MissingId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Require(await reader.ReadAsync(ct), "the verified owned accounting group exists");
        Require(reader.GetBoolean(14) && reader.GetBoolean(15) && reader.GetBoolean(16),
            "mutation boundary target identifiers are absent and cannot belong to an existing row");
        var value = new Snapshot(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetDecimal(4), reader.GetDecimal(5), reader.GetDecimal(6), reader.GetInt64(7), reader.GetInt64(8),
            reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetString(12), reader.GetString(13));
        Require(!await reader.ReadAsync(ct), "the owned accounting group is unambiguous");
        return value;
    }

    private static HttpClient AdminClient(Uri admin, IPAddress address)
    {
        ValidateUri(admin);
        Require(address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.Parse("127.0.0.3")),
            "only the seeded control or fresh authentication-negative source is accepted");
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, ct) =>
            {
                Require(context.DnsEndPoint.Host == "127.0.0.1" && context.DnsEndPoint.Port == admin.Port,
                    "the source-bound socket targets only the owned Admin authority");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(address, 0));
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, admin.Port), ct);
                    Require(socket.LocalEndPoint is IPEndPoint local && local.Address.Equals(address),
                        "the actual source-bound socket uses its declared loopback address");
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler) { BaseAddress = admin, Timeout = Timeout.InfiniteTimeSpan };
    }

    private static HttpClient Client(Uri uri)
    {
        ValidateUri(uri);
        return new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) })
        { BaseAddress = uri, Timeout = Timeout.InfiniteTimeSpan };
    }

    private static void ValidateUri(Uri uri) => Require(uri.IsAbsoluteUri && uri.Scheme == "http" && uri.Host == "127.0.0.1" &&
        uri.AbsolutePath == "/" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0,
        "only an owned loopback HTTP authority is accepted");

    private static void Properties(JsonElement value, params string[] names) => Require(value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)
            .SequenceEqual(names.OrderBy(name => name, StringComparer.Ordinal)), "the generated JSON property set is exact and unique");

    private static void Require(bool condition, string invariant)
    {
        if (!condition) throw new InvalidOperationException("Native Admin runtime profile: " + invariant + ".");
    }

    private sealed record Snapshot(long Executions, long Logs, long SpendRows, long Transactions, decimal Balance,
        decimal Spent, decimal Credits, long Providers, long ProviderTools, long Keys, long Groups, long MediaPolicies,
        string SettingsFingerprint, string IpFingerprint);

    private sealed class RequestBudget
    {
        internal int Count { get; private set; }
        internal void Take() { Require(++Count <= 10, "the fixed ten-request budget cannot be exceeded"); }
    }
}
