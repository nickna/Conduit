using System.Globalization;
using System.Net;
using System.Text.Json;

using Npgsql;
using NpgsqlTypes;

internal static partial class NativeFunctionExecutionProbe
{
    internal static async Task RunMcpAsync(Uri primary, Uri secondary, NpgsqlDataSource source,
        Guid mcpOwner, string protectedKey, Guid exaOwner, string otherVirtualKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(secondary);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(otherVirtualKey);
        Require(mcpOwner != Guid.Empty && exaOwner != Guid.Empty && mcpOwner != exaOwner,
            "independent nonempty MCP and Exa fixture owners");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var exa = await NativeFunctionFixture.ReadAsync(source, exaOwner, token)
            ?? throw new InvalidOperationException("The independent Exa function fixture is missing.");
        var exaExecutions = await CountExecutionsAsync(source, exa, token);
        var exaAccounting = await ReadAccountingAsync(source, exa, token);
        await using var stub = new NativeMcpProviderStub(authValue: "Bearer " + NativeFunctionFixture.ApiKey);
        var fixture = await NativeFunctionFixture.CreateAsync(source, mcpOwner, stub.Endpoint, protectedKey,
            NativeFunctionFixtureProfile.Mcp, token);
        Require(fixture.Profile == NativeFunctionFixtureProfile.Mcp && fixture.VirtualKey != exa.VirtualKey &&
            fixture.VirtualKey != otherVirtualKey && exa.VirtualKey != otherVirtualKey,
            "separate MCP, Exa and baseline virtual keys");
        using var first = Client(primary);
        using var second = Client(secondary);
        var gateways = new[] { first, second };
        var hosts = new[] { "primary", "secondary" };
        var output = McpExpectedOutput();
        // This is the independent compact producer representation, before jsonb
        // adds spaces or reorders properties. MCP usage counts its UTF-16 length.
        var responseChars = output.GetRawText().Length;
        var executions = new List<ObservedExecution>();
        var started = DateTime.UtcNow;
        Require(await CountExecutionsAsync(source, fixture, token) == 0 &&
            await ReadAccountingAsync(source, fixture, token) == Accounting.Pristine,
            "pristine separate MCP execution/accounting fixture");

        for (var index = 0; index < gateways.Length; index++)
        {
            var test = MakeMcpCase(fixture, hosts[index]);
            var resource = await ExecuteAsync(gateways[index], fixture, test, HttpStatusCode.OK, token);
            var id = resource.GetProperty("id").GetGuid();
            Require(id != Guid.Empty && executions.All(execution => execution.Id != id), "unique MCP execution ID");
            var row = await ReadExecutionAsync(source, fixture, id, token);
            AssertMcpStoredExecution(row, fixture, test, output, responseChars, started);
            AssertMcpResource(resource, row, test, output, fromPersistence: false);
            var opposite = await GetExecutionAsync(gateways[1 - index], fixture.VirtualKey, id, HttpStatusCode.OK, token);
            AssertMcpResource(opposite, row, test, output, fromPersistence: true);
            executions.Add(new ObservedExecution(id, test));
            await stub.WaitForIdleAsync(token);
            Require(stub.LastCallArguments is { } arguments &&
                JsonElement.DeepEquals(arguments, McpArguments(test.Body.GetProperty("parameters"))),
                "MCP SDK receives bound Unicode/nested argument types with routing key removed");
            AssertMcpProtocolMetadata(stub.LastRequestMetadata);
            Require(await CountExecutionsAsync(source, exa, token) == exaExecutions &&
                await ReadAccountingAsync(source, exa, token) == exaAccounting,
                "MCP execution and opposite-host GET preserve independent Exa rows and accounting");
        }

        var settled = await WaitForSettlementAsync(source, fixture, token);
        await AssertMcpRequestLogsAsync(source, fixture, executions, started, token);
        await stub.WaitForIdleAsync(token);
        var counters = stub.Counters;
        Require(counters.DiscoverRequests == 2 && counters.CallRequests == 2 && counters.ListRequests == 0 &&
            counters.AuthorizationFailures == 0 && counters.ProtocolFailures == 0 && counters.ChunkedRequests == 4 &&
            counters.CancellationNotifications == 0 && counters.ActiveHandlers == 0 && counters.HeldRequests == 0 &&
            counters.AcceptedConnections == 4,
            "two authenticated modern MCP discover/call sessions use four real chunked TCP requests without extra operations");

        foreach (var gateway in gateways)
        {
            foreach (var execution in executions)
            {
                foreach (var foreignKey in new[] { otherVirtualKey, exa.VirtualKey })
                {
                    AssertNotFound(await GetExecutionAsync(gateway, foreignKey, execution.Id,
                        HttpStatusCode.NotFound, token), execution.Id);
                }
            }
            var unknown = Guid.NewGuid();
            AssertNotFound(await GetExecutionAsync(gateway, fixture.VirtualKey, unknown,
                HttpStatusCode.NotFound, token), unknown);
            using var anonymous = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get,
                $"v1/conduit/functions/executions/{executions[0].Id}");
            using var challenge = await gateway.SendAsync(anonymous, token);
            Require(challenge.StatusCode == HttpStatusCode.Unauthorized, "unauthenticated MCP execution GET challenge");
        }

