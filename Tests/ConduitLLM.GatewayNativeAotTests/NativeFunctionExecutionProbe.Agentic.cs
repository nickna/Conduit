using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Npgsql;
using NpgsqlTypes;

internal static partial class NativeFunctionExecutionProbe
{
    private const decimal AgenticRequestPrice = 0.12515001m;
    private static readonly Accounting AgenticSettled =
        new(6, 0.75060006m, 99.24939994m, 0.75060006m, 100m, 0.75060006m, 0m, 99.24939994m);

    private static async Task AssertAgenticExecutionAsync(HttpClient[] gateways, HttpClient provider,
        NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture, string otherVirtualKey, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        Require(await CountExecutionsAsync(source, fixture, ct) == 4 &&
            await ReadAccountingAsync(source, fixture, ct) == Accounting.Settled,
            "agentic proof starts after the four inline attempts and their separate settled accounting");
        var baseline = await ReadCompleteProviderStateAsync(provider, ct);
        var started = DateTime.UtcNow;
        var observed = new List<AgenticObservedExecution>();
        var identities = new HashSet<Guid> { fixture.Owner };
        var paths = new[] { "v1/chat/completions", "v1/conduit/chat/completions" };

        for (var host = 0; host < gateways.Length; host++)
        foreach (var path in paths)
        {
            var marker = Guid.NewGuid();
            var callerId = Guid.NewGuid();
            Require(identities.Add(marker) && identities.Add(callerId), "unique agentic prompt and caller identities");
            var body = AgenticChatBody(fixture, marker);
            using var request = Authorized(HttpMethod.Post, path, fixture.VirtualKey);
            request.Headers.Add("x-request-id", callerId.ToString("N"));
            request.Headers.Add("Idempotency-Key", $"native-agentic-{fixture.Owner:N}-{marker:N}");
            request.Content = JsonContent.Create(body, NativeProbeJsonContext.Default.JsonElement);
            using var response = await gateways[host].SendAsync(request, ct);
            Require(response.StatusCode == HttpStatusCode.OK,
                $"agentic chat on host {host} route {path} returned {(int)response.StatusCode}");
            Require(response.Headers.TryGetValues("x-request-id", out var headerIds), "agentic response billing identity");
            var headerId = headerIds!.Single();
            Require(Guid.TryParseExact(headerId, "N", out var billingId) && billingId != Guid.Empty && identities.Add(billingId),
                "fresh HTTP billing GUID differs from owner, prompt and caller identities");
            var root = await JsonAsync(response, ct);
            var (executionId, summaryDuration) = AssertAgenticChatResponse(root, fixture, marker);
            Require(executionId != Guid.Empty && identities.Add(executionId), "unique execution ID for each agentic chat");
            var row = await ReadExecutionAsync(source, fixture, executionId, ct);
            var test = AssertAgenticInput(row, body, marker, billingId, identities);
            AssertStoredExecution(row, fixture, test, started);
            Require(row.Duration!.Value.TotalMilliseconds <= summaryDuration + 1 && summaryDuration is >= 0 and <= 90000,
                "function summary duration includes the persisted successful execution");
            var opposite = await GetExecutionAsync(gateways[1 - host], fixture.VirtualKey,
                executionId, HttpStatusCode.OK, ct);
            AssertResource(opposite, row, test, fromPersistence: true);
            foreach (var gateway in gateways)
                AssertNotFound(await GetExecutionAsync(gateway, otherVirtualKey, executionId,
                    HttpStatusCode.NotFound, ct), executionId);
            observed.Add(new AgenticObservedExecution(executionId, path, marker, billingId, callerId));
        }

        var settled = await WaitForAgenticSettlementAsync(source, fixture, ct);
        Require(await CountExecutionsAsync(source, fixture, ct) == 8,
            "exactly eight owned attempts after four configured-function agentic requests");
        await AssertAgenticRequestLogsAsync(source, fixture, observed, started, ct);
        AssertAgenticProviderCounters(baseline, await ReadCompleteProviderStateAsync(provider, ct));

        // Reads and the earlier provider failures must remain unbilled after delayed flushes.
        var stableUntil = DateTime.UtcNow.AddSeconds(3);
        do
        {
            Require(await ReadAccountingAsync(source, fixture, ct) == settled &&
                await CountExecutionsAsync(source, fixture, ct) == 8,
                "agentic ownership/read requests add no delayed logs, executions or spend");
            AssertAgenticProviderCounters(baseline, await ReadCompleteProviderStateAsync(provider, ct));
            await Task.Delay(100, ct);
        } while (DateTime.UtcNow < stableUntil);
        await AssertAgenticRequestLogsAsync(source, fixture, observed, started, ct);
        Console.WriteLine("Native configured-function agentic HTTP: PASS (both routes and Gateways, four OpenAI-to-Exa loops, persisted executions, cross-host ownership and settled combined billing).");
    }

