using System.Text.Json;

using ConduitLLM.Admin.DTOs;
using ConduitLLM.Admin.Serialization;

namespace ConduitLLM.SerializationTests;

public sealed class AdminRuntimeCapabilitySerializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedMetadataPreservesFiveSnakeCaseFieldsAndAllArrays(bool native)
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        string[] included = native
            ? [
                "postgresql-wolverine-persistence",
                "master-key-and-bearer-fallback-authentication",
                "typed-global-settings-http",
                "typed-ip-filter-http-and-enforcement",
                "typed-global-settings-cache",
                "typed-billing-audit-host",
                "release-owned-schema-wait",
                "readiness-and-database-health",
                "liveness-metrics-opentelemetry"
            ]
            : [
                "postgresql-wolverine-persistence",
                "master-key-and-bearer-fallback-authentication",
                "global-settings-http",
                "ip-filter-http-and-enforcement",
                "global-settings-cache",
                "billing-audit-host",
                "release-owned-schema-wait",
                "readiness-and-database-health",
                "liveness-metrics-opentelemetry",
                "ef-backed-admin-management",
                "pricing-tools-and-audit",
                "model-cost-canary",
                "startup-pricing-configuration-audit",
                "media-cleanup-and-reconciliation",
                "media-storage-startup-guard",
                "openrouter-metadata-sync",
                "database-count-operations-metrics",
                "billing-audit-reads-and-retention"
            ];
        string[] excluded = native
            ? [
                "ef-backed-admin-management",
                "pricing-tools-and-audit",
                "model-cost-canary",
                "startup-pricing-configuration-audit",
                "media-cleanup-and-reconciliation",
                "media-storage-startup-guard",
                "openrouter-metadata-sync",
                "database-count-operations-metrics",
                "billing-audit-reads-and-retention"
            ]
            : [];
        var expected = new AdminRuntimeCapabilitiesResponse(native ? "native-aot" : "jit", [],
            native ? "typed-npgsql-selected-runtime-slices" : "ef-core-supported", included, excluded);
        var metadata = AdminHttpJsonContext.Default.AdminRuntimeCapabilitiesResponse;

        var json = JsonSerializer.Serialize(expected, metadata);
        using var document = JsonDocument.Parse(json);
        var body = document.RootElement;
        Assert.Equal(JsonValueKind.Object, body.ValueKind);
        Assert.Equal(new[] { "excluded_features", "included_features", "persistence_status", "runtime_mode", "signal_r_protocols" },
            body.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(expected.RuntimeMode, body.GetProperty("runtime_mode").GetString());
        Assert.Equal(expected.PersistenceStatus, body.GetProperty("persistence_status").GetString());
        AssertStrings(body.GetProperty("signal_r_protocols"), expected.SignalRProtocols);
        AssertStrings(body.GetProperty("included_features"), included);
        AssertStrings(body.GetProperty("excluded_features"), excluded);

        var restored = JsonSerializer.Deserialize(json, metadata);
        Assert.NotNull(restored);
        Assert.Equal(expected.RuntimeMode, restored.RuntimeMode);
        Assert.Equal(expected.PersistenceStatus, restored.PersistenceStatus);
        Assert.Empty(restored.SignalRProtocols);
        Assert.Equal(included, restored.IncludedFeatures);
        Assert.Equal(excluded, restored.ExcludedFeatures);
    }

    private static void AssertStrings(JsonElement values, string[] expected)
    {
        Assert.Equal(JsonValueKind.Array, values.ValueKind);
        var entries = values.EnumerateArray().ToArray();
        Assert.All(entries, value => Assert.Equal(JsonValueKind.String, value.ValueKind));
        Assert.Equal(expected, entries.Select(value => value.GetString()));
    }
}