        // Observe several one-second spend flushes after all ownership/read probes.
        var stableUntil = DateTime.UtcNow.AddSeconds(3);
        do
        {
            Require(await CountExecutionsAsync(source, fixture, token) == 2 &&
                await ReadAccountingAsync(source, fixture, token) == settled,
                "MCP cross-host/ownership/missing/anonymous reads add no executions, logs or delayed spend");
            Require(await CountExecutionsAsync(source, exa, token) == exaExecutions &&
                await ReadAccountingAsync(source, exa, token) == exaAccounting,
                "MCP proof preserves Exa execution count and settled accounting through all flush windows");
            await stub.WaitForIdleAsync(token);
            Require(stub.Counters == counters, "MCP reads and ownership denials never reconnect to the upstream");
            await Task.Delay(100, token);
        } while (DateTime.UtcNow < stableUntil);
        await AssertMcpRequestLogsAsync(source, fixture, executions, started, token);
        Console.WriteLine("Native MCP inline function HTTP execution: PASS (both Gateways, encrypted scoped credential, two persisted 24-field executions, cross-host ownership, configured pricing and settled accounting).");
    }

    private static ProbeCase MakeMcpCase(NativeFunctionFixtureSnapshot fixture, string host)
    {
        var marker = $"native-mcp-{fixture.Owner:N}-{host}";
        var parameters = Element(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("query", $"café 雪 🔎 {marker}");
            writer.WriteNumber("limit", 3);
            writer.WriteBoolean("enabled", true);
            writer.WriteNull("optional");
            writer.WriteStartObject("nested");
            writer.WriteStartArray("items");
            writer.WriteStringValue("雪");
            writer.WriteNumberValue(2);
            writer.WriteBooleanValue(false);
            writer.WriteNullValue();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteString("__conduit_mcp_tool", "native_search");
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
        return new ProbeCase(true, marker, body, input);
    }

    private static JsonElement McpArguments(JsonElement parameters) => Element(writer =>
    {
        writer.WriteStartObject();
        foreach (var property in parameters.EnumerateObject().Where(property => property.Name != "__conduit_mcp_tool"))
        {
            property.WriteTo(writer);
        }
        writer.WriteEndObject();
    });

    private static JsonElement McpExpectedOutput() => Element(writer =>
    {
        writer.WriteStartObject();
        writer.WriteBoolean("isError", false);
        writer.WriteStartArray("content");
        writer.WriteStringValue("Native MCP result café 雪 🔎");
        writer.WriteEndArray();
        writer.WriteNumber("omittedNonTextBlocks", 1);
        writer.WritePropertyName("structuredContent");
        Parse("""{"unicode":"café 雪 🔎","items":[null,true,2,{"name":"nested 雪"}]}""").WriteTo(writer);
        writer.WriteEndObject();
    });

    private static void AssertMcpProtocolMetadata(JsonElement? snapshot)
    {
        Require(snapshot is { ValueKind: JsonValueKind.Object }, "MCP captured per-request metadata");
        var metadata = snapshot!.Value;
        Properties(metadata, "io.modelcontextprotocol/protocolVersion", "io.modelcontextprotocol/clientInfo",
            "io.modelcontextprotocol/clientCapabilities");
        Require(metadata.GetProperty("io.modelcontextprotocol/protocolVersion").GetString() == "2026-07-28" &&
            metadata.GetProperty("io.modelcontextprotocol/clientInfo").ValueKind == JsonValueKind.Object &&
            metadata.GetProperty("io.modelcontextprotocol/clientCapabilities").ValueKind == JsonValueKind.Object,
            "modern MCP SDK request carries protocol, client identity and capabilities");
    }

    private static void AssertMcpStoredExecution(Execution row, NativeFunctionFixtureSnapshot fixture, ProbeCase test,
        JsonElement output, int responseChars, DateTime started)
    {
        Require(row.Id != Guid.Empty && row.ConfigurationId == fixture.ConfigurationId && row.VirtualKeyId == fixture.KeyId &&
            row.Mode == 1 && row.State == 3, "MCP persisted execution identity, owner and completed sync lifecycle");
        Require(row.Requested.Kind == DateTimeKind.Utc && row.Started is { Kind: DateTimeKind.Utc } &&
            row.Completed is { Kind: DateTimeKind.Utc } && row.Requested >= started && row.Requested <= row.Started &&
            row.Started <= row.Completed && row.Completed <= DateTime.UtcNow && row.Duration is { } duration &&
            duration >= TimeSpan.Zero && duration <= TimeSpan.FromSeconds(90), "MCP persisted coherent UTC times and interval");
        Require(row.RequestJson is not null && SameJson(row.RequestJson, test.Input),
            "MCP persisted original JSON parameters including routing key, caller metadata and idempotency header");
        Require(row.ResponseJson is not null && SameJson(row.ResponseJson, output) && row.Error is null,
            "MCP persisted Unicode text, nested structured content and binary omission without provider dollars");
        Require(row.Estimated == Price && row.Actual == Price && row.RetryCount == 0 && row.NextRetry is null &&
            row.LeasedBy is null && row.LeaseExpiry is null && row.Version == 2 && row.WebhookUrl is null &&
            !row.WebhookDelivered && row.Progress is null && row.StatusMessage is null,
            "MCP persisted configured exact price, CAS version and every default/null scalar");
        Require(row.CostDetails is not null, "MCP persisted cost calculation details");
        AssertMcpCostDetails(Parse(row.CostDetails!), row.Duration!.Value, responseChars);
    }

    private static void AssertMcpCostDetails(JsonElement details, TimeSpan duration, int responseChars)
    {
        Properties(details, "usage", "estimatedCost", "actualCost");
        Require(details.GetProperty("estimatedCost").GetDecimal() == Price && details.GetProperty("actualCost").GetDecimal() == Price,
            "MCP configured pricing is used despite absent provider HTTP status/cost signals");
        var usage = details.GetProperty("usage");
        Properties(usage, "result_count", "execution_duration", "metadata");
        Require(usage.GetProperty("result_count").GetInt32() == 1, "MCP usage counts one successful invocation");
        var providerDuration = TimeSpan.Parse(usage.GetProperty("execution_duration").GetString()!, CultureInfo.InvariantCulture);
        Require(providerDuration >= TimeSpan.Zero && providerDuration <= duration + TimeSpan.FromTicks(10),
            "MCP usage duration fits the full execution");
        var metadata = usage.GetProperty("metadata");
        Properties(metadata, "responseChars");
        Require(metadata.GetProperty("responseChars").GetInt32() == responseChars,
            "MCP usage retains original compact producer response length before jsonb normalization");
    }

    private static void AssertMcpResource(JsonElement root, Execution row, ProbeCase test, JsonElement output, bool fromPersistence)
    {
        Properties(root, "id", "function_id", "status", "input", "output", "created_at", "started_at", "completed_at", "duration_ms", "cost");
        Require(root.GetProperty("id").GetGuid() == row.Id && root.GetProperty("function_id").GetInt32() == row.ConfigurationId &&
            root.GetProperty("status").GetString() == "completed" && JsonElement.DeepEquals(root.GetProperty("input"), test.Input) &&
            JsonElement.DeepEquals(root.GetProperty("output"), output), "MCP DTO identity/lifecycle and complete structured input/output");
        var tolerance = fromPersistence ? 0 : 10;
        Require(SameTime(root.GetProperty("created_at").GetDateTime(), row.Requested, tolerance) &&
            SameTime(root.GetProperty("started_at").GetDateTime(), row.Started!.Value, tolerance) &&
            SameTime(root.GetProperty("completed_at").GetDateTime(), row.Completed!.Value, tolerance), "MCP DTO UTC timestamps match SQL precision");
        var duration = Math.Max(0, (long)Math.Round(row.Duration!.Value.TotalMilliseconds, MidpointRounding.AwayFromZero));
        Require(Math.Abs(root.GetProperty("duration_ms").GetInt64() - duration) <= (fromPersistence ? 0 : 1),
            "MCP DTO rounded duration matches independent SQL interval");
        var cost = root.GetProperty("cost");
        Properties(cost, "estimated", "actual", "currency", "breakdown");
        Require(cost.GetProperty("estimated").GetDecimal() == Price && cost.GetProperty("actual").GetDecimal() == Price &&
            cost.GetProperty("currency").GetString() == "USD" && SameJson(row.CostDetails!, cost.GetProperty("breakdown")),
            "MCP DTO configured decimal price and complete structured usage breakdown");
    }

    private static async Task AssertMcpRequestLogsAsync(NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture,
        IReadOnlyList<ObservedExecution> executions, DateTime started, CancellationToken token)
    {
        var expected = executions.Select(execution => execution.Id).ToHashSet();
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
        Require(reader.FieldCount == 28, "independent 28-column MCP request-log oracle");
        var count = 0;
        while (await reader.ReadAsync(token))
        {
            count++;
            Require(reader.GetString(0) == fixture.ConfigurationName && reader.IsDBNull(1) && reader.GetString(2) == "Mcp" &&
                reader.GetString(3) == "function" && reader.GetInt32(4) == 0 && reader.GetInt32(5) == 0 && reader.IsDBNull(6) &&
                reader.IsDBNull(7) && reader.GetDecimal(8) == Price && reader.GetInt32(9) == 0 && reader.IsDBNull(10) &&
                reader.IsDBNull(11) && reader.GetString(12) == "/" + ExecutePath && reader.GetInt32(13) == 200,
                "MCP function request-log identity, zero token usage and exact eight-decimal configured cost");
            var metadata = Parse(reader.GetString(14));
            Properties(metadata, "functionConfigurationId", "executionId", "state");
            Require(metadata.GetProperty("functionConfigurationId").GetInt32() == fixture.ConfigurationId &&
                expected.Remove(metadata.GetProperty("executionId").GetGuid()) && metadata.GetProperty("state").GetString() == "Completed",
                "MCP request log links each successful execution exactly once");
            var billed = reader.GetDateTime(15);
            Require(billed.Kind == DateTimeKind.Utc && billed >= started && billed <= DateTime.UtcNow && billed == reader.GetDateTime(16) &&
                reader.GetDouble(17) >= 0 && reader.IsDBNull(18) && !reader.GetBoolean(19) && !reader.GetBoolean(20) &&
                reader.GetDecimal(21) == 0m && reader.GetDecimal(22) == 0m && !reader.GetBoolean(23) && reader.IsDBNull(24) &&
                reader.GetInt32(25) == 0 && reader.GetString(26) == $"native_function_{fixture.Owner:N}:key" &&
                reader.GetString(27) == "127.0.0.1", "MCP request-log UTC billing, caching/routing and ownership defaults");
        }
        Require(count == 2 && expected.Count == 0, "only the two successful MCP POSTs produced owned request logs");
    }
}
