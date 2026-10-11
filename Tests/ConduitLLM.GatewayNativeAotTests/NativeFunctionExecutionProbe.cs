using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Npgsql;
using NpgsqlTypes;

/// <summary>Exercises real native Gateway function transport, persistence and settled accounting.</summary>
internal static partial class NativeFunctionExecutionProbe
{
    private const string ExecutePath = "v1/conduit/functions/execute";
    private const decimal Price = 0.12500001m;
    private const string ProviderError = """{"error":{"message":"native function upstream private diagnostic","type":"invalid_request_error","code":"native_bad_query"}}""";
    private const string ErrorMessage = "Bad request to Exa API: " + ProviderError;
    private const string ProviderSuccess = """
        {"requestId":"native-function-search","resolvedSearchType":"neural","results":[{"title":"Native search 雪 🔎","url":"https://example.test/native-function","publishedDate":"2026-10-11T00:00:00Z","author":"Native fixture","score":0.875,"text":"Unicode text café 雪","highlights":["snow 雪","café"],"highlightScores":[0.875,0.75],"summary":"Native summary","subpages":[{"title":"Nested 雪","url":"https://example.test/native-function/child","text":"Nested content"}],"extras":{"links":["https://example.test/link"],"imageLinks":["https://example.test/image.png"]}}]}
        """;

    internal static async Task RunAsync(Uri primary, Uri secondary, Uri provider, NpgsqlDataSource source,
        Guid owner, string otherVirtualKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(otherVirtualKey);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(180));
        var token = deadline.Token;
        var fixture = await NativeFunctionFixture.ReadAsync(source, owner, token)
            ?? throw new InvalidOperationException("Native function HTTP fixture is missing.");
        Require(otherVirtualKey != fixture.VirtualKey, "independent virtual key for ownership denial");
        using var first = Client(primary);
        using var second = Client(secondary);
        using var upstream = Client(provider);
        var gateways = new[] { first, second };
        var hosts = new[] { "primary", "secondary" };
        var started = DateTime.UtcNow;
        Require(await CountExecutionsAsync(source, fixture, token) == 0, "pristine owned executions");
        Require(await ReadAccountingAsync(source, fixture, token) == Accounting.Pristine, "pristine separate function accounting");
        await AssertChatConfigurationPreflightAsync(gateways, upstream, source, fixture, token);
        var baseline = await ReadProviderStateAsync(upstream, token);
        var executions = new List<ObservedExecution>();

        for (var index = 0; index < gateways.Length; index++)
        {
            var test = MakeCase(fixture, hosts[index], success: true);
            var resource = await ExecuteAsync(gateways[index], fixture, test, HttpStatusCode.OK, token);
            var id = resource.GetProperty("id").GetGuid();
            Require(id != Guid.Empty && executions.All(execution => execution.Id != id), "unique successful execution ID");
            var row = await ReadExecutionAsync(source, fixture, id, token);
            AssertStoredExecution(row, fixture, test, started);
            AssertResource(resource, row, test, fromPersistence: false);
            var opposite = await GetExecutionAsync(gateways[1 - index], fixture.VirtualKey, id, HttpStatusCode.OK, token);
            AssertResource(opposite, row, test, fromPersistence: true);
            executions.Add(new ObservedExecution(id, test));
        }

        // PostgreSQL logs/ledger retain six decimals, while execution and group amounts retain eight.
        // The ledger may combine multiple requests into one flush, so its sum is the contract.
        var settled = await WaitForSettlementAsync(source, fixture, token);
        await AssertRequestLogsAsync(source, fixture, executions, started, token);

        for (var index = 0; index < gateways.Length; index++)
        {
            var test = MakeCase(fixture, hosts[index], success: false);
            var error = await ExecuteAsync(gateways[index], fixture, test, HttpStatusCode.BadRequest, token);
            AssertProviderError(error);
            var id = await FindExecutionAsync(source, fixture, test.IdempotencyKey, token);
            Require(id != Guid.Empty && executions.All(execution => execution.Id != id), "unique failed execution ID");
            var row = await ReadExecutionAsync(source, fixture, id, token);
            AssertStoredExecution(row, fixture, test, started);
            var opposite = await GetExecutionAsync(gateways[1 - index], fixture.VirtualKey, id, HttpStatusCode.OK, token);
            AssertResource(opposite, row, test, fromPersistence: true);
            executions.Add(new ObservedExecution(id, test));
            Require(await ReadAccountingAsync(source, fixture, token) == settled, "provider rejection and GET stay unbilled");
        }

