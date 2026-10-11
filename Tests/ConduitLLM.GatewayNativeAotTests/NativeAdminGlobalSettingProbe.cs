using System.Buffers.Binary;
using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Npgsql;
using NpgsqlTypes;

/// <summary>Exercises native Admin setting CRUD using exactly two GUID-owned keys.</summary>
internal static class NativeAdminGlobalSettingProbe
{
    private const string Path = "v1/admin/global-settings";
    private const string ProtectedKey = "WebAdmin_VirtualKey";

    internal static async Task RunAsync(Uri admin, NpgsqlDataSource source, Guid owner,
        string masterKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        var names = RequireOwner(source, owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterKey);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = admin,
            Timeout = Timeout.InfiniteTimeSpan
        };
        string? unrelated = null;
        Exception? primary = null;
        try
        {
            unrelated = Fingerprint(await ReadRowsAsync(source, names, Selection.Unrelated, token));
            Require((await ReadRowsAsync(source, names, Selection.Owned, token)).Count == 0, "both GUID-owned setting keys begin absent");
            foreach (var credential in new string?[] { null, $"native-admin-invalid-{owner:N}" })
            {
                var before = await AllFingerprintAsync(source, names, token);
                await SendAsync(client, HttpMethod.Post, Path + "/", SettingBody(names.First, "unauthorized", null),
                    credential, HttpStatusCode.Unauthorized, token);
                await SendAsync(client, HttpMethod.Get, ByKey(names.First), null,
                    credential, HttpStatusCode.Unauthorized, token);
                Require(await AllFingerprintAsync(source, names, token) == before, "anonymous/invalid master keys cannot read or create settings");
            }

            await SendAsync(client, HttpMethod.Get, ByKey(names.First), null, masterKey, HttpStatusCode.NotFound, token);
            var started = DateTime.UtcNow;
            var created = await SendAsync(client, HttpMethod.Post, Path + "/",
                SettingBody(names.First, "initial café 雪 🔎", "first description 雪"), masterKey, HttpStatusCode.Created, token);
            var first = await OwnedRowAsync(source, names, names.First, token);
            Require(first.Id > 0 && first.Value == "initial café 雪 🔎" && first.Description == "first description 雪" &&
                first.CreatedAt >= started && first.CreatedAt <= first.UpdatedAt && first.UpdatedAt <= DateTime.UtcNow,
                "POST generates a positive identifier and coherent persisted UTC timestamps");
            Require(created.Location?.OriginalString == $"/{Path}/{first.Id}", "POST Location addresses its generated ID");
            AssertDto(created.Body!.Value, first);
            await AssertGetAsync(client, source, names, first, masterKey, token);
            var bearerRead = await SendAsync(client, HttpMethod.Get, ByKey(first.Key), null,
                masterKey, HttpStatusCode.OK, token, bearer: true);
            AssertDto(bearerRead.Body!.Value, first);
            await AssertUnrelatedAsync(source, names, unrelated, token);

            var beforeDuplicate = await AllFingerprintAsync(source, names, token);
            await SendAsync(client, HttpMethod.Post, Path + "/", SettingBody(names.First, "duplicate", "different"),
                masterKey, HttpStatusCode.BadRequest, token);
            Require(await AllFingerprintAsync(source, names, token) == beforeDuplicate, "duplicate POST rejects before any scalar changes");

            var unchanged = await SendAsync(client, HttpMethod.Patch, $"{Path}/{first.Id}", Object(), masterKey,
                HttpStatusCode.OK, token, mergePatch: true);
            var noOp = await OwnedRowAsync(source, names, names.First, token);
            Require(noOp == first, "empty merge patch preserves all six scalars including UpdatedAt");
            AssertDto(unchanged.Body!.Value, noOp);

            first = await PatchAsync(client, source, names, first, Object(writer => writer.WriteString("description", "changed café 雪")),
                first.Value, "changed café 雪", masterKey, token);
            first = await PatchAsync(client, source, names, first, Object(writer => writer.WriteNull("description")),
                first.Value, null, masterKey, token);
            first = await PatchAsync(client, source, names, first, Object(writer => writer.WriteString("value", "updated value 雪 🔎")),
                "updated value 雪 🔎", null, masterKey, token);
            var beforeNull = await AllFingerprintAsync(source, names, token);
            await SendAsync(client, HttpMethod.Patch, $"{Path}/{first.Id}", Object(writer => writer.WriteNull("value")),
                masterKey, HttpStatusCode.BadRequest, token, mergePatch: true);
            Require(await AllFingerprintAsync(source, names, token) == beforeNull, "explicit null value rejects without persistence changes");

            var upsertStarted = DateTime.UtcNow;
            await SendAsync(client, HttpMethod.Put, Path + "/by-key", SettingBody(names.Second, "second café 雪", "retained description 🔎"),
                masterKey, HttpStatusCode.NoContent, token);
            var second = await OwnedRowAsync(source, names, names.Second, token);
            Require(second.Id > 0 && second.Id != first.Id && second.Value == "second café 雪" &&
                second.Description == "retained description 🔎" && second.CreatedAt >= upsertStarted &&
                second.CreatedAt == second.UpdatedAt, "PUT by-key creates a separate owned row with generated ID and equal UTC timestamps");
            var beforePut = second;
            await SendAsync(client, HttpMethod.Put, Path + "/by-key", Object(writer =>
            {
                writer.WriteString("key", names.Second);
                writer.WriteString("value", "second update 雪");
            }), masterKey, HttpStatusCode.NoContent, token);
            second = await OwnedRowAsync(source, names, names.Second, token);
            AssertMutation(second, beforePut, "second update 雪", beforePut.Description);
            beforePut = second;
            await SendAsync(client, HttpMethod.Put, Path + "/by-key", SettingBody(names.Second, "second explicit null café", null),
                masterKey, HttpStatusCode.NoContent, token);
            second = await OwnedRowAsync(source, names, names.Second, token);
            AssertMutation(second, beforePut, "second explicit null café", beforePut.Description);
            await AssertGetAsync(client, source, names, first, masterKey, token);
            await AssertGetAsync(client, source, names, second, masterKey, token);
            await AssertListAsync(client, source, names, masterKey, token);
            await AssertUnrelatedAsync(source, names, unrelated, token);

            foreach (var (method, path, body) in new (HttpMethod, string, JsonElement?)[]
                     {
                         (HttpMethod.Put, Path + "/by-key", SettingBody("Agentic.MaxIterations", "0", null)),
                         (HttpMethod.Post, Path + "/", SettingBody(ProtectedKey, "rejected fixture value", null)),
                         (HttpMethod.Delete, ByKey(ProtectedKey), null)
                     })
            {
                var before = await AllFingerprintAsync(source, names, token);
                await SendAsync(client, method, path, body, masterKey, HttpStatusCode.BadRequest, token);
                Require(await AllFingerprintAsync(source, names, token) == before,
                    "invalid typed and protected create/delete requests reject before writing any setting");
            }

            var beforeCache = await AllFingerprintAsync(source, names, token);
            await SendAsync(client, HttpMethod.Post, $"{Path}/cache/invalidate/{Uri.EscapeDataString(names.First)}", null,
                masterKey, HttpStatusCode.NoContent, token);
            var acceptedStarted = DateTime.UtcNow;
            var reload = await SendAsync(client, HttpMethod.Post, Path + "/cache/reload", null,
                masterKey, HttpStatusCode.Accepted, token);
            var acknowledgement = reload.Body!.Value;
            Properties(acknowledgement, "message", "requestId", "acceptedAt");
            Require(acknowledgement.GetProperty("message").GetString() ==
                "Global settings cache reload was accepted for all Admin and Gateway instances." &&
                acknowledgement.GetProperty("requestId").GetGuid() != Guid.Empty &&
                acknowledgement.GetProperty("acceptedAt").GetDateTime() is { Kind: DateTimeKind.Utc } accepted &&
                accepted >= acceptedStarted && accepted <= DateTime.UtcNow,
                "cache reload protocol acknowledges a nonempty request ID and UTC acceptance timestamp");
            Require(await AllFingerprintAsync(source, names, token) == beforeCache, "cache protocol acknowledgements do not mutate settings");

            await SendAsync(client, HttpMethod.Delete, $"{Path}/{first.Id}", null, masterKey, HttpStatusCode.NoContent, token);
            await SendAsync(client, HttpMethod.Get, $"{Path}/{first.Id}", null, masterKey, HttpStatusCode.NotFound, token);
            await SendAsync(client, HttpMethod.Get, ByKey(first.Key), null, masterKey, HttpStatusCode.NotFound, token);
            await SendAsync(client, HttpMethod.Delete, $"{Path}/{first.Id}", null, masterKey, HttpStatusCode.NotFound, token);
            await SendAsync(client, HttpMethod.Delete, ByKey(second.Key), null, masterKey, HttpStatusCode.NoContent, token);
            await SendAsync(client, HttpMethod.Get, ByKey(second.Key), null, masterKey, HttpStatusCode.NotFound, token);
            await SendAsync(client, HttpMethod.Get, $"{Path}/{second.Id}", null, masterKey, HttpStatusCode.NotFound, token);
            await SendAsync(client, HttpMethod.Delete, ByKey(second.Key), null, masterKey, HttpStatusCode.NotFound, token);
            Require((await ReadRowsAsync(source, names, Selection.Owned, token)).Count == 0, "DELETE by ID/key removes exactly both owned rows");
            await AssertUnrelatedAsync(source, names, unrelated, token);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            // Parent also repeats this cleanup after stopping producers: an Admin
            // request with an unknown HTTP outcome can outlive caller cancellation.
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                await CleanupAsync(source, owner, cleanupDeadline.Token);
                if (unrelated is not null) await AssertUnrelatedAsync(source, names, unrelated, cleanupDeadline.Token);
            }
            catch (Exception cleanup) when (primary is not null)
            {
                throw new AggregateException("Native Admin setting proof and owned cleanup both failed.", primary, cleanup);
            }
        }
        Console.WriteLine("Native Admin global settings HTTP: PASS (master-key authentication, six-field CRUD, merge-patch/upsert semantics, pagination, protected validation and cache acknowledgements).");
    }

    internal static async Task CleanupAsync(NpgsqlDataSource source, Guid owner, CancellationToken ct)
    {
        var names = RequireOwner(source, owner);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var before = Fingerprint(await ReadRowsAsync(connection, transaction, names, Selection.Unrelated, token));
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """DELETE FROM "GlobalSettings" WHERE "Key" = @first OR "Key" = @second""";
            AddNames(command, names);
            Require(await command.ExecuteNonQueryAsync(token) is >= 0 and <= 2, "Admin cleanup deletes only the two exact GUID-owned keys");
        }
        Require((await ReadRowsAsync(connection, transaction, names, Selection.Owned, token)).Count == 0,
            "Admin cleanup leaves no owned setting rows");
        Require(Fingerprint(await ReadRowsAsync(connection, transaction, names, Selection.Unrelated, token)) == before,
            "Admin cleanup preserves every unrelated setting scalar");
        await transaction.CommitAsync(token);
    }

    private static Names RequireOwner(NpgsqlDataSource source, Guid owner)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (owner == Guid.Empty) throw new ArgumentException("A nonempty Admin setting fixture owner GUID is required.", nameof(owner));
        return new Names($"native_admin_{owner:N}:first", $"native_admin_{owner:N}:second");
    }

    private static JsonElement SettingBody(string key, string value, string? description) => Object(writer =>
    {
        writer.WriteString("key", key);
        writer.WriteString("value", value);
        if (description is null) writer.WriteNull("description"); else writer.WriteString("description", description);
    });

    private static JsonElement Object(Action<Utf8JsonWriter>? write = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write?.Invoke(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

    private static string ByKey(string key) => $"{Path}/by-key/{Uri.EscapeDataString(key)}";

    private static async Task<Observation> SendAsync(HttpClient client, HttpMethod method, string path, JsonElement? body,
        string? masterKey, HttpStatusCode expected, CancellationToken ct, bool mergePatch = false, bool bearer = false)
    {
        var correlation = Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("x-request-id", correlation);
        if (masterKey is not null)
        {
            if (bearer) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", masterKey);
            else request.Headers.Add("X-Master-Key", masterKey);
        }
        if (body is { } payload)
        {
            request.Content = JsonContent.Create(payload, NativeProbeJsonContext.Default.JsonElement);
            if (mergePatch) request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/merge-patch+json");
        }
        using var response = await client.SendAsync(request, ct);
        Require(response.StatusCode == expected, $"{method} setting route expected {(int)expected}, received {(int)response.StatusCode}");
        Require(response.Headers.TryGetValues("x-request-id", out var ids) && ids.SequenceEqual([correlation]),
            "Admin response retains the supplied request correlation ID");
        var content = await response.Content.ReadAsStringAsync(ct);
        if (expected == HttpStatusCode.NoContent)
        {
            Require(content.Length == 0, "Admin 204 response has no body");
            return new Observation(null, response.Headers.Location);
        }
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement.Clone();
        if (expected == HttpStatusCode.Unauthorized)
        {
            Require(response.Content.Headers.ContentType?.MediaType == "application/json", "Admin authentication error JSON content type");
            Properties(root, "error", "code");
            Require(root.GetProperty("error").GetString() == "Invalid or missing API key" && root.GetProperty("code").GetInt32() == 401,
                "Admin missing/invalid master-key security error shape");
        }
        else if ((int)expected >= 400)
        {
            Require(response.Content.Headers.ContentType?.MediaType == "application/problem+json", "Admin service error problem content type");
            Properties(root, "type", "title", "status", "detail", "code", "traceId");
            Require(root.GetProperty("status").GetInt32() == (int)expected &&
                root.GetProperty("type").GetString() == $"https://httpstatuses.com/{(int)expected}" &&
                root.GetProperty("code").GetString() == (expected == HttpStatusCode.NotFound ? "not_found" : "validation_error") &&
                root.GetProperty("traceId").GetString() == correlation &&
                !string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()) &&
                !string.IsNullOrWhiteSpace(root.GetProperty("detail").GetString()), "Admin problem classification and correlated trace");
        }
        else Require(response.Content.Headers.ContentType?.MediaType == "application/json", "Admin success JSON content type");
        return new Observation(root, response.Headers.Location);
    }

    private static async Task<Setting> PatchAsync(HttpClient client, NpgsqlDataSource source, Names names, Setting previous,
        JsonElement patch, string value, string? description, string masterKey, CancellationToken ct)
    {
        var response = await SendAsync(client, HttpMethod.Patch, $"{Path}/{previous.Id}", patch, masterKey,
            HttpStatusCode.OK, ct, mergePatch: true);
        var row = await OwnedRowAsync(source, names, previous.Key, ct);
        AssertMutation(row, previous, value, description);
        AssertDto(response.Body!.Value, row);
        return row;
    }

    private static void AssertMutation(Setting row, Setting previous, string value, string? description) =>
        Require(row.Id == previous.Id && row.Key == previous.Key && row.Value == value && row.Description == description &&
            row.CreatedAt == previous.CreatedAt && row.UpdatedAt > previous.UpdatedAt && row.UpdatedAt <= DateTime.UtcNow,
            "setting mutation preserves identity/CreatedAt, applies only defined values and advances UTC UpdatedAt");

    private static void AssertDto(JsonElement dto, Setting row)
    {
        Properties(dto, "id", "key", "value", "description", "createdAt", "updatedAt");
        Require(dto.GetProperty("id").GetInt32() == row.Id && dto.GetProperty("key").GetString() == row.Key &&
            dto.GetProperty("value").GetString() == row.Value && dto.GetProperty("description").GetString() == row.Description &&
            dto.GetProperty("createdAt").GetDateTime() == row.CreatedAt && dto.GetProperty("updatedAt").GetDateTime() == row.UpdatedAt &&
            dto.GetProperty("createdAt").GetDateTime().Kind == DateTimeKind.Utc &&
            dto.GetProperty("updatedAt").GetDateTime().Kind == DateTimeKind.Utc,
            "all six Admin DTO scalars match the independent PostgreSQL row");
    }

    private static async Task AssertGetAsync(HttpClient client, NpgsqlDataSource source, Names names,
        Setting expected, string masterKey, CancellationToken ct)
    {
        var row = await OwnedRowAsync(source, names, expected.Key, ct);
        Require(row == expected, "owned setting is unchanged before ID/key GET");
        foreach (var path in new[] { $"{Path}/{row.Id}", ByKey(row.Key) })
            AssertDto((await SendAsync(client, HttpMethod.Get, path, null, masterKey, HttpStatusCode.OK, ct)).Body!.Value, row);
    }

    private static async Task AssertListAsync(HttpClient client, NpgsqlDataSource source, Names names,
        string masterKey, CancellationToken ct)
    {
        var rows = await ReadRowsAsync(source, names, Selection.Public, ct);
        var pages = new HashSet<(int Page, int Size)> { (1, 50) };
        foreach (var key in new[] { names.First, names.Second })
        {
            var index = rows.FindIndex(row => row.Key == key);
            Require(index >= 0, "owned setting is present in the independent public list");
            pages.Add((index + 1, 1));
        }
        foreach (var (page, size) in pages)
        {
            var suffix = page == 1 && size == 50 ? "/" : $"/?page={page}&pageSize={size}";
            var root = (await SendAsync(client, HttpMethod.Get, Path + suffix, null,
                masterKey, HttpStatusCode.OK, ct)).Body!.Value;
            Properties(root, "data", "pagination");
            var pagination = root.GetProperty("pagination");
            Properties(pagination, "page", "pageSize", "totalItems", "totalPages");
            Require(pagination.GetProperty("page").GetInt32() == page && pagination.GetProperty("pageSize").GetInt32() == size &&
                pagination.GetProperty("totalItems").GetInt32() == rows.Count &&
                pagination.GetProperty("totalPages").GetInt32() == (int)Math.Ceiling(rows.Count / (double)size),
                "Admin pagination reflects the independently ordered public row set");
            var expected = rows.Skip((page - 1) * size).Take(size).ToArray();
            var data = root.GetProperty("data").EnumerateArray().ToArray();
            Require(data.Length == expected.Length, "Admin page has the exact expected number of rows");
            for (var index = 0; index < data.Length; index++) AssertDto(data[index], expected[index]);
        }
    }

    private static async Task<Setting> OwnedRowAsync(NpgsqlDataSource source, Names names, string key, CancellationToken ct)
    {
        var rows = await ReadRowsAsync(source, names, Selection.Owned, ct);
        var matches = rows.Where(row => row.Key == key).ToArray();
        Require(matches.Length == 1, "exactly one owned SQL setting row per key");
        return matches[0];
    }

    private static async Task AssertUnrelatedAsync(NpgsqlDataSource source, Names names, string expected, CancellationToken ct) =>
        Require(Fingerprint(await ReadRowsAsync(source, names, Selection.Unrelated, ct)) == expected,
            "every unrelated setting retains all six original scalars");

    private static async Task<string> AllFingerprintAsync(NpgsqlDataSource source, Names names, CancellationToken ct) =>
        Fingerprint(await ReadRowsAsync(source, names, Selection.All, ct));

    private static async Task<List<Setting>> ReadRowsAsync(NpgsqlDataSource source, Names names, Selection selection, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return await ReadRowsAsync(connection, null, names, selection, ct);
    }

    private static async Task<List<Setting>> ReadRowsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        Names names, Selection selection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var predicate = selection switch
        {
            Selection.Owned => """ WHERE "Key" = @first OR "Key" = @second""",
            Selection.Unrelated => """ WHERE "Key" <> @first AND "Key" <> @second""",
            Selection.Public => """ WHERE "Key" <> @protected""",
            _ => string.Empty
        };
        command.CommandText = "SELECT \"Id\", \"Key\", \"Value\", \"Description\", \"CreatedAt\", \"UpdatedAt\" FROM \"GlobalSettings\"" +
            predicate + " ORDER BY \"Key\", \"Id\"";
        AddNames(command, names);
        command.Parameters.AddWithValue("protected", NpgsqlDbType.Varchar, ProtectedKey);
        await using var reader = await command.ExecuteReaderAsync(ct);
        Require(reader.FieldCount == 6, "independent six-column global-setting SQL oracle");
        var rows = new List<Setting>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Setting(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetDateTime(4), reader.GetDateTime(5));
            Require(row.CreatedAt.Kind == DateTimeKind.Utc && row.UpdatedAt.Kind == DateTimeKind.Utc,
                "SQL setting timestamp types are UTC");
            rows.Add(row);
        }
        return rows;
    }

    private static void AddNames(NpgsqlCommand command, Names names)
    {
        command.Parameters.AddWithValue("first", NpgsqlDbType.Varchar, names.First);
        command.Parameters.AddWithValue("second", NpgsqlDbType.Varchar, names.Second);
    }

    private static string Fingerprint(IEnumerable<Setting> rows)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> identity = stackalloc byte[20];
        foreach (var row in rows.OrderBy(row => row.Id))
        {
            BinaryPrimitives.WriteInt32BigEndian(identity, row.Id);
            BinaryPrimitives.WriteInt64BigEndian(identity[4..], row.CreatedAt.Ticks);
            BinaryPrimitives.WriteInt64BigEndian(identity[12..], row.UpdatedAt.Ticks);
            hash.AppendData(identity);
            Append(hash, row.Key);
            Append(hash, row.Value);
            Append(hash, row.Description);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        var bytes = value is null ? null : Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes?.Length ?? -1);
        hash.AppendData(length);
        if (bytes is not null) hash.AppendData(bytes);
    }

    private static void Properties(JsonElement value, params string[] expected) =>
        Require(value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(property => property.Name)
            .Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)), "exact Admin JSON property set");

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Native Admin global-setting assertion failed: " + name);
    }

    private enum Selection { All, Owned, Unrelated, Public }
    private sealed record Names(string First, string Second);
    private sealed record Setting(int Id, string Key, string Value, string? Description, DateTime CreatedAt, DateTime UpdatedAt);
    private sealed record Observation(JsonElement? Body, Uri? Location);
}