    private static JsonElement AgenticChatBody(NativeFunctionFixtureSnapshot fixture, Guid marker) => Element(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("model", NativeParityFixture.ModelAlias);
        writer.WriteStartArray("messages");
        writer.WriteStartObject();
        writer.WriteString("role", "user");
        writer.WriteString("content", NativeProviderStub.AgenticPromptPrefix + marker.ToString("N"));
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteStartArray("function_configuration_ids");
        writer.WriteNumberValue(fixture.ConfigurationId);
        writer.WriteEndArray();
        writer.WriteBoolean("enable_agentic_mode", true);
        writer.WriteNumber("max_agentic_iterations", 2);
        writer.WriteBoolean("stream", false);
        writer.WriteEndObject();
    });

    private static (Guid Id, long Duration) AssertAgenticChatResponse(JsonElement root,
        NativeFunctionFixtureSnapshot fixture, Guid marker)
    {
        Properties(root, "id", "object", "created", "model", "choices", "usage", "agentic_metrics", "performance_metrics");
        Require(root.GetProperty("id").GetString() == $"chatcmpl-native-agentic-{marker:N}-final" &&
            root.GetProperty("object").GetString() == "chat.completion" && root.GetProperty("created").GetInt64() == 1787800010 &&
            root.GetProperty("model").GetString() == NativeParityFixture.ModelAlias,
            "final provider response identity, timestamp and original model alias");
        var choices = root.GetProperty("choices");
        Require(choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() == 1, "one final agentic choice");
        var choice = choices[0];
        Properties(choice, "index", "message", "finish_reason");
        Require(choice.GetProperty("index").GetInt32() == 0 && choice.GetProperty("finish_reason").GetString() == "stop",
            "second model iteration finishes without pending tools");
        var message = choice.GetProperty("message");
        Properties(message, "role", "content");
        Require(message.GetProperty("role").GetString() == "assistant" &&
            message.GetProperty("content").GetString() == NativeProviderStub.AgenticFinalContent,
            "final structured Unicode assistant content");
        var usage = root.GetProperty("usage");
        Properties(usage, "prompt_tokens", "completion_tokens", "total_tokens");
        Require(usage.GetProperty("prompt_tokens").GetInt32() == 20 && usage.GetProperty("completion_tokens").GetInt32() == 10 &&
            usage.GetProperty("total_tokens").GetInt32() == 30, "two provider-call usage records aggregate exactly");

        var metrics = root.GetProperty("agentic_metrics");
        Properties(metrics, "total_iterations", "total_function_calls", "total_function_cost", "total_llm_cost", "total_cost", "function_calls");
        Require(metrics.GetProperty("total_iterations").GetInt32() == 2 && metrics.GetProperty("total_function_calls").GetInt32() == 1 &&
            metrics.GetProperty("total_function_cost").GetDecimal() == Price && metrics.GetProperty("total_llm_cost").GetDecimal() == 0m &&
            metrics.GetProperty("total_cost").GetDecimal() == Price,
            "agentic metrics retain current function-only total and zero LLM cost");
        var calls = metrics.GetProperty("function_calls");
        Require(calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() == 1, "one configured function summary");
        var call = calls[0];
        Properties(call, "iteration", "tool_call_id", "function_name", "function_configuration_id", "function_execution_id", "success", "cost", "duration_ms");
        Require(call.GetProperty("iteration").GetInt32() == 1 &&
            call.GetProperty("tool_call_id").GetString() == "call_native_" + marker.ToString("N") &&
            call.GetProperty("function_name").GetString() == $"native_function_{fixture.Owner:N}exa" &&
            call.GetProperty("function_configuration_id").GetInt32() == fixture.ConfigurationId &&
            call.GetProperty("success").GetBoolean() && call.GetProperty("cost").GetDecimal() == Price,
            "successful owned function summary identifies the first actual provider iteration");

        var performance = root.GetProperty("performance_metrics");
        // Nonstream performance measures the final provider call, before aggregate usage is assigned.
        var hasThroughput = performance.TryGetProperty("tokens_per_second", out var throughput);
        Properties(performance, hasThroughput
            ? ["total_latency_ms", "tokens_per_second", "provider", "model", "streaming"]
            : ["total_latency_ms", "provider", "model", "streaming"]);
        Require(performance.GetProperty("total_latency_ms").GetInt64() >= 0 &&
            performance.GetProperty("provider").GetString() == "openai" &&
            performance.GetProperty("model").GetString() == NativeParityFixture.ModelAlias &&
            !performance.GetProperty("streaming").GetBoolean() &&
            (!hasThroughput || double.IsFinite(throughput.GetDouble()) && throughput.GetDouble() > 0),
            "final provider-call nonstream performance metrics retain current generated JSON contract");
        return (call.GetProperty("function_execution_id").GetGuid(), call.GetProperty("duration_ms").GetInt64());
    }

    private static ProbeCase AssertAgenticInput(Execution row, JsonElement body, Guid marker, Guid billingId, HashSet<Guid> identities)
    {
        Require(row.RequestJson is not null, "agentic execution persisted request JSON");
        var input = Parse(row.RequestJson!);
        Properties(input, "parameters", "metadata", "idempotencyKey");
        var parameters = Element(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("query", NativeProviderStub.AgenticQueryPrefix + marker.ToString("N") + " café 雪 🔎");
            writer.WriteEndObject();
        });
        Require(JsonElement.DeepEquals(input.GetProperty("parameters"), parameters), "original generated Unicode tool arguments reach persistence unchanged");
        var metadata = input.GetProperty("metadata");
        Properties(metadata, "chat_completion_id", "tool_call_id", "iteration_number", "request_id");
        var chatIdText = metadata.GetProperty("chat_completion_id").GetString();
        var internalIdText = metadata.GetProperty("request_id").GetString();
        Require(Guid.TryParseExact(chatIdText, "D", out var chatId) && chatId != Guid.Empty && chatId != billingId && identities.Add(chatId),
            "fresh internal chat GUID differs from HTTP billing, owner and all previous identities");
        Require(Guid.TryParseExact(internalIdText, "D", out var internalId) && internalId != Guid.Empty && internalId != billingId && identities.Add(internalId),
            "fresh internal request GUID differs from chat and HTTP identities");
        var toolId = "call_native_" + marker.ToString("N");
        Require(metadata.GetProperty("tool_call_id").GetString() == toolId && metadata.GetProperty("iteration_number").GetInt32() == 1,
            "persisted metadata links the first actual model iteration to its requested tool");
        var idempotencyKey = chatId.ToString("D") + "_" + toolId;
        Require(input.GetProperty("idempotencyKey").GetString() == idempotencyKey,
            "agentic execution idempotency uses generated chat identity and tool ID");
        // Build the independent expected input from known arguments and independently validated IDs.
        var expected = Element(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("parameters"); parameters.WriteTo(writer);
            writer.WriteStartObject("metadata");
            writer.WriteString("chat_completion_id", chatId.ToString("D"));
            writer.WriteString("tool_call_id", toolId);
            writer.WriteNumber("iteration_number", 1);
            writer.WriteString("request_id", internalId.ToString("D"));
            writer.WriteEndObject();
            writer.WriteString("idempotencyKey", idempotencyKey);
            writer.WriteEndObject();
        });
        return new ProbeCase(true, idempotencyKey, body, expected);
    }

    private static async Task<Accounting> WaitForAgenticSettlementAsync(NpgsqlDataSource source,
        NativeFunctionFixtureSnapshot fixture, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        Accounting? latest = null;
        try
        {
            while (true)
            {
                latest = await ReadAccountingAsync(source, fixture, deadline.Token);
                if (latest == AgenticSettled) return latest;
                Require(latest.LogCount <= AgenticSettled.LogCount && latest.LogCost <= AgenticSettled.LogCost &&
                    latest.Spent <= AgenticSettled.Spent && latest.Debits <= AgenticSettled.Debits &&
                    latest.Balance >= AgenticSettled.Balance && latest.Credits == 100m && latest.OtherTransactions == 0m,
                    "combined model and function billing never exceeds four successful agentic requests");
                await Task.Delay(100, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Native agentic accounting did not settle within 60 seconds. Last observed totals: {latest}.");
        }
    }

    private static async Task AssertAgenticRequestLogsAsync(NpgsqlDataSource source, NativeFunctionFixtureSnapshot fixture,
        IReadOnlyList<AgenticObservedExecution> executions, DateTime started, CancellationToken token)
    {
        Require(executions.Count == 4 && executions.Select(execution => execution.Id).Distinct().Count() == 4 &&
            executions.Select(execution => execution.Marker).Distinct().Count() == 4,
            "four distinct agentic requests and persisted executions");
        var paths = executions.GroupBy(execution => "/" + execution.Path).ToDictionary(group => group.Key, group => group.Count());
        var expected = executions.ToDictionary(execution => execution.Id);
        await using var connection = await source.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT "ModelName", "ProviderId", "ProviderType", "RequestType", "InputTokens", "OutputTokens", "CachedInputTokens",
                "CachedWriteTokens", "Cost", "BillingMethod", "ProviderReportedCostUsd", "ProviderCostMarkupMultiplier",
                "RequestPath", "StatusCode", "Metadata", "BilledAtUtc", "Timestamp", "ResponseTimeMs", "ModelProviderMappingId",
                "PromptCachingEligible", "PromptCachingPolicyApplied", "CachedReadSavings", "CacheWritePremium", "RoutingAffinityUsed",
                "RoutingDecisionReason", "RoutingFailoverCount", "UserId", "ClientIp"
            FROM "RequestLogs" WHERE "VirtualKeyId" = @keyId AND "RequestType" = 'chat' ORDER BY "Id"
            """;
        command.Parameters.AddWithValue("keyId", NpgsqlDbType.Integer, fixture.KeyId);
        await using var reader = await command.ExecuteReaderAsync(token);
        Require(reader.FieldCount == 28, "independent complete chat request-log scalar oracle");
        var count = 0;
        while (await reader.ReadAsync(token))
        {
            count++;
            Require(reader.GetString(0) == NativeParityFixture.ModelAlias && reader.GetInt32(1) == -9001 &&
                reader.GetString(2) == "OpenAI" && reader.GetString(3) == "chat" && reader.GetInt32(4) == 20 &&
                reader.GetInt32(5) == 10 && reader.IsDBNull(6) && reader.IsDBNull(7) &&
                reader.GetDecimal(8) == AgenticRequestPrice && reader.GetInt32(9) == 0 &&
                reader.IsDBNull(10) && reader.IsDBNull(11) && reader.GetInt32(13) == 200 && !reader.IsDBNull(14),
                "each chat logs aggregate tokens, exact combined configured model/function price and generated function metadata");
            var metadata = Parse(reader.GetString(14));
            Properties(metadata, "type", "functionCallCount", "totalCost", "successCount", "failedCount",
                "functionCalls", "billingRequestId", "requestId", "costBreakdown");
            Require(metadata.GetProperty("type").GetString() == "chat_with_functions" &&
                metadata.GetProperty("functionCallCount").GetInt32() == 1 && metadata.GetProperty("totalCost").GetDecimal() == Price &&
                metadata.GetProperty("successCount").GetInt32() == 1 && metadata.GetProperty("failedCount").GetInt32() == 0,
                "request-log function metadata retains the distinct configured-function total");
            var calls = metadata.GetProperty("functionCalls");
            Require(calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() == 1,
                "one persisted configured-function event per parent chat");
            var call = calls[0];
            Properties(call, "iteration", "tool_call_id", "function_name", "status", "cost", "function_execution_id");
            Require(expected.TryGetValue(call.GetProperty("function_execution_id").GetGuid(), out var execution) &&
                expected.Remove(execution!.Id), "each chat log independently joins one observed execution exactly once");
            var observedExecution = execution!;
            Require(call.GetProperty("iteration").GetInt32() == 1 &&
                call.GetProperty("tool_call_id").GetString() == "call_native_" + observedExecution.Marker.ToString("N") &&
                call.GetProperty("function_name").GetString() == $"native_function_{fixture.Owner:N}exa" &&
                call.GetProperty("status").GetString() == "completed" && call.GetProperty("cost").GetDecimal() == Price &&
                metadata.GetProperty("billingRequestId").GetString() == observedExecution.BillingId.ToString("N") &&
                metadata.GetProperty("requestId").GetString() == observedExecution.CallerId.ToString("N"),
                "stored event links actual provider iteration, execution, billing and original caller identities");
            var costs = metadata.GetProperty("costBreakdown");
            Properties(costs, "llmCost", "functionCost", "providerToolCost", "totalCost");
            Require(costs.GetProperty("llmCost").GetDecimal() == 0.000150m &&
                costs.GetProperty("functionCost").GetDecimal() == Price && costs.GetProperty("providerToolCost").GetDecimal() == 0m &&
                costs.GetProperty("totalCost").GetDecimal() == AgenticRequestPrice,
                "stored billing breakdown records both provider-call token usage and configured function cost without hosted tools");
            var path = reader.GetString(12);
            Require(path == "/" + observedExecution.Path, "stored billing metadata execution joins its actual HTTP route");
            Require(paths.TryGetValue(path, out var remaining) && remaining > 0, "each actual chat route logged exactly twice");
            paths[path] = remaining - 1;
            var billed = reader.GetDateTime(15);
            Require(billed.Kind == DateTimeKind.Utc && billed >= started && billed <= DateTime.UtcNow &&
                billed == reader.GetDateTime(16) && reader.GetDouble(17) >= 0 && reader.GetInt32(18) == -9007 &&
                reader.GetBoolean(19) && !reader.GetBoolean(20) && reader.GetDecimal(21) == 0m &&
                reader.GetDecimal(22) == 0m && !reader.GetBoolean(23) && reader.GetString(24) == "balanced_score" &&
                reader.GetInt32(25) == 0 && reader.GetString(26) == $"native_function_{fixture.Owner:N}:key" &&
                reader.GetString(27) == "127.0.0.1",
                $"chat request-log timestamps, routing identity, caching and ownership defaults " +
                $"(utc={billed.Kind}, started={billed >= started}, timestamp={billed == reader.GetDateTime(16)}, " +
                $"latency={reader.GetDouble(17)}, mapping={(reader.IsDBNull(18) ? null : reader.GetInt32(18))}, " +
                $"eligible={reader.GetBoolean(19)}, applied={reader.GetBoolean(20)}, " +
                $"savings={reader.GetDecimal(21)}, premium={reader.GetDecimal(22)}, " +
                $"affinity={reader.GetBoolean(23)}, reason={(reader.IsDBNull(24) ? null : reader.GetString(24))}, " +
                $"failovers={reader.GetInt32(25)}, userMatches={reader.GetString(26) == $"native_function_{fixture.Owner:N}:key"}, " +
                $"ipMatches={reader.GetString(27) == "127.0.0.1"})");
        }
        Require(count == 4 && expected.Count == 0 && paths.Values.All(remaining => remaining == 0),
            "exactly four agentic chat logs across both aliases and all observed executions");
    }

    private static void AssertAgenticProviderCounters(JsonElement before, JsonElement after)
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["non_stream_requests"] = 0, ["stream_requests"] = 0, ["image_requests"] = 0,
            ["error_requests"] = 0, ["authorization_failures"] = 0, ["active_cancellation_requests"] = 0,
            ["cancellations_observed"] = 0, ["function_requests"] = 4, ["function_error_requests"] = 0,
            ["function_authorization_failures"] = 0, ["function_chunked_requests"] = 4,
            ["agentic_tool_call_requests"] = 4, ["agentic_final_requests"] = 4, ["agentic_contract_failures"] = 0
        };
        Properties(before, expected.Keys.ToArray());
        Properties(after, expected.Keys.ToArray());
        foreach (var (name, delta) in expected)
            Require(after.GetProperty(name).GetInt32() - before.GetProperty(name).GetInt32() == delta,
                "exact agentic provider counter delta for " + name);
    }

    private sealed record AgenticObservedExecution(Guid Id, string Path, Guid Marker, Guid BillingId, Guid CallerId);
}