        foreach (var gateway in gateways)
        {
            foreach (var execution in executions)
                AssertNotFound(await GetExecutionAsync(gateway, otherVirtualKey, execution.Id,
                    HttpStatusCode.NotFound, token), execution.Id);
            var unknown = Guid.NewGuid();
            AssertNotFound(await GetExecutionAsync(gateway, fixture.VirtualKey, unknown, HttpStatusCode.NotFound, token), unknown);
            using var unauthenticated = new HttpRequestMessage(HttpMethod.Get,
                $"v1/conduit/functions/executions/{executions[0].Id}");
            using var challenge = await gateway.SendAsync(unauthenticated, token);
            Require(challenge.StatusCode == HttpStatusCode.Unauthorized, "unauthenticated function execution GET challenge");
        }

        Require(await CountExecutionsAsync(source, fixture, token) == 4, "exactly four owned provider attempts");
        await AssertRequestLogsAsync(source, fixture, executions, started, token);
        // The harness flushes spend every second. Sample through three flush intervals after failures/GETs.
        var stableUntil = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < stableUntil)
        {
            Require(await ReadAccountingAsync(source, fixture, token) == settled, "failed executions and reads do not add delayed spend");
            await Task.Delay(100, token);
        }
        var finalState = await ReadProviderStateAsync(upstream, token);
        Require(finalState.Successes - baseline.Successes == 2 && finalState.Failures - baseline.Failures == 2 &&
            finalState.AuthorizationFailures == baseline.AuthorizationFailures &&
            finalState.ChunkedRequests - baseline.ChunkedRequests == 4,
            "two successful/two failed authenticated real chunked Exa requests");
        Console.WriteLine("Native function HTTP execution: PASS (both Gateways, four persisted 24-field executions, cross-host reads, configured pricing and settled accounting).");
        await AssertAgenticExecutionAsync(gateways, upstream, source, fixture, otherVirtualKey, token);
    }

    private static async Task AssertChatConfigurationPreflightAsync(HttpClient[] gateways, HttpClient provider,
        NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var providerBefore = await ReadCompleteProviderStateAsync(provider, ct);
        // Earlier stream cancellation increments its observed counter before the
        // provider finally decrements the active count. Snapshot only after both.
        while (providerBefore.GetProperty("active_cancellation_requests").GetInt32() != 0)
        {
            await Task.Delay(100, ct);
            providerBefore = await ReadCompleteProviderStateAsync(provider, ct);
        }
        foreach (var gateway in gateways)
        foreach (var path in new[] { "v1/chat/completions", "v1/conduit/chat/completions" })
        {
            using (var anonymous = new HttpRequestMessage(HttpMethod.Post, path))
            {
                anonymous.Content = JsonContent.Create(ChatPreflightBody([fixture.ConfigurationId]),
                    NativeProbeJsonContext.Default.JsonElement);
                using var response = await gateway.SendAsync(anonymous, ct);
                Require(response.StatusCode == HttpStatusCode.Unauthorized, "unauthenticated chat preflight challenge");
            }

            foreach (var (ids, code, expectedMessage) in new (int[], string, string?)[]
                     {
                         ([int.MinValue, NativeParityFixture.DisabledFunctionId, int.MinValue],
                             "invalid_function_configuration_ids", $"Function configuration IDs not found: {int.MinValue}"),
                         ([fixture.ConfigurationId, NativeParityFixture.DisabledFunctionId, fixture.ConfigurationId],
                             "disabled_function_configurations", $"Function configurations are disabled: {NativeParityFixture.DisabledFunctionId}"),
                         ([fixture.ConfigurationId, fixture.ConfigurationId], "invalid_max_agentic_iterations", null)
                     })
            {
                var correlationId = Guid.NewGuid().ToString("N");
                using var request = new HttpRequestMessage(HttpMethod.Post, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.VirtualKey);
                request.Headers.Add("x-request-id", correlationId);
                request.Content = JsonContent.Create(ChatPreflightBody(ids), NativeProbeJsonContext.Default.JsonElement);
                using var response = await gateway.SendAsync(request, ct);
                Require(response.StatusCode == HttpStatusCode.BadRequest,
                    $"native chat configuration rejection on {gateway.BaseAddress!.Authority}/{path} " +
                    $"for {code}: expected 400, received {(int)response.StatusCode}");
                Require(response.Headers.TryGetValues("x-request-id", out var responseIds) &&
                    responseIds.SequenceEqual([correlationId]), "chat preflight retains middleware correlation ID before billing setup");
                var root = await JsonAsync(response, ct);
                Properties(root, "error");
                var error = root.GetProperty("error");
                Properties(error, "message", "type", "code");
                Require(error.GetProperty("code").GetString() == code &&
                    error.GetProperty("type").GetString() == "invalid_request_error", "chat preflight error classification");
                var message = error.GetProperty("message").GetString();
                if (expectedMessage is not null)
                    Require(message == expectedMessage, "missing IDs precede disabled rows, then iteration limits");
                else
                {
                    var parts = message?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    Require(parts is { Length: 7 } && parts[0] == "MaxAgenticIterations" && parts[1] == "must" &&
                        parts[2] == "be" && parts[3] == "between" && parts[5] == "and" &&
                        int.TryParse(parts[4], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minimum) &&
                        int.TryParse(parts[6], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var maximum) &&
                        minimum is >= 1 and <= 100 && maximum is >= 1 and <= 100,
                        "explicit zero iterations use the clamped configured limits after enabled configuration reads");
                }
            }
        }

        // The full host has one-second spend flushes. Rejections must stay unbilled
        // through multiple flush windows, as well as avoiding provider dispatch.
        var stableUntil = DateTime.UtcNow.AddSeconds(3);
        do
        {
            Require(await CountExecutionsAsync(source, fixture, ct) == 0 &&
                await ReadAccountingAsync(source, fixture, ct) == Accounting.Pristine,
                "chat preflight creates no executions, logs, ledger changes or spend");
            Require(JsonElement.DeepEquals(providerBefore, await ReadCompleteProviderStateAsync(provider, ct)),
                "chat preflight leaves all provider counters unchanged");
            await Task.Delay(100, ct);
        } while (DateTime.UtcNow < stableUntil);
        Console.WriteLine("Native chat function configuration preflight: PASS (both routes and Gateways, validation order, authentication, no provider dispatch or accounting).");
    }

    private static JsonElement ChatPreflightBody(int[] ids) => Element(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("model", NativeParityFixture.ModelAlias);
        writer.WriteStartArray("messages");
        writer.WriteStartObject();
        writer.WriteString("role", "user");
        writer.WriteString("content", "Native chat preflight café 雪 🔎");
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteStartArray("function_configuration_ids");
        foreach (var id in ids) writer.WriteNumberValue(id);
        writer.WriteEndArray();
        writer.WriteNumber("max_agentic_iterations", 0);
        writer.WriteBoolean("enable_agentic_mode", false);
        writer.WriteBoolean("stream", false);
        writer.WriteEndObject();
    });

    private static async Task<JsonElement> ReadCompleteProviderStateAsync(HttpClient provider, CancellationToken token)
    {
        using var response = await provider.GetAsync("probe/state", token);
        Require(response.StatusCode == HttpStatusCode.OK, "complete provider counters available");
        return await JsonAsync(response, token);
    }

    private static HttpClient Client(Uri address) => new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    }) { BaseAddress = address, Timeout = Timeout.InfiniteTimeSpan };

    private static ProbeCase MakeCase(NativeFunctionFixtureSnapshot fixture, string host, bool success)
    {
        var marker = $"native-function-{fixture.Owner:N}-{host}-{(success ? "success" : "error")}";
        var query = $"{(success ? "native-function-search" : "native-function-error")} café 雪 🔎 {marker}";
        var parameters = Element(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("query", query);
            writer.WriteStartObject("probe_payload");
            writer.WriteBoolean("enabled", true);
            writer.WriteNumber("count", 2);
            writer.WriteStartArray("items");
            writer.WriteNullValue();
            writer.WriteStringValue("雪 café 🔎");
            writer.WriteNumberValue(Price);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        });
        var metadata = Element(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("owner", fixture.Owner.ToString("N"));
            writer.WriteString("host", host);
            writer.WriteString("marker", marker);
            writer.WriteString("unicode", "café 雪 🔎");
            writer.WriteEndObject();
        });
        var body = Element(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("function_configuration_id", fixture.ConfigurationId);
            writer.WritePropertyName("parameters"); parameters.WriteTo(writer);
            writer.WritePropertyName("metadata"); metadata.WriteTo(writer);
            writer.WriteEndObject();
        });
        var input = Element(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("parameters"); parameters.WriteTo(writer);
            writer.WritePropertyName("metadata"); metadata.WriteTo(writer);
            writer.WriteString("idempotencyKey", marker);
            writer.WriteEndObject();
        });
        return new ProbeCase(success, marker, body, input);
    }

    private static JsonElement Element(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) write(writer);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> ExecuteAsync(HttpClient gateway, NativeFunctionFixtureSnapshot fixture,
        ProbeCase test, HttpStatusCode status, CancellationToken token)
    {
        using var request = Authorized(HttpMethod.Post, ExecutePath, fixture.VirtualKey);
        request.Headers.Add("Idempotency-Key", test.IdempotencyKey);
        request.Content = JsonContent.Create(test.Body, NativeProbeJsonContext.Default.JsonElement);
        using var response = await gateway.SendAsync(request, token);
        Require(response.StatusCode == status, $"function POST status {(int)status}, received {(int)response.StatusCode}");
        return await JsonAsync(response, token);
    }

    private static async Task<JsonElement> GetExecutionAsync(HttpClient gateway, string? virtualKey, Guid id,
        HttpStatusCode status, CancellationToken token)
    {
        using var request = Authorized(HttpMethod.Get, $"v1/conduit/functions/executions/{id}", virtualKey);
        using var response = await gateway.SendAsync(request, token);
        Require(response.StatusCode == status, $"function GET status {(int)status}, received {(int)response.StatusCode}");
        return await JsonAsync(response, token);
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string? key)
    {
        var request = new HttpRequestMessage(method, path);
        if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        Require(response.Content.Headers.ContentType?.MediaType == "application/json", "function HTTP JSON content type");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.Clone();
    }

    private static void AssertProviderError(JsonElement root)
    {
        var error = root.GetProperty("error");
        Require(error.GetProperty("code").GetString() == "provider_request_error" &&
            error.GetProperty("type").GetString() == "invalid_request_error" &&
            error.GetProperty("message").GetString() == ErrorMessage,
            "inherited Exa provider error status, type and diagnostic");
    }

    private static void AssertNotFound(JsonElement root, Guid id)
    {
        var error = root.GetProperty("error");
        Require(error.GetProperty("code").GetString() == "not_found" &&
            error.GetProperty("type").GetString() == "not_found_error" &&
            error.GetProperty("message").GetString() == $"Function execution {id} not found", "execution ownership/missing error");
    }

    private static void AssertStoredExecution(Execution row, NativeFunctionFixtureSnapshot fixture, ProbeCase test, DateTime started)
    {
        Require(row.Id != Guid.Empty && row.ConfigurationId == fixture.ConfigurationId && row.VirtualKeyId == fixture.KeyId &&
            row.Mode == 1 && row.State == (test.Success ? 3 : 4), "stored execution identity, owner, mode and lifecycle");
        Require(row.Requested.Kind == DateTimeKind.Utc && row.Started is { Kind: DateTimeKind.Utc } &&
            row.Completed is { Kind: DateTimeKind.Utc } && row.Requested >= started && row.Requested <= row.Started &&
            row.Started <= row.Completed && row.Completed <= DateTime.UtcNow && row.Duration is { } duration &&
            duration >= TimeSpan.Zero && duration <= TimeSpan.FromSeconds(90), "stored execution coherent UTC times and interval");
        Require(row.RequestJson is not null && SameJson(row.RequestJson, test.Input), "stored JSON-bound parameters, metadata and idempotency header");
        Require(row.Estimated == Price && row.Actual == (test.Success ? Price : 0m) && row.RetryCount == 0 &&
            row.NextRetry is null && row.LeasedBy is null && row.LeaseExpiry is null && row.Version == 2 &&
            row.WebhookUrl is null && !row.WebhookDelivered && row.Progress is null && row.StatusMessage is null,
            "stored exact prices, CAS version and all default/null scalar fields");
        if (test.Success)
        {
            Require(row.ResponseJson is not null && SameJson(row.ResponseJson, Parse(ProviderSuccess)) && row.Error is null,
                "stored rich Unicode/nested Exa response without costDollars");
            Require(row.CostDetails is not null, "stored cost calculation details");
            AssertCostDetails(Parse(row.CostDetails!), row.Duration!.Value);
        }
        else Require(row.ResponseJson is null && row.Error == ErrorMessage && row.CostDetails is null,
            "stored provider rejection has zero actual cost, diagnostic and null response/cost details");
    }

    private static void AssertResource(JsonElement root, Execution row, ProbeCase test, bool fromPersistence)
    {
        Properties(root, "id", "function_id", "status", "input", test.Success ? "output" : "error",
            "created_at", "started_at", "completed_at", "duration_ms", "cost");
        Require(root.GetProperty("id").GetGuid() == row.Id && root.GetProperty("function_id").GetInt32() == row.ConfigurationId &&
            root.GetProperty("status").GetString() == (test.Success ? "completed" : "failed") &&
            JsonElement.DeepEquals(root.GetProperty("input"), test.Input), "function DTO identity, status and structured input");
        var tolerance = fromPersistence ? 0 : 10;
        Require(SameTime(root.GetProperty("created_at").GetDateTime(), row.Requested, tolerance) &&
            SameTime(root.GetProperty("started_at").GetDateTime(), row.Started!.Value, tolerance) &&
            SameTime(root.GetProperty("completed_at").GetDateTime(), row.Completed!.Value, tolerance), "function DTO UTC timestamps match independent SQL");
        var duration = Math.Max(0, (long)Math.Round(row.Duration!.Value.TotalMilliseconds, MidpointRounding.AwayFromZero));
        Require(Math.Abs(root.GetProperty("duration_ms").GetInt64() - duration) <= (fromPersistence ? 0 : 1),
            "function DTO integer duration matches persisted interval precision");
        var cost = root.GetProperty("cost");
        if (test.Success) Properties(cost, "estimated", "actual", "currency", "breakdown");
        else Properties(cost, "estimated", "actual", "currency");
        Require(cost.GetProperty("estimated").GetDecimal() == Price && cost.GetProperty("actual").GetDecimal() == row.Actual &&
            cost.GetProperty("currency").GetString() == "USD", "function DTO exact configured decimal price and currency");
        if (test.Success)
        {
            Require(JsonElement.DeepEquals(root.GetProperty("output"), Parse(ProviderSuccess)) &&
                SameJson(row.CostDetails!, cost.GetProperty("breakdown")), "function DTO preserves structured nested output and cost breakdown");
        }
        else Require(root.GetProperty("error").GetString() == ErrorMessage, "failed function GET preserves inherited diagnostic");
    }

    private static void AssertCostDetails(JsonElement details, TimeSpan duration)
    {
        Properties(details, "usage", "estimatedCost", "actualCost", "httpStatusCode");
        Require(details.GetProperty("estimatedCost").GetDecimal() == Price && details.GetProperty("actualCost").GetDecimal() == Price &&
            details.GetProperty("httpStatusCode").GetInt32() == 200, "configured price and successful upstream cost evidence");
        var usage = details.GetProperty("usage");
        Properties(usage, "result_count", "execution_duration", "search_type", "text_pages_extracted",
            "highlight_pages_extracted", "summary_pages_generated", "metadata");
        Require(usage.GetProperty("result_count").GetInt32() == 1 && usage.GetProperty("search_type").GetString() == "neural" &&
            usage.GetProperty("text_pages_extracted").GetInt32() == 1 && usage.GetProperty("highlight_pages_extracted").GetInt32() == 1 &&
            usage.GetProperty("summary_pages_generated").GetInt32() == 1, "actual top-level Exa usage with nested result retained");
        var providerDuration = TimeSpan.Parse(usage.GetProperty("execution_duration").GetString()!, CultureInfo.InvariantCulture);
        Require(providerDuration >= TimeSpan.Zero && providerDuration <= duration + TimeSpan.FromTicks(10), "provider usage interval fits full execution");
        Properties(usage.GetProperty("metadata"), "requestId");
        Require(usage.GetProperty("metadata").GetProperty("requestId").GetString() == "native-function-search",
            "provider metadata retained without reported-cost evidence");
    }

    private static async Task<Execution> ReadExecutionAsync(NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture,
        Guid id, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id", "FunctionConfigurationId", "VirtualKeyId", "ExecutionMode", "State", "RequestedAt", "StartedAt",
                "CompletedAt", "Duration", "RequestJson", "ResponseJson", "ErrorMessage", "EstimatedCost", "ActualCost",
                "CostCalculationDetails", "RetryCount", "NextRetryAt", "LeasedBy", "LeaseExpiryTime", "Version", "WebhookUrl",
                "WebhookDelivered", "ProgressPercentage", "StatusMessage"
            FROM "FunctionExecutions" WHERE "Id" = @id AND "FunctionConfigurationId" = @configurationId AND "VirtualKeyId" = @keyId
            """;
        AddScope(command, fixture);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, id);
        await using var reader = await command.ExecuteReaderAsync(token);
        Require(reader.FieldCount == 24 && await reader.ReadAsync(token), "independent 24-column persisted execution oracle");
        var row = new Execution(reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
            reader.GetDateTime(5), Date(reader, 6), Date(reader, 7), reader.IsDBNull(8) ? null : reader.GetTimeSpan(8),
            Text(reader, 9), Text(reader, 10), Text(reader, 11), Decimal(reader, 12), Decimal(reader, 13), Text(reader, 14),
            reader.GetInt32(15), Date(reader, 16), Text(reader, 17), Date(reader, 18), reader.GetInt32(19), Text(reader, 20),
            reader.GetBoolean(21), reader.IsDBNull(22) ? null : reader.GetInt32(22), Text(reader, 23));
        Require(!await reader.ReadAsync(token), "one row per provider attempt");
        return row;
    }

    private static async Task<Guid> FindExecutionAsync(NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture,
        string idempotencyKey, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "Id" FROM "FunctionExecutions" WHERE "FunctionConfigurationId" = @configurationId AND "VirtualKeyId" = @keyId
                AND "RequestJson" ->> 'idempotencyKey' = @idempotencyKey
            """;
        AddScope(command, fixture);
        command.Parameters.AddWithValue("idempotencyKey", NpgsqlDbType.Text, idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(token);
        Require(await reader.ReadAsync(token), "provider rejection persisted an execution before responding");
        var id = reader.GetGuid(0);
        Require(!await reader.ReadAsync(token), "one failed execution for each unique header marker");
        return id;
    }

    private static async Task<long> CountExecutionsAsync(NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT count(*) FROM "FunctionExecutions" WHERE "FunctionConfigurationId" = @configurationId OR "VirtualKeyId" = @keyId""";
        AddScope(command, fixture);
        return (long)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task<Accounting> WaitForSettlementAsync(NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        Accounting? latest = null;
        try
        {
            while (true)
            {
                latest = await ReadAccountingAsync(source, fixture, deadline.Token);
                if (latest == Accounting.Settled) return latest;
                Require(latest.LogCount <= 2 && latest.LogCost <= 0.250000m && latest.Spent <= 2 * Price && latest.Debits <= 0.250000m,
                    "function accounting never exceeds the two successful calls");
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Native function accounting did not settle within 60 seconds. Last observed totals: {latest}.");
        }
    }

    private static async Task<Accounting> ReadAccountingAsync(NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture, CancellationToken token)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT count(*) FROM "RequestLogs" WHERE "VirtualKeyId" = @keyId),
                (SELECT COALESCE(sum("Cost"), 0) FROM "RequestLogs" WHERE "VirtualKeyId" = @keyId),
                group_row."Balance", group_row."LifetimeSpent", group_row."LifetimeCreditsAdded",
                (SELECT COALESCE(sum("Amount"), 0) FROM "VirtualKeyGroupTransactions"
                    WHERE "VirtualKeyGroupId" = @groupId AND "TransactionType" = 2 AND NOT "IsDeleted"),
                (SELECT COALESCE(sum("Amount"), 0) FROM "VirtualKeyGroupTransactions"
                    WHERE "VirtualKeyGroupId" = @groupId AND ("TransactionType" <> 2 OR "IsDeleted")),
                COALESCE((SELECT "BalanceAfter" FROM "VirtualKeyGroupTransactions"
                    WHERE "VirtualKeyGroupId" = @groupId ORDER BY "Id" DESC LIMIT 1), 100)
            FROM "VirtualKeyGroups" group_row WHERE group_row."Id" = @groupId
            """;
        command.Parameters.AddWithValue("keyId", NpgsqlDbType.Integer, fixture.KeyId);
        command.Parameters.AddWithValue("groupId", NpgsqlDbType.Integer, fixture.GroupId);
        await using var reader = await command.ExecuteReaderAsync(token);
        Require(await reader.ReadAsync(token), "owned function accounting group exists");
        var result = new Accounting(reader.GetInt64(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3),
            reader.GetDecimal(4), reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7));
        Require(!await reader.ReadAsync(token), "one owned function accounting group");
        return result;
    }

    private static async Task AssertRequestLogsAsync(NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture,
        IReadOnlyList<ObservedExecution> executions, DateTime started, CancellationToken token)
    {
        var expected = executions.Where(execution => execution.Test.Success).Select(execution => execution.Id).ToHashSet();
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "ModelName", "ProviderId", "ProviderType", "RequestType", "InputTokens", "OutputTokens", "CachedInputTokens",
                "CachedWriteTokens", "Cost", "BillingMethod", "ProviderReportedCostUsd", "ProviderCostMarkupMultiplier",
                "RequestPath", "StatusCode", "Metadata", "BilledAtUtc", "Timestamp", "ResponseTimeMs", "ModelProviderMappingId",
                "PromptCachingEligible", "PromptCachingPolicyApplied", "CachedReadSavings", "CacheWritePremium", "RoutingAffinityUsed",
                "RoutingDecisionReason", "RoutingFailoverCount", "UserId", "ClientIp"
            FROM "RequestLogs" WHERE "VirtualKeyId" = @keyId ORDER BY "Id"
            """;
        command.Parameters.AddWithValue("keyId", NpgsqlDbType.Integer, fixture.KeyId);
        await using var reader = await command.ExecuteReaderAsync(token);
        var count = 0;
        while (await reader.ReadAsync(token))
        {
            count++;
            Require(reader.GetString(0) == fixture.ConfigurationName && reader.IsDBNull(1) && reader.GetString(2) == "Exa" &&
                reader.GetString(3) == "function" && reader.GetInt32(4) == 0 && reader.GetInt32(5) == 0 && reader.IsDBNull(6) &&
                reader.IsDBNull(7) && reader.GetDecimal(8) == 0.125000m && reader.GetInt32(9) == 0 && reader.IsDBNull(10) &&
                reader.IsDBNull(11) && reader.GetString(12) == "/" + ExecutePath && reader.GetInt32(13) == 200,
                "success-only function request logs, fresh zero-token usage and configured price at six-decimal precision");
            var metadata = Parse(reader.GetString(14));
            Properties(metadata, "functionConfigurationId", "executionId", "state");
            Require(metadata.GetProperty("functionConfigurationId").GetInt32() == fixture.ConfigurationId &&
                expected.Remove(metadata.GetProperty("executionId").GetGuid()) && metadata.GetProperty("state").GetString() == "Completed",
                "request log links each successful execution once with Pascal-case lifecycle metadata");
            var billed = reader.GetDateTime(15);
            Require(billed.Kind == DateTimeKind.Utc && billed >= started && billed <= DateTime.UtcNow && billed == reader.GetDateTime(16) &&
                reader.GetDouble(17) >= 0 && reader.IsDBNull(18) && !reader.GetBoolean(19) && !reader.GetBoolean(20) &&
                reader.GetDecimal(21) == 0m && reader.GetDecimal(22) == 0m && !reader.GetBoolean(23) && reader.IsDBNull(24) &&
                reader.GetInt32(25) == 0 && reader.GetString(26) == $"native_function_{fixture.Owner:N}:key" &&
                reader.GetString(27) == "127.0.0.1", "function request-log timestamps and inherited unrelated-usage defaults");
        }
        Require(count == 2 && expected.Count == 0, "exactly the two successful executions produced request logs");
    }

    private static async Task<ProviderState> ReadProviderStateAsync(HttpClient provider, CancellationToken token)
    {
        using var response = await provider.GetAsync("probe/state", token);
        Require(response.StatusCode == HttpStatusCode.OK, "native function provider counters available");
        var root = await JsonAsync(response, token);
        return new ProviderState(root.GetProperty("function_requests").GetInt32(), root.GetProperty("function_error_requests").GetInt32(),
            root.GetProperty("function_authorization_failures").GetInt32(), root.GetProperty("function_chunked_requests").GetInt32());
    }

    private static void AddScope(NpgsqlCommand command, NativeFunctionFixtureSnapshot fixture)
    {
        command.Parameters.AddWithValue("configurationId", NpgsqlDbType.Integer, fixture.ConfigurationId);
        command.Parameters.AddWithValue("keyId", NpgsqlDbType.Integer, fixture.KeyId);
    }

    private static DateTime? Date(NpgsqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetDateTime(index);
    private static string? Text(NpgsqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static decimal? Decimal(NpgsqlDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetDecimal(index);
    private static JsonElement Parse(string json) { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
    private static bool SameJson(string json, JsonElement expected) => JsonElement.DeepEquals(Parse(json), expected);
    private static bool SameTime(DateTime actual, DateTime expected, int ticks) =>
        actual.Kind == DateTimeKind.Utc && Math.Abs(actual.Ticks - expected.Ticks) <= ticks;
    private static void Properties(JsonElement element, params string[] names) =>
        Require(element.ValueKind == JsonValueKind.Object && element.EnumerateObject().Select(property => property.Name)
            .Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)), "exact structured JSON properties");
    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Native function HTTP assertion failed: " + name);
    }

    private sealed record ProbeCase(bool Success, string IdempotencyKey, JsonElement Body, JsonElement Input);
    private sealed record ObservedExecution(Guid Id, ProbeCase Test);
    private sealed record Execution(Guid Id, int ConfigurationId, int VirtualKeyId, int Mode, int State, DateTime Requested,
        DateTime? Started, DateTime? Completed, TimeSpan? Duration, string? RequestJson, string? ResponseJson, string? Error,
        decimal? Estimated, decimal? Actual, string? CostDetails, int RetryCount, DateTime? NextRetry, string? LeasedBy,
        DateTime? LeaseExpiry, int Version, string? WebhookUrl, bool WebhookDelivered, int? Progress, string? StatusMessage);
    private sealed record Accounting(long LogCount, decimal LogCost, decimal Balance, decimal Spent, decimal Credits,
        decimal Debits, decimal OtherTransactions, decimal LedgerBalance)
    {
        internal static readonly Accounting Pristine = new(0, 0m, 100m, 0m, 100m, 0m, 0m, 100m);
        internal static readonly Accounting Settled = new(2, 0.250000m, 99.74999998m, 0.25000002m, 100m, 0.250000m, 0m, 99.750000m);
    }
    private readonly record struct ProviderState(int Successes, int Failures, int AuthorizationFailures, int ChunkedRequests);
}
