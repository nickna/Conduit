using System.Text.Json;

/// <summary>Exact counter oracles shared by the native fixture and its fast HTTP contract tests.</summary>
internal static class NativeProviderStateAssertions
{
    private static readonly string[] ProviderCounters =
    [
        "non_stream_requests", "stream_requests", "image_requests", "error_requests", "authorization_failures",
        "active_cancellation_requests", "cancellations_observed", "function_requests", "function_error_requests",
        "function_authorization_failures", "function_chunked_requests", "agentic_tool_call_requests",
        "agentic_final_requests", "agentic_contract_failures", "responses_proof_requests"
    ];

    internal static void AssertQuietSnapshot(JsonElement value)
    {
        Properties(value, ProviderCounters);
        foreach (var name in ProviderCounters)
            Require(value.GetProperty(name).GetInt64() >= 0, "provider counters are nonnegative integers");
        Require(value.GetProperty("active_cancellation_requests").GetInt64() == 0,
            "no unrelated provider request is active during the boundary proof");
    }

    internal static void AssertAgenticDelta(JsonElement before, JsonElement after)
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["non_stream_requests"] = 0, ["stream_requests"] = 0, ["image_requests"] = 0,
            ["error_requests"] = 0, ["authorization_failures"] = 0, ["active_cancellation_requests"] = 0,
            ["cancellations_observed"] = 0, ["function_requests"] = 4, ["function_error_requests"] = 0,
            ["function_authorization_failures"] = 0, ["function_chunked_requests"] = 4,
            ["agentic_tool_call_requests"] = 4, ["agentic_final_requests"] = 4, ["agentic_contract_failures"] = 0,
            ["responses_proof_requests"] = 0
        };
        Properties(before, expected.Keys.ToArray());
        Properties(after, expected.Keys.ToArray());
        foreach (var (name, delta) in expected)
        {
            var previous = before.GetProperty(name).GetInt32();
            var current = after.GetProperty(name).GetInt32();
            Require(previous >= 0 && current >= 0 && current - previous == delta,
                "exact nonnegative agentic provider counter delta for " + name);
        }
    }

    private static void Properties(JsonElement value, params string[] names) => Require(
        value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(property => property.Name)
            .Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal)),
        "provider counter property set is exact and unique");

    private static void Require(bool condition, string invariant)
    {
        if (!condition) throw new InvalidOperationException("Native provider state: " + invariant + ".");
    }
}
