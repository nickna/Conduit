using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Npgsql;
using NpgsqlTypes;

// Only the three exact GUID-owned names may be mutated. The alternate client
// binds a real loopback source socket; it never supplies forwarding headers.
internal static class NativeAdminIpFilterProbe
{
    private const string Path = "v1/admin/ip-filters";
    private const string CandidateIp = "127.0.0.2";
    private const string DenyDescription = "Native Admin deny café 雪 🔎";

    internal static async Task RunAsync(Uri admin, NpgsqlDataSource source, Guid owner,
        Guid functionOwner, string masterKey, CancellationToken ct)
    {
        var names = RequireOwner(source, owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKey);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        var token = deadline.Token;
        using var control = Client(admin, IPAddress.Loopback);
        using var candidate = Client(admin, IPAddress.Parse(CandidateIp));
        Snapshot? unrelated = null;
        Exception? primary = null;
        try
        {
            var fixture = await NativeFunctionFixture.ReadAsync(source, functionOwner, token)
                ?? throw new InvalidOperationException("The owned function key is required for the per-key IP proof.");
            var rows = await RowsAsync(source, token);
            Require(rows.All(row => !names.All.Contains(row.Name, StringComparer.Ordinal)), "three GUID-owned rules begin absent");
            Require(rows.Any(row => row.Key is null && row.Type == "whitelist" && row.Address == "127.0.0.1" && row.Enabled),
                "the seeded control-source allow rule is retained");
            unrelated = await UnrelatedAsync(source, names, token);

            // These two failures use the alternate IP so the existing setting
            // proof's four control-IP auth failures cannot cross the ban limit.
            foreach (var credential in new string?[] { null, $"invalid-native-admin-{owner:N}" })
                await SendAsync(candidate, HttpMethod.Get, Path + "/settings", null, credential,
                    HttpStatusCode.Unauthorized, token);
            await AssertUnrelatedAsync(source, names, unrelated, token);
            await AssertAccessAsync(candidate, masterKey, allowed: false, token);
            await AssertCheckAsync(control, masterKey, allowed: false, null, token);

            var allow = await CreateAsync(control, source, names.White, "whitelist", "Native Admin allow café 雪",
                key: null, masterKey, token);
            allow = await GetAsync(control, source, allow, masterKey, token);
            await AssertAccessAsync(candidate, masterKey, allowed: true, token);
            await AssertCheckAsync(control, masterKey, allowed: true, null, token);
            await AssertSettingsAsync(control, masterKey, token);

            var deny = await CreateAsync(control, source, names.Black, "blacklist", DenyDescription,
                key: null, masterKey, token);
            deny = await GetAsync(control, source, deny, masterKey, token);
            await AssertAccessAsync(candidate, masterKey, allowed: false, token);
            await AssertCheckAsync(control, masterKey, allowed: false, DenyDescription, token);

            var perKey = await CreateAsync(control, source, names.PerKey, "blacklist", "Native per-key deny café 雪",
                fixture.KeyId, masterKey, token);
            perKey = await GetAsync(control, source, perKey, masterKey, token);
            await AssertListsAsync(control, source, names, fixture.KeyId, masterKey, token);
            await AssertUnrelatedAsync(source, names, unrelated, token);

            foreach (var (method, version, status) in new (HttpMethod, string?, HttpStatusCode)[]
                     {
                         (HttpMethod.Patch, null, (HttpStatusCode)428),
                         (HttpMethod.Patch, "\"stale\"", HttpStatusCode.PreconditionFailed),
                         (HttpMethod.Patch, "W/" + ETag(deny), HttpStatusCode.PreconditionFailed),
                         (HttpMethod.Delete, null, (HttpStatusCode)428),
                         (HttpMethod.Delete, "\"stale\"", HttpStatusCode.PreconditionFailed)
                     })
            {
                var before = await AllAsync(source, token);
                var rejected = await SendAsync(control, method, $"{Path}/{deny.Id}",
                    method == HttpMethod.Patch ? Object(writer => writer.WriteBoolean("isEnabled", false)) : null,
                    masterKey, status, token, version);
                Require(rejected.ETag is null, "precondition failure does not emit a resource ETag");
                Require(await AllAsync(source, token) == before, "428/412 retain all IP and setting scalars");
            }
            var beforeNoOp = await AllAsync(source, token);
            var noOp = await SendAsync(control, HttpMethod.Patch, $"{Path}/{deny.Id}", Object(),
                masterKey, HttpStatusCode.OK, token, ETag(deny));
            AssertDto(noOp.Body!.Value, deny);
            Require(noOp.ETag == ETag(deny) && await AllAsync(source, token) == beforeNoOp,
                "empty merge patch retains persisted UpdatedAt, RowVersion and strong ETag");

            var oldVersion = ETag(deny);
            deny = await PatchAsync(control, source, deny, Object(writer =>
            {
                writer.WriteBoolean("isEnabled", false);
                writer.WriteNull("description");
            }), expectedEnabled: false, expectedDescription: null, masterKey, token, oldVersion);
            Require(ETag(deny) != oldVersion, "matching-version update advances the persisted fallback ETag");
            await AssertAccessAsync(candidate, masterKey, allowed: true, token);
            await AssertCheckAsync(control, masterKey, allowed: true, null, token);
            // The enabled per-key blacklist still matches the source, but must
            // never appear in or control the global Admin database rule set.
            Require((await OneAsync(source, perKey.Name!, token)).Enabled, "per-key blacklist remains enabled during global allow");
            await AssertListsAsync(control, source, names, fixture.KeyId, masterKey, token);

            var beforeStale = await AllAsync(source, token);
            await SendAsync(control, HttpMethod.Patch, $"{Path}/{deny.Id}", Object(writer => writer.WriteBoolean("isEnabled", true)),
                masterKey, HttpStatusCode.PreconditionFailed, token, oldVersion);
            Require(await AllAsync(source, token) == beforeStale, "a formerly matching strong ETag becomes stale after update");
            deny = await PatchAsync(control, source, deny, Object(writer =>
            {
                writer.WriteBoolean("isEnabled", true);
                writer.WriteString("description", DenyDescription);
            }), expectedEnabled: true, DenyDescription, masterKey, token, "*");
            await AssertAccessAsync(candidate, masterKey, allowed: false, token);
            await AssertCheckAsync(control, masterKey, allowed: false, DenyDescription, token);

            allow = await PatchAsync(control, source, allow, Object(writer => writer.WriteBoolean("isEnabled", false)),
                expectedEnabled: false, allow.Description, masterKey, token, ETag(allow));
            await AssertListsAsync(control, source, names, fixture.KeyId, masterKey, token);
            foreach (var row in new[] { perKey, deny, allow })
            {
                var deleted = await SendAsync(control, HttpMethod.Delete, $"{Path}/{row.Id}", null,
                    masterKey, HttpStatusCode.NoContent, token, ETag(row));
                Require(deleted.ETag == ETag(row), "existing DELETE returns the old strong ETag after its row is gone");
                Require((await RowsAsync(source, token)).All(current => current.Id != row.Id), "DELETE removes exactly its owned row");
                foreach (var (method, version) in new (HttpMethod, string?)[]
                         { (HttpMethod.Get, null), (HttpMethod.Patch, null), (HttpMethod.Delete, null),
                           (HttpMethod.Patch, "\"stale\""), (HttpMethod.Delete, "*") })
                {
                    var missing = await SendAsync(control, method, $"{Path}/{row.Id}",
                        method == HttpMethod.Patch ? Object() : null, masterKey, HttpStatusCode.NotFound, token, version);
                    Require(missing.ETag is null, "missing resource reaches 404 without ETag or precondition interception");
                }
            }
            await AssertAccessAsync(candidate, masterKey, allowed: false, token);
            await AssertUnrelatedAsync(source, names, unrelated, token);
            Require((await RowsAsync(source, token)).All(row => !names.All.Contains(row.Name, StringComparer.Ordinal)),
                "HTTP cleanup leaves no owned rules");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            // Repeat after producer shutdown in the parent as well: a request
            // whose HTTP outcome is unknown can outlive caller cancellation.
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await CleanupAsync(source, owner, cleanupDeadline.Token);
                if (unrelated is not null) await AssertUnrelatedAsync(source, names, unrelated, cleanupDeadline.Token);
            }
            catch (Exception cleanup) when (primary is not null)
            { throw new AggregateException("Native Admin IP proof and owned cleanup both failed.", primary, cleanup); }
        }
        Console.WriteLine("Native Admin IP filters HTTP/security: PASS");
    }

    internal static async Task CleanupAsync(NpgsqlDataSource source, Guid owner, CancellationToken ct)
    {
        var names = RequireOwner(source, owner);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var before = await UnrelatedAsync(connection, transaction, names, token);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """DELETE FROM "IpFilters" WHERE "Name" = @white OR "Name" = @black OR "Name" = @perKey""";
            AddNames(command, names);
            Require(await command.ExecuteNonQueryAsync(token) is >= 0 and <= 3, "cleanup deletes only three exact GUID-owned names");
        }
        Require((await RowsAsync(connection, transaction, token)).All(row => !names.All.Contains(row.Name, StringComparer.Ordinal)),
            "owned-name cleanup verifies absence");
        Require(await UnrelatedAsync(connection, transaction, names, token) == before,
            "cleanup retains every unrelated IP and global-setting scalar");
        await transaction.CommitAsync(token);
    }

    private static async Task<Row> CreateAsync(HttpClient client, NpgsqlDataSource source, string name, string type,
        string description, int? key, string masterKey, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var observed = await SendAsync(client, HttpMethod.Post, Path + "/", Object(writer =>
        {
            writer.WriteString("filterType", type);
            writer.WriteString("ipAddressOrCidr", CandidateIp);
            writer.WriteString("name", name);
            writer.WriteString("description", description);
            writer.WriteBoolean("isEnabled", true);
            if (key is { } id) writer.WriteNumber("virtualKeyId", id); else writer.WriteNull("virtualKeyId");
        }), masterKey, HttpStatusCode.Created, ct);
        var row = await OneAsync(source, name, ct);
        Require(row.Id > 0 && row.Type == type && row.Address == CandidateIp && row.Name == name && row.Description == description &&
            row.Enabled && row.Key == key && row.CreatedBy is null && row.UpdatedBy is null && row.Version is null &&
            row.Created >= started.AddTicks(-9) && row.Created <= row.Updated && row.Updated <= DateTime.UtcNow,
            "POST generates all twelve independently read SQL scalars with nullable audit/version defaults");
        Require(observed.Location?.OriginalString == $"/{Path}/{row.Id}" && observed.ETag is null,
            "POST Location identifies its generated resource without a route-ID ETag");
        AssertDto(observed.Body!.Value, row, tolerateWriteTicks: true);
        return row;
    }

    private static async Task<Row> GetAsync(HttpClient client, NpgsqlDataSource source, Row expected, string masterKey, CancellationToken ct)
    {
        var row = await OneAsync(source, expected.Name!, ct);
        Require(Fingerprint([row]) == Fingerprint([expected]), "GET leaves every persisted scalar unchanged");
        var observed = await SendAsync(client, HttpMethod.Get, $"{Path}/{row.Id}", null, masterKey, HttpStatusCode.OK, ct);
        AssertDto(observed.Body!.Value, row);
        Require(observed.ETag is { } actual && actual == ETag(row) && !actual.StartsWith("W/", StringComparison.Ordinal),
            "GET emits exact quoted Base64 RowVersion-or-persisted-ticks strong ETag");
        return row;
    }

    private static async Task<Row> PatchAsync(HttpClient client, NpgsqlDataSource source, Row previous, JsonElement patch,
        bool expectedEnabled, string? expectedDescription, string masterKey, CancellationToken ct, string version)
    {
        var observed = await SendAsync(client, HttpMethod.Patch, $"{Path}/{previous.Id}", patch, masterKey, HttpStatusCode.OK, ct, version);
        var row = await OneAsync(source, previous.Name!, ct);
        Require(row.Id == previous.Id && row.Type == previous.Type && row.Address == previous.Address && row.Name == previous.Name &&
            row.Description == expectedDescription && row.Enabled == expectedEnabled && row.Created == previous.Created &&
            row.Updated > previous.Updated && row.Updated <= DateTime.UtcNow && row.CreatedBy == previous.CreatedBy &&
            row.UpdatedBy == previous.UpdatedBy && row.Key == previous.Key &&
            (row.Version is null ? previous.Version is null : previous.Version is not null && row.Version.SequenceEqual(previous.Version)),
            "PATCH changes only defined scalars and advances persisted UpdatedAt");
        AssertDto(observed.Body!.Value, row);
        Require(observed.ETag == ETag(row), "PATCH returns the new persisted strong ETag");
        return row;
    }

    private static async Task AssertAccessAsync(HttpClient candidate, string masterKey, bool allowed, CancellationToken ct)
    {
        var observed = await SendAsync(candidate, HttpMethod.Get, Path + "/settings", null, masterKey,
            allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, ct);
        if (allowed) AssertSettings(observed.Body!.Value);
    }

    private static async Task AssertSettingsAsync(HttpClient control, string masterKey, CancellationToken ct) =>
        AssertSettings((await SendAsync(control, HttpMethod.Get, Path + "/settings", null, masterKey, HttpStatusCode.OK, ct)).Body!.Value);

    private static void AssertSettings(JsonElement body)
    {
        Properties(body, "isEnabled", "defaultAllow", "bypassForAdminUi", "excludedEndpoints", "filterMode", "whitelistFilters", "blacklistFilters");
        Require(body.GetProperty("isEnabled").GetBoolean() && !body.GetProperty("defaultAllow").GetBoolean() &&
            body.GetProperty("bypassForAdminUi").ValueKind is JsonValueKind.True or JsonValueKind.False &&
            body.GetProperty("filterMode").GetString() == "permissive" &&
            body.GetProperty("excludedEndpoints").EnumerateArray().All(value => value.ValueKind == JsonValueKind.String) &&
            body.GetProperty("whitelistFilters").GetArrayLength() == 0 && body.GetProperty("blacklistFilters").GetArrayLength() == 0,
            "settings GET returns its actual seven-field database/configuration contract");
    }

    private static async Task AssertCheckAsync(HttpClient control, string masterKey, bool allowed,
        string? denyDescription, CancellationToken ct)
    {
        var body = (await SendAsync(control, HttpMethod.Get, Path + "/check/" + CandidateIp, null, masterKey, HttpStatusCode.OK, ct)).Body!.Value;
        Properties(body, "isAllowed", "deniedReason");
        var expectedReason = allowed ? null : denyDescription is not null
            ? "IP address matched deny rule: " + denyDescription
            : "IP address did not match any allow rule (a whitelist is active, so all other IPs are denied)";
        Require(body.GetProperty("isAllowed").GetBoolean() == allowed && body.GetProperty("deniedReason").GetString() == expectedReason,
            "check endpoint agrees with exact database rule precedence");
    }

    private static async Task AssertListsAsync(HttpClient control, NpgsqlDataSource source, Names names,
        int key, string masterKey, CancellationToken ct)
    {
        var rows = await RowsAsync(source, ct);
        foreach (var (path, selected) in new (string, Row[])[]
                 {
                     (Path + "/", rows.Where(row => row.Key is null).ToArray()),
                     (Path + "/enabled", rows.Where(row => row.Key is null && row.Enabled).ToArray()),
                     ($"{Path}/by-virtual-key/{key}", rows.Where(row => row.Key == key).ToArray())
                 })
        {
            var pages = new HashSet<(int Page, int Size)> { (1, 50), (selected.Length + 1, 1) };
            for (var index = 0; index < selected.Length; index++)
                if (names.All.Contains(selected[index].Name, StringComparer.Ordinal)) pages.Add((index + 1, 1));
            foreach (var (page, size) in pages)
            {
                var body = (await SendAsync(control, HttpMethod.Get, $"{path}?page={page}&pageSize={size}", null,
                    masterKey, HttpStatusCode.OK, ct)).Body!.Value;
                Properties(body, "data", "pagination");
                var pagination = body.GetProperty("pagination");
                Properties(pagination, "page", "pageSize", "totalItems", "totalPages");
                Require(pagination.GetProperty("page").GetInt32() == page && pagination.GetProperty("pageSize").GetInt32() == size &&
                    pagination.GetProperty("totalItems").GetInt32() == selected.Length &&
                    pagination.GetProperty("totalPages").GetInt32() == (int)Math.Ceiling(selected.Length / (double)size),
                    "global/enabled/per-key pagination uses the independent ordered SQL selection");
                var expected = selected.Skip((page - 1) * size).Take(size).ToArray();
                var data = body.GetProperty("data").EnumerateArray().ToArray();
                Require(data.Length == expected.Length, "exact paginated filter count including empty beyond-end pages");
                for (var index = 0; index < data.Length; index++) AssertDto(data[index], expected[index]);
            }
        }
    }

    private static async Task<Observation> SendAsync(HttpClient client, HttpMethod method, string path, JsonElement? payload,
        string? masterKey, HttpStatusCode expected, CancellationToken ct, string? version = null)
    {
        var correlation = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(method, path) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        request.Headers.Add("x-request-id", correlation);
        if (masterKey is not null) request.Headers.Add("X-Master-Key", masterKey);
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        if (payload is { } body)
        {
            request.Content = JsonContent.Create(body, NativeProbeJsonContext.Default.JsonElement);
            if (method == HttpMethod.Patch) request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/merge-patch+json");
        }
        using var response = await client.SendAsync(request, ct);
        Require(response.StatusCode == expected, $"{method} IP route expected {(int)expected}, received {(int)response.StatusCode}");
        Require(response.Headers.TryGetValues("x-request-id", out var ids) && ids.SequenceEqual([correlation]), "IP responses retain request correlation");
        var text = await response.Content.ReadAsStringAsync(ct);
        var etag = response.Headers.ETag?.ToString();
        if (expected == HttpStatusCode.NoContent)
        {
            Require(text.Length == 0, "DELETE204 has no body");
            return new(null, etag, response.Headers.Location);
        }
        using var document = JsonDocument.Parse(text);
        var result = document.RootElement.Clone();
        if (expected is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            Require(response.Content.Headers.ContentType?.MediaType == "application/json", "IP security error JSON content type");
            Properties(result, "error", "code");
            Require(result.GetProperty("code").GetInt32() == (int)expected && result.GetProperty("error").GetString() ==
                (expected == HttpStatusCode.Unauthorized ? "Invalid or missing API key" : "IP address not allowed"), "exact Admin auth/IP denial envelope");
        }
        else if (expected is HttpStatusCode.PreconditionFailed || (int)expected == 428)
        {
            Require(response.Content.Headers.ContentType?.MediaType == "application/problem+json", "precondition problem content type");
            if ((int)expected == 428)
            {
                Properties(result, "title", "status", "detail", "code", "traceId");
                Require(!result.TryGetProperty("type", out _), "428 problem omits its undefined framework type");
            }
            else
            {
                Properties(result, "type", "title", "status", "detail", "code", "traceId");
                Require(!string.IsNullOrWhiteSpace(result.GetProperty("type").GetString()), "412 problem retains its framework type");
            }
            Require(result.GetProperty("status").GetInt32() == (int)expected &&
                result.GetProperty("code").GetString() == "request_failed" && result.GetProperty("traceId").GetString() == correlation &&
                result.GetProperty("title").GetString() == ((int)expected == 428 ? "If-Match is required" : "Resource version mismatch") &&
                result.GetProperty("detail").GetString() == ((int)expected == 428
                    ? "Fetch the resource and send its ETag in the If-Match header."
                    : "The resource changed after it was read. Fetch it again and retry."), "exact conditional-request problem contract");
        }
        else if (expected == HttpStatusCode.NotFound)
        {
            Require(response.Content.Headers.ContentType?.MediaType == "application/problem+json", "missing IP problem content type");
            Properties(result, "type", "title", "status", "detail", "code", "traceId");
            Require(result.GetProperty("status").GetInt32() == 404 && result.GetProperty("code").GetString() == "not_found" &&
                result.GetProperty("traceId").GetString() == correlation, "missing IP problem classification and correlation");
        }
        else Require(response.Content.Headers.ContentType?.MediaType == "application/json", "IP success JSON content type");
        return new(result, etag, response.Headers.Location);
    }

    private static void AssertDto(JsonElement body, Row row, bool tolerateWriteTicks = false)
    {
        Properties(body, "id", "filterType", "ipAddressOrCidr", "name", "description", "isEnabled", "createdAt", "updatedAt", "createdBy", "updatedBy", "virtualKeyId");
        var created = body.GetProperty("createdAt").GetDateTime();
        var updated = body.GetProperty("updatedAt").GetDateTime();
        Require(body.GetProperty("id").GetInt32() == row.Id && body.GetProperty("filterType").GetString() == row.Type &&
            body.GetProperty("ipAddressOrCidr").GetString() == row.Address && body.GetProperty("name").GetString() == (row.Name ?? string.Empty) &&
            body.GetProperty("description").GetString() == row.Description && body.GetProperty("isEnabled").GetBoolean() == row.Enabled &&
            created.Kind == DateTimeKind.Utc && updated.Kind == DateTimeKind.Utc &&
            Math.Abs(created.Ticks - row.Created.Ticks) <= (tolerateWriteTicks ? 9 : 0) &&
            Math.Abs(updated.Ticks - row.Updated.Ticks) <= (tolerateWriteTicks ? 9 : 0) &&
            body.GetProperty("createdBy").GetString() == row.CreatedBy && body.GetProperty("updatedBy").GetString() == row.UpdatedBy &&
            (body.GetProperty("virtualKeyId").ValueKind == JsonValueKind.Null ? row.Key is null : body.GetProperty("virtualKeyId").GetInt32() == row.Key),
            "all eleven DTO scalars match independently persisted IP fields");
    }

    private static string ETag(Row row) => "\"" + Convert.ToBase64String(row.Version is { Length: > 0 } ? row.Version : BitConverter.GetBytes(row.Updated.Ticks)) + "\"";

    private static HttpClient Client(Uri admin, IPAddress source)
    {
        ArgumentNullException.ThrowIfNull(admin);
        Require(admin.IsAbsoluteUri && admin.Scheme == "http" && admin.Host == "127.0.0.1" &&
            admin.AbsolutePath == "/" && admin.UserInfo.Length == 0 && admin.Query.Length == 0 && admin.Fragment.Length == 0,
            "owned Admin loopback authority only");
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, ct) =>
            {
                Require(context.DnsEndPoint.Host == "127.0.0.1" && context.DnsEndPoint.Port == admin.Port, "source-bound connection targets only owned Admin");
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(source, 0));
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, admin.Port), ct);
                    Require(socket.LocalEndPoint is IPEndPoint local && local.Address.Equals(source), "actual connected socket uses the declared source IP");
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler) { BaseAddress = admin, Timeout = Timeout.InfiniteTimeSpan };
    }

    private static JsonElement Object(Action<Utf8JsonWriter>? write = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) { writer.WriteStartObject(); write?.Invoke(writer); writer.WriteEndObject(); }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static async Task<Row> OneAsync(NpgsqlDataSource source, string name, CancellationToken ct)
    {
        var rows = (await RowsAsync(source, ct)).Where(row => row.Name == name).ToArray();
        Require(rows.Length == 1, "one independently persisted row per GUID-owned name");
        return rows[0];
    }

    private static async Task<List<Row>> RowsAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return await RowsAsync(connection, null, ct);
    }

    private static async Task<List<Row>> RowsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT "Id", "FilterType", "IpAddressOrCidr", "Name", "Description", "IsEnabled", "CreatedAt", "UpdatedAt",
                "CreatedBy", "UpdatedBy", "VirtualKeyId", "RowVersion" FROM "IpFilters" ORDER BY "FilterType", "IpAddressOrCidr"
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        Require(reader.FieldCount == 12, "independent twelve-column SQL IP oracle");
        var rows = new List<Row>();
        while (await reader.ReadAsync(ct)) rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), Text(reader, 3),
            Text(reader, 4), reader.GetBoolean(5), reader.GetDateTime(6), reader.GetDateTime(7), Text(reader, 8), Text(reader, 9),
            reader.IsDBNull(10) ? null : reader.GetInt32(10), reader.IsDBNull(11) ? null : reader.GetFieldValue<byte[]>(11)));
        Require(rows.All(row => row.Created.Kind == DateTimeKind.Utc && row.Updated.Kind == DateTimeKind.Utc), "SQL IP timestamps are UTC");
        return rows;
    }

    private static async Task<Snapshot> AllAsync(NpgsqlDataSource source, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return new(Fingerprint(await RowsAsync(connection, null, ct)), await SettingsFingerprintAsync(connection, null, ct));
    }

    private static async Task<Snapshot> UnrelatedAsync(NpgsqlDataSource source, Names names, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return await UnrelatedAsync(connection, null, names, ct);
    }

    private static async Task<Snapshot> UnrelatedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Names names, CancellationToken ct) =>
        new(Fingerprint((await RowsAsync(connection, transaction, ct)).Where(row => !names.All.Contains(row.Name, StringComparer.Ordinal))),
            await SettingsFingerprintAsync(connection, transaction, ct));

    private static async Task<string> SettingsFingerprintAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """SELECT COALESCE(jsonb_agg(to_jsonb(setting) ORDER BY "Id")::text, '[]') FROM "GlobalSettings" AS setting""";
        var value = (string)(await command.ExecuteScalarAsync(ct) ?? throw new InvalidOperationException("Setting fingerprint is missing."));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static async Task AssertUnrelatedAsync(NpgsqlDataSource source, Names names, Snapshot expected, CancellationToken ct) =>
        Require(await UnrelatedAsync(source, names, ct) == expected, "every unrelated IP and global-setting scalar remains unchanged");

    private static string Fingerprint(IEnumerable<Row> rows)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var row in rows.OrderBy(row => row.Id))
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(row.Id); writer.WriteStringValue(row.Type); writer.WriteStringValue(row.Address);
                writer.WriteStringValue(row.Name); writer.WriteStringValue(row.Description); writer.WriteBooleanValue(row.Enabled);
                writer.WriteNumberValue(row.Created.Ticks); writer.WriteNumberValue(row.Updated.Ticks);
                writer.WriteStringValue(row.CreatedBy); writer.WriteStringValue(row.UpdatedBy);
                if (row.Key is { } key) writer.WriteNumberValue(key); else writer.WriteNullValue();
                if (row.Version is null) writer.WriteNullValue(); else writer.WriteBase64StringValue(row.Version);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
    }

    private static string? Text(NpgsqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static Names RequireOwner(NpgsqlDataSource source, Guid owner)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (owner == Guid.Empty) throw new ArgumentException("A nonempty Admin IP fixture owner is required.", nameof(owner));
        return new($"native_admin_ip_{owner:N}:whitelist", $"native_admin_ip_{owner:N}:blacklist", $"native_admin_ip_{owner:N}:per-key");
    }
    private static void AddNames(NpgsqlCommand command, Names names)
    {
        command.Parameters.AddWithValue("white", NpgsqlDbType.Varchar, names.White);
        command.Parameters.AddWithValue("black", NpgsqlDbType.Varchar, names.Black);
        command.Parameters.AddWithValue("perKey", NpgsqlDbType.Varchar, names.PerKey);
    }
    private static void Properties(JsonElement value, params string[] expected) =>
        Require(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(property => property.Name)
            .Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)), "exact Admin IP JSON property set");
    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException("Native Admin IP assertion failed: " + detail);
    }
    private sealed record Names(string White, string Black, string PerKey) { public string[] All => [White, Black, PerKey]; }
    private sealed record Row(int Id, string Type, string Address, string? Name, string? Description, bool Enabled,
        DateTime Created, DateTime Updated, string? CreatedBy, string? UpdatedBy, int? Key, byte[]? Version);
    private sealed record Snapshot(string Ip, string Settings);
    private sealed record Observation(JsonElement? Body, string? ETag, Uri? Location);
}
