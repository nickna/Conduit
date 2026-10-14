using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

using Npgsql;
using NpgsqlTypes;

/// <summary>Observes both native Gateway caches through the real chat validation path.</summary>
internal static class NativeGlobalSettingsCacheConvergenceProbe
{
    private const string SettingKey = "Agentic.MaxIterations";
    private const string MinimumKey = "Agentic.MinIterations";
    private const string AdminPath = "v1/admin/global-settings";
    private const int MaximumGatewayRequests = 50;

    internal static async Task RunAsync(Uri admin, Uri primary, Uri secondary, Uri provider,
        NpgsqlDataSource source, Guid settingsOwner, Guid functionOwner, string masterKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);
        ArgumentNullException.ThrowIfNull(provider);
        var marker = RequireOwner(source, settingsOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKey);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        using var adminClient = Client(admin);
        using var first = Client(primary);
        using var second = Client(secondary);
        using var upstream = Client(provider);
        var gateways = new[] { first, second };
        var budget = new RequestBudget();
        string? unrelated = null;
        Exception? primaryFailure = null;
        try
        {
            Require(await CountFixedSettingsAsync(source, token) == 0,
                "both fixed iteration settings must initially be absent; existing settings cannot be borrowed");
            unrelated = await FingerprintUnrelatedAsync(source, token);
            var fixture = await NativeFunctionFixture.ReadAsync(source, functionOwner, token)
                ?? throw new InvalidOperationException("Native cache convergence function fixture is missing.");
            var providerBefore = await ProviderStateAsync(upstream, token);
            while (providerBefore.GetProperty("active_cancellation_requests").GetInt32() != 0)
            {
                await Task.Delay(100, token);
                providerBefore = await ProviderStateAsync(upstream, token);
            }
            await AssertPristineAsync(source, fixture, upstream, providerBefore, unrelated, token);
            var baseline = await ReadPairAsync(gateways, fixture, budget, token);
            Require(baseline == (5, 5), "both empty caches use the exact default range 1 through 5");

            // Register the marker before the request. An unknown POST outcome is
            // recovered by this exact key+description, and by parent cleanup
            // after stopping Admin, whose persistence work can outlive HTTP.
            var created = await AdminAsync(adminClient, HttpMethod.Post, AdminPath + "/", Element(writer =>
            {
                writer.WriteString("key", SettingKey);
                writer.WriteString("value", "7");
                writer.WriteString("description", marker);
            }), masterKey, HttpStatusCode.Created, token);
            var owned = await ReadOwnedAsync(source, marker, token)
                ?? throw new InvalidOperationException("Owned cache convergence setting was not persisted.");
            Require(owned.Value == "7", "ordinary Admin POST persists the configured value 7");
            AssertSetting(created.Body!.Value, owned);
            Require(created.Location?.OriginalString == $"/{AdminPath}/{owned.Id}",
                "ordinary Admin POST Location addresses its generated ID");
            Console.WriteLine($"Native global settings cache convergence SQL: owned {SettingKey}=7 (id={owned.Id}, owner={settingsOwner:N}).");

            // No reload or invalidate endpoint is invoked before this assertion.
            // Eight paired samples span at least 15 seconds if convergence fails.
            await WaitForMaximumAsync(gateways, fixture, budget, 7, 8, TimeSpan.FromMilliseconds(2250),
                "ordinary Admin creation", token);
            await AssertStablePristineAsync(source, fixture, upstream, providerBefore, unrelated, token);

            await SetOwnedValueAsync(source, owned, marker, "9", token);
            owned = await ReadOwnedAsync(source, marker, token)
                ?? throw new InvalidOperationException("Owned cache convergence setting disappeared after SQL update.");
            Require(owned.Value == "9", "direct SQL changes only the owned fixed setting to 9");
            for (var sample = 0; sample < 2; sample++)
            {
                Require(await ReadPairAsync(gateways, fixture, budget, token) == (7, 7),
                    "direct SQL without an event leaves both process-local caches at 7");
                await Task.Delay(TimeSpan.FromMilliseconds(1500), token);
            }
            await AssertPristineAsync(source, fixture, upstream, providerBefore, unrelated, token);

            await ReloadAsync(adminClient, masterKey, token);
            await WaitForMaximumAsync(gateways, fixture, budget, 9, 6, TimeSpan.FromSeconds(2),
                "explicit cluster reload", token);
            await AssertPristineAsync(source, fixture, upstream, providerBefore, unrelated, token);

            // Conditional SQL deletion deliberately sends no mutation event;
            // the explicit reload must independently restore both defaults.
            await CleanupAsync(source, settingsOwner, token);
            await ReloadAsync(adminClient, masterKey, token);
            await WaitForMaximumAsync(gateways, fixture, budget, 5, 6, TimeSpan.FromSeconds(2),
                "owned deletion and cluster reload", token);
            await AssertStablePristineAsync(source, fixture, upstream, providerBefore, unrelated, token);
            Require(await CountFixedSettingsAsync(source, token) == 0 && budget.Count <= MaximumGatewayRequests,
                "fixed settings are absent and the aggregate authenticated Gateway request budget is bounded");
        }
        catch (Exception error)
        {
            primaryFailure = error;
            throw;
        }
        finally
        {
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await CleanupAsync(source, settingsOwner, cleanupDeadline.Token);
                if (unrelated is not null)
                    Require(await FingerprintUnrelatedAsync(source, cleanupDeadline.Token) == unrelated,
                        "owned convergence cleanup preserves every unrelated setting scalar");
            }
            catch (Exception cleanup) when (primaryFailure is not null)
            {
                throw new AggregateException("Native cache convergence proof and owned cleanup both failed.", primaryFailure, cleanup);
            }
        }
        Console.WriteLine($"Native global settings cache convergence: PASS (both Gateways, ordinary Admin creation 5→7, direct SQL stays cached, explicit reload 9 and deletion restores 5; {budget.Count}/{MaximumGatewayRequests} authenticated requests, no dispatch or spend).");
    }

    internal static async Task CleanupAsync(NpgsqlDataSource source, Guid settingsOwner, CancellationToken ct)
    {
        var marker = RequireOwner(source, settingsOwner);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var unrelated = await FingerprintUnrelatedAsync(connection, transaction, token);
        var owned = await ReadOwnedAsync(connection, transaction, marker, token, lockRow: true);
        if (owned is not null)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """DELETE FROM "GlobalSettings" WHERE "Id" = @id AND "Key" = @key AND "Description" = @marker""";
            AddOwnership(command, owned.Id, marker);
            Require(await command.ExecuteNonQueryAsync(token) == 1, "cleanup conditionally deletes exactly the owned fixed-setting row");
        }
        Require(await ReadOwnedAsync(connection, transaction, marker, token) is null,
            "cleanup leaves no owned fixed-setting row");
        Require(await FingerprintUnrelatedAsync(connection, transaction, token) == unrelated,
            "cleanup preserves every unrelated six-field setting row");
        await transaction.CommitAsync(token);
    }

    private static string RequireOwner(NpgsqlDataSource source, Guid owner)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (owner == Guid.Empty) throw new ArgumentException("A nonempty cache convergence owner GUID is required.", nameof(owner));
        return $"native_global_settings_cache_{owner:N}";
    }

    private static HttpClient Client(Uri address) => new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    }) { BaseAddress = address, Timeout = Timeout.InfiniteTimeSpan };

    private static JsonElement Element(Action<Utf8JsonWriter> contents)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            contents(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static async Task<(int Primary, int Secondary)> ReadPairAsync(HttpClient[] gateways,
        NativeFunctionFixtureSnapshot fixture, RequestBudget budget, CancellationToken token) =>
        (await ReadMaximumAsync(gateways[0], fixture, budget, token),
            await ReadMaximumAsync(gateways[1], fixture, budget, token));

    private static async Task<int> ReadMaximumAsync(HttpClient gateway, NativeFunctionFixtureSnapshot fixture,
        RequestBudget budget, CancellationToken token)
    {
        Require(++budget.Count <= MaximumGatewayRequests, "aggregate authenticated Gateway request budget cannot exceed 50");
        var correlation = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.VirtualKey);
        request.Headers.Add("x-request-id", correlation);
        request.Content = JsonContent.Create(Element(writer =>
        {
            writer.WriteString("model", NativeParityFixture.ModelAlias);
            writer.WriteStartArray("messages");
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", "Native settings cache preflight café 雪 🔎");
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteStartArray("function_configuration_ids");
            writer.WriteNumberValue(fixture.ConfigurationId);
            writer.WriteEndArray();
            writer.WriteNumber("max_agentic_iterations", 0);
            writer.WriteBoolean("enable_agentic_mode", false);
            writer.WriteBoolean("stream", false);
        }), NativeProbeJsonContext.Default.JsonElement);
        using var response = await gateway.SendAsync(request, token);
        Require(response.StatusCode == HttpStatusCode.BadRequest,
            $"cache preflight on {gateway.BaseAddress!.Authority} expected 400, received {(int)response.StatusCode}");
        AssertCorrelation(response, correlation);
        var body = await JsonAsync(response, token);
        Properties(body, "error");
        var error = body.GetProperty("error");
        Properties(error, "message", "type", "code");
        Require(error.GetProperty("type").GetString() == "invalid_request_error" &&
            error.GetProperty("code").GetString() == "invalid_max_agentic_iterations",
            "preflight reaches only the enabled-function iteration-range validation");
        var text = error.GetProperty("message").GetString();
        const string prefix = "MaxAgenticIterations must be between 1 and ";
        Require(text is not null && text.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(text.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var maximum) &&
            maximum is >= 1 and <= 100,
            "400 response exposes the exact current minimum 1 and clamped integer maximum");
        return int.Parse(text!.AsSpan(prefix.Length), CultureInfo.InvariantCulture);
    }

    private static async Task WaitForMaximumAsync(HttpClient[] gateways, NativeFunctionFixtureSnapshot fixture,
        RequestBudget budget, int expected, int attempts, TimeSpan interval, string stage, CancellationToken token)
    {
        var observed = (Primary: -1, Secondary: -1);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            observed = await ReadPairAsync(gateways, fixture, budget, token);
            if (observed == (expected, expected)) return;
            if (attempt + 1 < attempts) await Task.Delay(interval, token);
        }
        throw new InvalidOperationException($"Native global settings cache convergence failed at {stage}: " +
            $"primary={observed.Primary}, secondary={observed.Secondary}, expected={expected}.");
    }

    private static async Task ReloadAsync(HttpClient admin, string masterKey, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        var response = await AdminAsync(admin, HttpMethod.Post, AdminPath + "/cache/reload", null,
            masterKey, HttpStatusCode.Accepted, token);
        var body = response.Body!.Value;
        Properties(body, "message", "requestId", "acceptedAt");
        Require(body.GetProperty("message").GetString() ==
            "Global settings cache reload was accepted for all Admin and Gateway instances." &&
            body.GetProperty("requestId").GetGuid() != Guid.Empty &&
            body.GetProperty("acceptedAt").GetDateTime() is { Kind: DateTimeKind.Utc } accepted &&
            accepted >= started && accepted <= DateTime.UtcNow,
            "explicit reload returns the exact correlated 202 protocol and a UTC acknowledgement");
    }

    private static async Task<AdminResponse> AdminAsync(HttpClient client, HttpMethod method, string path,
        JsonElement? payload, string masterKey, HttpStatusCode expected, CancellationToken token)
    {
        var correlation = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Master-Key", masterKey);
        request.Headers.Add("x-request-id", correlation);
        if (payload is { } body) request.Content = JsonContent.Create(body, NativeProbeJsonContext.Default.JsonElement);
        using var response = await client.SendAsync(request, token);
        Require(response.StatusCode == expected, $"cache Admin route expected {(int)expected}, received {(int)response.StatusCode}");
        AssertCorrelation(response, correlation);
        return new AdminResponse(await JsonAsync(response, token), response.Headers.Location);
    }

    private static void AssertCorrelation(HttpResponseMessage response, string correlation) =>
        Require(response.Headers.TryGetValues("x-request-id", out var ids) && ids.SequenceEqual([correlation]),
            "cache proof retains the supplied request correlation ID");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        Require(response.Content.Headers.ContentType?.MediaType == "application/json", "cache proof response JSON content type");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> ProviderStateAsync(HttpClient provider, CancellationToken token)
    {
        using var response = await provider.GetAsync("probe/state", token);
        Require(response.StatusCode == HttpStatusCode.OK, "provider counters remain available during cache proof");
        return await JsonAsync(response, token);
    }

    private static async Task<long> CountFixedSettingsAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT count(*) FROM "GlobalSettings" WHERE "Key" = @key OR "Key" = @minimum""";
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, SettingKey);
        command.Parameters.AddWithValue("minimum", NpgsqlDbType.Varchar, MinimumKey);
        return (long)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task<Setting?> ReadOwnedAsync(NpgsqlDataSource source, string marker, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        return await ReadOwnedAsync(connection, null, marker, token);
    }

    private static async Task<Setting?> ReadOwnedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string marker, CancellationToken token, bool lockRow = false)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT \"Id\", \"Key\", \"Value\", \"Description\", \"CreatedAt\", \"UpdatedAt\" " +
            "FROM \"GlobalSettings\" WHERE \"Key\" = @key" + (lockRow ? " FOR UPDATE" : string.Empty);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, SettingKey);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        var row = new Setting(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetDateTime(4), reader.GetDateTime(5));
        Require(row.Id > 0 && row.Key == SettingKey && row.Description == marker &&
            row.CreatedAt.Kind == DateTimeKind.Utc && row.UpdatedAt.Kind == DateTimeKind.Utc && row.CreatedAt <= row.UpdatedAt,
            "fixed-setting ownership requires the exact key, positive ID, GUID description and coherent UTC timestamps");
        Require(!await reader.ReadAsync(token), "one fixed-setting row exists");
        return row;
    }

    private static void AssertSetting(JsonElement body, Setting row)
    {
        Properties(body, "id", "key", "value", "description", "createdAt", "updatedAt");
        Require(body.GetProperty("id").GetInt32() == row.Id && body.GetProperty("key").GetString() == row.Key &&
            body.GetProperty("value").GetString() == row.Value && body.GetProperty("description").GetString() == row.Description &&
            body.GetProperty("createdAt").GetDateTime() == row.CreatedAt && body.GetProperty("updatedAt").GetDateTime() == row.UpdatedAt,
            "all six Admin creation DTO fields match the independent owned PostgreSQL row");
    }

    private static void AddOwnership(NpgsqlCommand command, int id, string marker)
    {
        command.Parameters.AddWithValue("id", NpgsqlDbType.Integer, id);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, SettingKey);
        command.Parameters.AddWithValue("marker", NpgsqlDbType.Varchar, marker);
    }

    private static async Task SetOwnedValueAsync(NpgsqlDataSource source, Setting row, string marker,
        string value, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """UPDATE "GlobalSettings" SET "Value" = @value, "UpdatedAt" = CURRENT_TIMESTAMP WHERE "Id" = @id AND "Key" = @key AND "Description" = @marker AND "Value" = '7'""";
        AddOwnership(command, row.Id, marker);
        command.Parameters.AddWithValue("value", NpgsqlDbType.Varchar, value);
        Require(await command.ExecuteNonQueryAsync(token) == 1, "direct SQL conditionally updates exactly the owned value 7 to 9");
    }

    private static async Task<string> FingerprintUnrelatedAsync(NpgsqlDataSource source, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        return await FingerprintUnrelatedAsync(connection, null, token);
    }

    private static async Task<string> FingerprintUnrelatedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT \"Id\", \"Key\", \"Value\", \"Description\", \"CreatedAt\", \"UpdatedAt\" " +
            "FROM \"GlobalSettings\" WHERE \"Key\" <> @key ORDER BY \"Key\", \"Id\"";
        command.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, SettingKey);
        await using var reader = await command.ExecuteReaderAsync(token);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            while (await reader.ReadAsync(token))
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(reader.GetInt32(0));
                writer.WriteStringValue(reader.GetString(1));
                writer.WriteStringValue(reader.GetString(2));
                if (reader.IsDBNull(3)) writer.WriteNullValue(); else writer.WriteStringValue(reader.GetString(3));
                writer.WriteStringValue(reader.GetDateTime(4));
                writer.WriteStringValue(reader.GetDateTime(5));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    private static async Task AssertPristineAsync(NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture,
        HttpClient provider, JsonElement providerBefore, string unrelated, CancellationToken token)
    {
        await using (var connection = await source.OpenConnectionAsync(token))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT (SELECT count(*) FROM "FunctionExecutions" WHERE "VirtualKeyId" = @keyId),
                    (SELECT count(*) FROM "RequestLogs" WHERE "VirtualKeyId" = @keyId),
                    (SELECT count(*) FROM "VirtualKeySpendHistory" WHERE "VirtualKeyId" = @keyId),
                    (SELECT count(*) FROM "VirtualKeyGroupTransactions" WHERE "VirtualKeyGroupId" = @groupId),
                    "Balance", "LifetimeSpent", "LifetimeCreditsAdded"
                FROM "VirtualKeyGroups" WHERE "Id" = @groupId
                """;
            command.Parameters.AddWithValue("keyId", NpgsqlDbType.Integer, fixture.KeyId);
            command.Parameters.AddWithValue("groupId", NpgsqlDbType.Integer, fixture.GroupId);
            await using var reader = await command.ExecuteReaderAsync(token);
            Require(await reader.ReadAsync(token) && reader.GetInt64(0) == 0 && reader.GetInt64(1) == 0 &&
                reader.GetInt64(2) == 0 && reader.GetInt64(3) == 0 && reader.GetDecimal(4) == 100m &&
                reader.GetDecimal(5) == 0m && reader.GetDecimal(6) == 100m,
                "cache preflight creates no executions, logs, spend history, ledger changes or balance mutations");
            Require(!await reader.ReadAsync(token), "one owned function accounting group remains");
        }
        Require(JsonElement.DeepEquals(providerBefore, await ProviderStateAsync(provider, token)),
            "cache proof leaves every provider counter unchanged");
        Require(await FingerprintUnrelatedAsync(source, token) == unrelated,
            "cache proof preserves every unrelated global-setting scalar");
    }

    private static async Task AssertStablePristineAsync(NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture,
        HttpClient provider, JsonElement providerBefore, string unrelated, CancellationToken token)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        do
        {
            await AssertPristineAsync(source, fixture, provider, providerBefore, unrelated, token);
            await Task.Delay(200, token);
        } while (DateTime.UtcNow < until);
    }

    private static void Properties(JsonElement body, params string[] names) =>
        Require(body.ValueKind == JsonValueKind.Object && body.EnumerateObject().Select(property => property.Name)
            .Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)), "exact cache probe JSON property set");

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Native global settings cache convergence: " + message);
    }

    private sealed class RequestBudget { internal int Count; }
    private sealed record Setting(int Id, string Key, string Value, string? Description, DateTime CreatedAt, DateTime UpdatedAt);
    private sealed record AdminResponse(JsonElement? Body, Uri? Location);
}
